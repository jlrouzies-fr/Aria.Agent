using System.Text.Json;
using Aria.Harness.Governance;
using Aria.Web.Helpers;
using Aria.Web.Services.Chat;
using Aria.Web.Services;
using Markdig;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OpenAI.Chat;

namespace Aria.Web.Components.Pages;

public partial class Chat
{
    // ── Screenshot lightbox ──────────────────────────────────────────────
    // Full-size data-URL of the currently open inline screenshot, or null when no modal is showing.

    private string? _imageModalSrc;

    private void OpenImageModal(string? mediaType, string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return;
        _imageModalSrc = $"data:{mediaType ?? "image/png"};base64,{base64}";
    }

    private void CloseImageModal() => _imageModalSrc = null;

    // The blinking block cursor means "the agent is doing something but nothing is visibly
    // changing right now". It shows before the very first token, and it reappears whenever the
    // most recent section is tool activity or a todo list — those render as a static card/list
    // (no char-by-char growth), so without the cursor a running tool, or the gap between one
    // tool call finishing and the next one (or the final answer) starting, looks like nothing is
    // happening. It stays hidden while content or thinking text is actively streaming in, since
    // the growing text itself already conveys activity.
    private static bool ShouldShowStreamingCursor(MessageEntry msg)
    {
        var last = msg.Sections.LastOrDefault();
        if (last is null) return true;
        if (last.Type is MessageSection.SectionType.ToolActivity or MessageSection.SectionType.TodoList) return true;
        // A brand-new message always has an empty placeholder Content section (added by the
        // MessageEntry constructor) — that's not "output" yet, just an unfilled slot, so it must
        // count the same as no sections at all.
        return string.IsNullOrEmpty(last.Text);
    }

    // ── Chat timeline rail ────────────────────────────────────────────────

    private static string MessagePreview(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "(empty)";
        var clean = content.Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
        const int max = 60;
        return clean.Length > max ? clean[..max].TrimEnd() + "…" : clean;
    }

    private static string TimelineTip(MessageEntry m) =>
        $"{m.Timestamp:MMM d, HH:mm} — {MessagePreview(m.Content)}";

    private async Task ScrollToMessage(int index)
    {
        try { await JS.InvokeVoidAsync("ariaInterop.scrollToElement", $"msg-{index}"); }
        catch { }
    }

    // Keep the URL in sync with the open cogitation so a refresh reopens it.
    // Guard against navigating to the URL we're already on — during server prerender that
    // turns into an HTTP self-redirect loop (ERR_TOO_MANY_REDIRECTS).
    private void SyncChatUrl(int? id)
    {
        var target = id.HasValue ? $"/chat/{id.Value}" : "/chat";
        if (!string.Equals(Nav.Uri, Nav.ToAbsoluteUri(target).ToString(), StringComparison.OrdinalIgnoreCase))
            Nav.NavigateTo(target, forceLoad: false, replace: true);
    }

    // ── Markdown ──────────────────────────────────────────────────────────

    // Matches the exact "// CONTEXT APPROVAL REQUIRED — ... //" note CogitationRunRegistry appends to
    // an assistant reply when a turn halts on ContextApprovalRequiredException. The real actionable UI
    // is the .approval-bar--context panel pinned above the input; this inline marker just needs to read
    // as a badge instead of plain code-fence-less text sitting in the transcript.
    private static readonly System.Text.RegularExpressions.Regex _contextApprovalNoteRx = new(
        @"//\s*CONTEXT APPROVAL REQUIRED\s*—\s*approve sensitive operations on your node to continue\.\s*//",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string RenderMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (!_contextApprovalNoteRx.IsMatch(text)) return MarkdownHelper.ToHtml(text);

        // MarkdownHelper's pipelines run with .DisableHtml() (XSS guard), so a literal badge <span>
        // injected pre-render would just get HTML-encoded. Same placeholder-token swap as
        // RenderUserMarkdown's #path pills: token survives Markdig untouched, then gets replaced with
        // the real badge markup afterward. Inline <span>, not <div> — Markdig wraps the line in <p>,
        // and a block element there would produce invalid (if browser-tolerated) nesting.
        var badges = new List<string>();
        var token = "APPROVALBADGE" + Guid.NewGuid().ToString("N");
        var withPlaceholders = _contextApprovalNoteRx.Replace(text, _ =>
        {
            badges.Add(
                "<span class=\"inline-approval-badge\">" +
                    "<span class=\"inline-approval-badge-icon\">⛨</span>" +
                    "<span class=\"inline-approval-badge-text\">CONTEXT APPROVAL REQUIRED — approve sensitive operations on your node to continue.</span>" +
                "</span>");
            return $"{token}{badges.Count - 1}ENDBADGE";
        });

        var html = MarkdownHelper.ToHtml(withPlaceholders);
        for (var i = 0; i < badges.Count; i++)
            html = html.Replace($"{token}{i}ENDBADGE", badges[i]);
        return html;
    }

    // Matches a "#path" reference at the start or after whitespace (path has no spaces/#).
    private static readonly System.Text.RegularExpressions.Regex _refPillRx =
        new(@"(?<=^|\s)#([^\s#]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    // User messages: turn "#path" references into visible pills. MarkdownHelper's pipelines run
    // with .DisableHtml() (intentionally — it stops raw HTML/XSS from user or LLM text), so a
    // literal <span> injected pre-render would just get HTML-encoded and shown as text. Instead,
    // swap in an inert placeholder token before Markdig runs, then substitute the real pill HTML
    // into the rendered output afterward.
    private static string RenderUserMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var pills = new List<string>();
        var token = "PILLTOKEN" + Guid.NewGuid().ToString("N");
        var withPlaceholders = _refPillRx.Replace(text, m =>
        {
            pills.Add($"<span class=\"ref-pill\">⊡ {System.Net.WebUtility.HtmlEncode(m.Groups[1].Value)}</span>");
            return $"{token}{pills.Count - 1}ENDPILL";
        });

        var html = MarkdownHelper.ToHtml(withPlaceholders);
        for (var i = 0; i < pills.Count; i++)
            html = html.Replace($"{token}{i}ENDPILL", pills[i]);
        return html;
    }

    // ── Message section helpers ───────────────────────────────────────────

    private static readonly System.Text.RegularExpressions.Regex _excessNewlines =
        new(@"\n{3,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string CollapseNewlines(string s) =>
        string.IsNullOrEmpty(s) ? s : _excessNewlines.Replace(s, "\n\n");

    // ── Copy answer ───────────────────────────────────────────────────────
    // Hover actions on completed assistant turns: COPY (plain text) and MD (raw markdown).

    private int?    _copyFeedbackIndex;
    private string? _copyFeedbackKind;   // "plain" | "md"
    private CancellationTokenSource? _copyFeedbackCts;

    private static bool CanCopyAnswer(MessageEntry msg) =>
        msg.Role == "assistant" && !string.IsNullOrWhiteSpace(msg.Content);

    private async Task CopyAnswerAsync(int index, bool asMarkdown)
    {
        if (index < 0 || index >= _messages.Count) return;
        var msg = _messages[index];
        if (!CanCopyAnswer(msg) || msg == _streamingMsg) return;

        var markdown = msg.Content;
        var text = asMarkdown ? markdown : MarkdownToPlainText(markdown);
        try
        {
            await JS.InvokeVoidAsync("ariaInterop.copyText", text);
        }
        catch
        {
            return;
        }

        _copyFeedbackIndex = index;
        _copyFeedbackKind  = asMarkdown ? "md" : "plain";
        StateHasChanged();

        _copyFeedbackCts?.Cancel();
        _copyFeedbackCts?.Dispose();
        _copyFeedbackCts = new CancellationTokenSource();
        var ct = _copyFeedbackCts.Token;
        _ = InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(1500, ct);
                if (_copyFeedbackIndex == index)
                {
                    _copyFeedbackIndex = null;
                    _copyFeedbackKind  = null;
                    StateHasChanged();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Renders markdown to HTML then strips tags — readable plain text for the clipboard.</summary>
    private static string MarkdownToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        // Use SafePipeline directly (not MarkdownHelper.ToHtml) so code-block COPY buttons
        // are not baked into the HTML we are about to strip.
        var html = global::Markdig.Markdown.ToHtml(markdown, MarkdownHelper.SafePipeline);
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+\r?\n", "\n");
        text = _excessNewlines.Replace(text, "\n\n");
        return text.Trim();
    }

    // Collapse every thinking section of a message — called once the agent finishes cogitating
    // (content begins, streaming ends, or when loading history). User can re-expand by clicking.
    private static void CollapseThinking(MessageEntry m)
    {
        foreach (var s in m.Sections)
            if (s.Type == MessageSection.SectionType.Thinking)
                s.Collapsed = true;
    }

    private static string FormatToolArgs(string name, string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("command",   out var cmd))  return "$ " + cmd.GetString();
            if (root.TryGetProperty("path",      out var path)) return path.GetString() ?? argsJson;
            if (root.TryGetProperty("file_path", out var fp))   return fp.GetString() ?? argsJson;
            if (root.TryGetProperty("directory", out var dir))  return dir.GetString() ?? argsJson;
            if (root.TryGetProperty("url",       out var url))  return url.GetString() ?? argsJson;
            if (root.TryGetProperty("query",     out var q))    return q.GetString() ?? argsJson;
            if (root.TryGetProperty("content",   out var cont)) return (cont.GetString() ?? "")
                .Split('\n').FirstOrDefault() ?? "";
        }
        catch { }
        return argsJson.Length > 120 ? argsJson[..120] + "…" : argsJson;
    }

    private static string PreviewResult(string result)
    {
        // JSON array → show as filename list (list_dir, search results, etc.)
        try
        {
            using var doc = JsonDocument.Parse(result);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var items = doc.RootElement.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String
                        ? e.GetString()
                        : e.TryGetProperty("name", out var n) ? n.GetString()
                        : e.ToString())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Take(12)
                    .ToList();
                var preview = string.Join('\n', items);
                return doc.RootElement.GetArrayLength() > 12
                    ? preview + $"\n… (+{doc.RootElement.GetArrayLength() - 12} more)"
                    : preview;
            }
        }
        catch { }

        // Plain text → first 8 non-empty lines
        var lines = result.ReplaceLineEndings("\n").Split('\n')
            .SkipWhile(string.IsNullOrWhiteSpace)
            .ToArray();
        var shown = lines.Take(8).ToArray();
        var text  = string.Join('\n', shown);
        return lines.Length > 8 ? text + "\n…" : text;
    }

    private static readonly System.Text.Json.JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    private static FileMutationMetadata? ParseFileMutationMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<FileMutationMetadata>(metadataJson, MetadataJsonOptions);
        }
        catch { return null; }
    }

    private async Task OpenDiffFileAsync(string absPath)
    {
        var project = SessionState.Projects.FirstOrDefault(p => absPath.StartsWith(p.Path.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(absPath, p.Path, StringComparison.OrdinalIgnoreCase));
        if (project != null)
            await SetActiveProjectAsync(project);

        if (_explorerCollapsed)
        {
            _explorerCollapsed = false;
            if (project != null && _explorerLoadedForPath != project.Path)
                _ = LoadExplorerTreeAsync();
        }

        _viewerOpen = true;
        _viewerRelPath = project != null ? Path.GetRelativePath(project.Path, absPath).Replace('\\', '/') : Path.GetFileName(absPath);
        _viewerAbsPath = absPath;
        _viewerContent = null;
        _viewerLoading = true;
        _viewerTruncated = false;
        await InvokeAsync(StateHasChanged);

        var userId = BridgeUserId();
        var result = userId != null
            ? await ProjectFiles.ReadFileAsync(userId, absPath, SessionState.AllowedProjectPaths, nodeId: project?.NodeId, sessionId: SessionState.SessionToken)
            : null;
        _viewerContent = result?.Content;
        _viewerTruncated = result?.Truncated ?? false;
        _viewerLoading = false;
        await InvokeAsync(StateHasChanged);
        await SaveExplorerStateAsync();
    }



    private sealed record FileMutationMetadata(
        string Kind,
        string Path,
        string? Destination,
        string? Diff,
        int Adds,
        int Dels,
        string UndoToken,
        string? Checkpoint,
        bool Created,
        bool Deleted,
        bool Reverted = false);

    private async Task RevertDiffAsync(MessageEntry msg, ToolCallInfo tc)
    {
        var userId = BridgeUserId();
        if (userId == null) return;

        var metadata = ParseFileMutationMetadata(tc.MetadataJson);
        if (metadata is null) return;

        var result = await ProjectFiles.RevertAsync(userId, metadata.UndoToken, SessionState.AllowedProjectPaths, sessionId: SessionState.SessionToken);
        if (result == null)
        {
            _attachError = "// REVERT FAILED: bridge returned no response";
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (result.HashMismatch)
        {
            var confirmed = await JS.InvokeAsync<bool>("confirm", "The file has changed since this edit. Force revert anyway?");
            if (!confirmed) return;
            result = await ProjectFiles.RevertAsync(userId, metadata.UndoToken, SessionState.AllowedProjectPaths, force: true, sessionId: SessionState.SessionToken);
            if (result == null || !result.Reverted)
            {
                _attachError = "// REVERT FAILED: forced revert did not complete";
                await InvokeAsync(StateHasChanged);
                return;
            }
        }

        if (!result.Reverted)
        {
            _attachError = "// REVERT FAILED: could not restore file";
            await InvokeAsync(StateHasChanged);
            return;
        }

        // Persist the reverted flag into the message's metadata so it survives reload.
        var updatedMetadata = metadata with { Reverted = true };
        tc.MetadataJson = System.Text.Json.JsonSerializer.Serialize(updatedMetadata, MetadataJsonOptions);

        var persisted = await PersistToolCardStateAsync(msg, userId);

        if (!persisted)
        {
            _attachError = "// REVERT OK (state not persisted — reload may show the card as active)";
        }

        // Refresh the diff card's reverted state via the explorer auto-refresh path.
        var refreshArgs = System.Text.Json.JsonSerializer.Serialize(new { path = metadata.Path });
        await HandleFileToolCompletedAsync("write_file", refreshArgs);
        await InvokeAsync(StateHasChanged);
    }

    private async Task<bool> PersistToolCardStateAsync(MessageEntry msg, string userId)
    {
        var sectionsJson = System.Text.Json.JsonSerializer.Serialize(msg.Sections, SectionJsonOptions);
        if (_cogitationId.HasValue)
        {
            if (_cogitationOriginNodeId == null && msg.DbMessageId.HasValue)
                return await CogitationService.UpdateMessageSectionsAsync(_cogitationId.Value, msg.DbMessageId.Value, sectionsJson);
            if (_cogitationOriginNodeId != null && !string.IsNullOrEmpty(msg.BridgeMessageId))
                return await BridgeCogitation.UpdateMessageAsync(userId, _cogitationId.Value, msg.BridgeMessageId, sectionsJson, _cogitationOriginNodeId);
        }

        return false;
    }

    private static string FormatResult(string toolName, string argsJson, string result)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;

            if (toolName == "write_file" && root.TryGetProperty("content", out var wc))
            {
                var content = wc.GetString() ?? "";
                var lines   = content.ReplaceLineEndings("\n").Split('\n');
                var preview = string.Join('\n', lines.Take(12));
                return lines.Length > 12 ? preview + $"\n… ({lines.Length} lines total)" : preview;
            }

            if (toolName == "edit_file")
            {
                var oldStr   = root.TryGetProperty("old_string", out var os) ? os.GetString() ?? "" : "";
                var newStr   = root.TryGetProperty("new_string", out var ns) ? ns.GetString() ?? "" : "";
                var oldLines = oldStr.ReplaceLineEndings("\n").Split('\n').Take(6).Select(l => "- " + l);
                var newLines = newStr.ReplaceLineEndings("\n").Split('\n').Take(6).Select(l => "+ " + l);
                return string.Join('\n', oldLines) + "\n" + string.Join('\n', newLines);
            }
        }
        catch { }

        if (toolName is "list_dir" or "list_directory")
        {
            try
            {
                using var doc = JsonDocument.Parse(result);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var entries = doc.RootElement.EnumerateArray().Select(e =>
                    {
                        var name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
                        return type == "dir" ? name + "/" : name;
                    });
                    return string.Join("\n", entries);
                }
            }
            catch { }
        }

        return PreviewResult(result);
    }

    private static void ToggleToolBlock(ToolCallInfo tc) => tc.Expanded = !tc.Expanded;

    // ── Streaming token callbacks ─────────────────────────────────────────

    private void OnToolStart(string name, string args) =>
        _ = InvokeAsync(() =>
        {
            if (_streamingMsg != null)
            {
                _streamingMsg.Sections.Add(new MessageSection
                {
                    Type     = MessageSection.SectionType.ToolActivity,
                    ToolCall = new ToolCallInfo { Name = name, ArgsJson = args }
                });
            }
            _smartScrollPending = true;
            StateHasChanged();
        });

    private void OnToolComplete(string name, string result, string? imageBase64 = null, string? imageMediaType = null, string? metadataJson = null) =>
        _ = InvokeAsync(async () =>
        {
            string? argsJson = null;
            if (_streamingMsg != null)
            {
                var toolSection = _streamingMsg.Sections.LastOrDefault(s =>
                    s.Type == MessageSection.SectionType.ToolActivity &&
                    s.ToolCall?.Name == name &&
                    s.ToolCall?.Result == null);
                if (toolSection != null)
                {
                    // A tool that produces an image (e.g. TakeScreenshot) carries it on the ToolCall
                    // itself — Chat.razor renders it inline, right at this section's position in the
                    // reply's own timeline, rather than as a separate message tacked onto the end.
                    toolSection.ToolCall!.Result        = result;
                    toolSection.ToolCall.ImageBase64    = imageBase64;
                    toolSection.ToolCall.ImageMediaType = imageMediaType;
                    toolSection.ToolCall.MetadataJson   = metadataJson;
                    argsJson = toolSection.ToolCall.ArgsJson;
                }
            }
            _smartScrollPending = true;
            StateHasChanged();

            if (argsJson != null)
                await HandleFileToolCompletedAsync(name, argsJson);
        });

    // The agent posted/updated its task manifest. A single manifest is pinned at the bottom of
    // the chat (above the composer) so directives stay in view as the conversation scrolls; each
    // update replaces it in place.
    private void OnTodoUpdate(IReadOnlyList<Aria.Tools.TodoItem> todos) =>
        _ = InvokeAsync(() =>
        {
            _currentManifest = NormalizeTodos(todos, _currentManifest);
            UpdateManifestCollapse();
            StateHasChanged();
        });

    // Normalize incoming manifest items so CSS classes and glyph matching agree on snake_case
    // statuses, and drop directives with no visible text (the agent sometimes emits a leading
    // empty entry that renders as a blank checkbox line). Small models also send status-only
    // updates — entries whose text is blank inherit the previous manifest's text by position.
    private static List<Aria.Tools.TodoItem> NormalizeTodos(IEnumerable<Aria.Tools.TodoItem> todos, IReadOnlyList<Aria.Tools.TodoItem>? previous = null)
    {
        var list = todos.ToList();
        return list
            .Select((t, i) => new Aria.Tools.TodoItem
            {
                Text   = !string.IsNullOrWhiteSpace(t.Text) ? t.Text.Trim()
                         : (previous != null && i < previous.Count ? previous[i].Text : ""),
                Status = (t.Status ?? "pending").Trim().ToLowerInvariant().Replace('-', '_')
            })
            .Where(t => !string.IsNullOrEmpty(t.Text))
            .ToList();
    }

    // Collapse the checklist once every directive is complete. Never auto-expands here — a new
    // user directive resets the collapse state in SendAsync, so a manual mid-task collapse sticks.
    private void UpdateManifestCollapse()
    {
        if (_currentManifest.Count > 0 && _currentManifest.All(t => t.Status == "completed"))
            _manifestCollapsed = true;
    }

    private static bool TodoListsEqual(IReadOnlyList<Aria.Tools.TodoItem> a, IReadOnlyList<Aria.Tools.TodoItem> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].Text != b[i].Text || a[i].Status != b[i].Status)
                return false;
        return true;
    }

    // Streaming render coalescing. A verbose reasoner (deepseek-r1 emits thousands of reasoning
    // tokens fast) was frozen mid-stream not by render cost but because EVERY token did its own
    // InvokeAsync marshal onto the circuit's single-threaded sync context — thousands of queued
    // callbacks starve rendering and the WebSocket, so the view stops updating while tokens keep
    // arriving in the background. The fix: append token text SYNCHRONOUSLY off the UI thread (under
    // the run lock, exactly like the run's own content append), and marshal only ONE throttled
    // render per interval. Thread-safe: ScheduleStreamRender is called from the agent stream thread.
    private static readonly TimeSpan StreamRenderInterval = TimeSpan.FromMilliseconds(80);
    private int _streamRenderScheduled;               // 0/1, guarded via Interlocked
    private volatile CogitationRun? _pendingRunUpdate; // latest run update to fold in on the next flush

    private void ScheduleStreamRender()
    {
        // Collapse any burst of tokens between now and the next tick into a single render.
        if (Interlocked.CompareExchange(ref _streamRenderScheduled, 1, 0) != 0) return;
        _ = InvokeAsync(async () =>
        {
            await Task.Delay(StreamRenderInterval);
            Interlocked.Exchange(ref _streamRenderScheduled, 0);
            await FlushStreamRenderAsync();
        });
    }

    // Runs on the circuit sync context: fold in the latest run state, paint once, then do the
    // (throttled) file-tool completion check. All the per-update work that used to run per token.
    private async Task FlushStreamRenderAsync()
    {
        var run = _pendingRunUpdate;
        if (run != null && _attachedRun == run && _streamingMsg != null)
        {
            _pendingRunUpdate = null;
            // Promote steers the injector has drained before syncing the live bubble — so the
            // transcript can seal/rotate and the new Reply is what we mirror into.
            await PromoteConsumedSteersAsync();
            if (_streamingMsg != null)
                SyncMirrorFromRun(_streamingMsg, run);
            // Only replace the manifest list when the contents actually changed. Replacing it
            // on every throttled flush restarts the CSS pulse animation on the in-progress item
            // and can make the checklist appear to blink or jump.
            List<Aria.Tools.TodoItem> freshManifest;
            lock (run.Sync) freshManifest = NormalizeTodos(run.Manifest, _currentManifest);
            if (!TodoListsEqual(_currentManifest, freshManifest))
            {
                _currentManifest = freshManifest;
                UpdateManifestCollapse();
            }
            _statusOverride = run.StatusText;
            if (run.Status == CogitationRunStatus.AwaitingContextApproval)
                _awaitingContextApprovalSessionId = run.ContextApprovalSessionId;
        }
        _smartScrollPending = true;
        StateHasChanged();
        if (run != null && _attachedRun == run && _streamingMsg != null)
            await CheckForFileToolCompletionsAsync(run, _streamingMsg);
    }

    private void OnThinkingToken(string text)
    {
        // Append synchronously — NO per-token InvokeAsync. Use _thinkingTarget (not _streamingMsg):
        // thinking tokens can arrive after the content loop nulls _streamingMsg. Lock the run's Sync
        // (the same lock its content append takes) so thinking and content never mutate Sections
        // concurrently. run.Reply IS _thinkingTarget in the foreground, so this also keeps the
        // persisted reply complete — no buffer to drain at completion.
        var target = _thinkingTarget;
        if (target != null)
        {
            var run = _attachedRun;
            if (run != null) lock (run.Sync) AppendThinkingSection(target, text);
            else AppendThinkingSection(target, text);
        }
        // Keep the auto-greeting alive while the model is still reasoning: its thinking arrives here,
        // not through the greeting's own content loop, so without this a long "present yourself"
        // cogitation would trip the greeting idle timeout mid-thought.
        var g = _greetingIdleCts;
        if (g != null) { try { g.CancelAfter(TimeSpan.FromSeconds(60)); } catch (ObjectDisposedException) { } }
        ScheduleStreamRender();
    }

    private static void AppendThinkingSection(MessageEntry target, string text)
    {
        var last = target.Sections.LastOrDefault();
        if (last?.Type == MessageSection.SectionType.Thinking)
            last.Text += text;
        else
            target.Sections.Add(new MessageSection { Type = MessageSection.SectionType.Thinking, Text = text });
    }

    // ── ICogitationStreamSink (foreground view) ───────────────────────────
    // The Harness bakes callbacks into the agent's tools at construction time (see
    // CogitationStreamRouter), so the component is wired in as a router Target, not directly.

    void ICogitationStreamSink.ThinkingToken(string text) => OnThinkingToken(text);
    void ICogitationStreamSink.ToolStart(string name, string args) => OnToolStart(name, args);
    void ICogitationStreamSink.ToolComplete(string name, string result, string? imageBase64, string? imageMediaType, string? metadataJson) =>
        OnToolComplete(name, result, imageBase64, imageMediaType, metadataJson);
    void ICogitationStreamSink.TodoUpdate(IReadOnlyList<Aria.Tools.TodoItem> todos) => OnTodoUpdate(todos);
    Task<bool> ICogitationStreamSink.ApprovalRequestedAsync(ActionDescriptor descriptor, CancellationToken ct) =>
        RequestToolApprovalAsync(descriptor, ct);
    Task<string?> ICogitationStreamSink.AskUserAsync(string question, string[]? options, CancellationToken ct) =>
        RequestAskUserAsync(question, options, ct);
    void ICogitationStreamSink.ContextApprovalRequested(string sessionId) =>
        _ = InvokeAsync(() => { _awaitingContextApprovalSessionId = sessionId; StateHasChanged(); });

    private void OnUsage(ChatTokenUsage usage) =>
        _ = InvokeAsync((Action)(() =>
        {
            var elapsed = (DateTime.UtcNow - _streamStart).TotalSeconds;
            var tps = elapsed > 0 && usage.OutputTokenCount > 0
                ? Math.Round(usage.OutputTokenCount / elapsed, 1)
                : (double?)null;

            // Attach usage to the message so it renders as a per-message footer (ChatGPT style).
            if (_streamingMsg != null)
            {
                _streamingMsg.InputTokens  = usage.InputTokenCount;
                _streamingMsg.OutputTokens = usage.OutputTokenCount;
                _streamingMsg.Tps          = tps;
            }
            StateHasChanged();
        }));

    // ── Scroll helpers ────────────────────────────────────────────────────

    private async Task ScrollToBottomAsync()
    {
        try { await JS.InvokeVoidAsync("ariaInterop.scrollToBottom", "chatMessages"); }
        catch { /* JS not ready yet */ }
    }

    private async Task SmartScrollToBottomAsync()
    {
        try { await JS.InvokeVoidAsync("ariaInterop.smartScrollToBottom", "chatMessages"); }
        catch { }
    }

    // ── Render cycle ──────────────────────────────────────────────────────

    private string? _lastAppliedTheme;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await ApplyThemeAsync();
        // Idempotent (JS guards via _scrollListenerAttached). Must run on EVERY render, not just
        // firstRender — #chatMessages doesn't exist until a soul+channel+agent are selected.
        try { await JS.InvokeVoidAsync("ariaInterop.initChatScroll", "chatMessages"); } catch { }
        // Tab bar overflow arrows — idempotent (JS guards via _ariaTabsInit) but the visibility
        // refresh must still run every render since adding/removing a tab changes scrollWidth.
        try { await JS.InvokeVoidAsync("ariaInterop.initChatTabsScroll", "chat-tabs-scroll", "chat-tabs-arrow-left", "chat-tabs-arrow-right"); } catch { }
        // #chatInput, like #chatMessages, only exists once a soul+channel+agent are selected — wire
        // the picker keydown interceptor on every render (idempotent: JS removes any prior listener).
        await InitChatInputInteropAsync();
        // Terminal prompt: wire Tab to the Chat circuit for shell-style completion.
        // Idempotent (JS guards via _terminalTabBound) and safe when the panel is collapsed.
        EnsureTerminalInputRef();
        try { await JS.InvokeVoidAsync("ariaInterop.terminalInput.init", "terminalInput", _terminalInputDotNetRef); } catch { }
        // Clicking anywhere in the Quick Exec scrollback focuses the prompt — plain clicks only,
        // never a drag-select. Idempotent (JS guards via _focusBound); no-op when the body is absent.
        try { await JS.InvokeVoidAsync("ariaInterop.terminalInput.bindBodyFocus", "terminalBody", "terminalInput"); } catch { }
        // Explorer panel resize handles — idempotent (JS guards via handle._resizeBound) and safe to
        // call when the target doesn't exist yet (collapsed explorer / closed viewer): the JS no-ops.
        try
        {
            await JS.InvokeVoidAsync("ariaInterop.initResizablePanel",
                "explorer-panel-resize-handle", "explorer-panel",
                new { min = 180, max = 480, storageKey = "ariaExplorerTreeWidth" });
            await JS.InvokeVoidAsync("ariaInterop.initResizablePanel",
                "explorer-viewer-resize-handle", "explorer-viewer-panel",
                new { min = 320, max = 640, storageKey = "ariaExplorerViewerWidth" });
            await JS.InvokeVoidAsync("ariaInterop.initResizableHeight",
                "terminal-resize-handle", "terminal-panel",
                new { min = 120, max = 600, storageKey = "ariaTerminalHeight" });
        }
        catch { }
        // Restore explorer/viewer state once projects are actually available — on a hard refresh
        // this is the only render where there's real work to do (see RestoreExplorerStateAsync).
        if (!_explorerStateRestored && SessionState.Projects.Count > 0)
        {
            _explorerStateRestored = true;
            await RestoreExplorerStateAsync();
        }
        // Restore terminal panel state once bridge nodes are discoverable.
        if (!_terminalStateRestored && BridgeUserId() != null)
        {
            _terminalStateRestored = true;
            await RestoreTerminalStateAsync();
        }
        // Spawn the xterm PTY instance when the UI has rendered the PTY container.
        if (_ptyNeedsCreation && _terminalMode == TerminalMode.Pty && !_terminalCollapsed && !_ptyCreating && !_ptyRequesting)
        {
            _ptyNeedsCreation = false;
            await CreatePtyAsync();
        }
        // KaTeX mutates MarkupString DOM inside .math nodes — only typeset when not streaming so
        // per-token re-renders don't desync Blazor (see docs/troubleshooting/math-rendering.md).
        if (!_isStreaming)
        {
            try { await JS.InvokeVoidAsync("ariaInterop.typesetMath", "chatMessages"); } catch { }
        }
        if (firstRender)
        {
            await ScrollToBottomAsync();
            _ = RefreshTerminalBridgeStatusAsync();
            // If the bridge was already connected before this circuit subscribed to DirectBridgeRegistered,
            // the project list may never have been fetched. Load it now so the Explorer and file picker
            // work without requiring the user to open the Terminal tools modal first.
            if (BridgeUserId() is { } uid && BridgeRegistry.HasBridge(uid) && SessionState.Projects.Count == 0)
                _ = RefreshTerminalProjectsAsync();
            return;
        }
        if (_smartScrollPending)
        {
            _smartScrollPending = false;
            await SmartScrollToBottomAsync();
            // Also keep the inner thinking block following its own stream (separate scroll container).
            try { await JS.InvokeVoidAsync("ariaInterop.followThinkingStream"); } catch { }
        }
    }

    private async Task ApplyThemeAsync()
    {
        var color = _isHiveCogitation ? HivePurpleAccent : SessionState.ActiveSubAgent?.AccentColor;
        if (color == _lastAppliedTheme) return;
        try
        {
            _lastAppliedTheme = color;
            if (!string.IsNullOrEmpty(color))
                await JS.InvokeVoidAsync("ariaInterop.applyTheme", color);
            else
                await JS.InvokeVoidAsync("ariaInterop.clearTheme");
        }
        catch { _lastAppliedTheme = null; }
    }
}
