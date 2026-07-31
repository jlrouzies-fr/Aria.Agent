using Aria.Web.Services.Chat;
using Aria.Web.Services;
using Aria.Harness.Context;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using OpenAI.Chat;

namespace Aria.Web.Components.Pages;

public partial class Chat
{
    // The active greeting's idle-timeout CTS, so OnThinkingToken can push the deadline out while the
    // model is still reasoning (thinking tokens don't pass through the greeting's own content loop).
    private CancellationTokenSource? _greetingIdleCts;

    private async Task SendGreetingAsync(CancellationToken ct = default)
    {
        if (_agent == null || _session == null) return;

        // Never greet while something else already owns the stream. A greeting fires on session
        // reuse/rebuild (e.g. switching channel); if a run is mid-flight or adopted for this view, the
        // greeting's token stream would interleave with the run's into the same _streamingMsg — the
        // "I am Aria recorded…" greeting spliced character-by-character into the real answer. Bail so
        // exactly one producer writes the live bubble.
        if (_isStreaming || _attachedRun != null) return;

        var capturedAgent = _agent;
        // Greet through a THROWAWAY session, not the live one. Streaming the "present yourself"
        // prompt + the greeting reply into the real session pollutes the conversation history and
        // primes the model to re-introduce itself after later tool calls instead of answering
        // (confirmed by replaying the captured request: with the greeting turn in context the
        // model re-greets ~4/5 of the time; remove it → answers 5/5). The greeting is still shown
        // in the UI (_messages) — it just never enters the model-visible session history.
        var capturedSession = await capturedAgent.CreateSessionAsync();

        var greeting = new MessageEntry("assistant", "")
        {
            SpriteKey   = SessionState.ActiveSubAgent?.AvatarSpriteKey ?? _ariaAvatarKey,
            AccentColor = SessionState.ActiveSubAgent?.AccentColor,
            AgentName   = SessionState.ActiveSubAgent?.GeneratedName,
            IsSoul      = false,
        };
        await InvokeAsync(() =>
        {
            _messages.Add(greeting);
            _streamingMsg   = greeting;
            _thinkingTarget = greeting;
            _isStreaming   = true;
            _streamStart   = DateTime.UtcNow;
            StateHasChanged();
        });

        // IDLE timeout, not a 20s total cap: a verbose local reasoner (deepseek-r1) spends 30s+ just
        // THINKING about how to present itself, so a flat cap cancelled the greeting mid-thought while
        // LM Studio kept generating — the reported "stops mid thinking". This fires only after real
        // silence (no thinking OR content for GreetingIdle). Thinking arrives via OnThinkingToken (a
        // separate channel), so that handler resets this same CTS while the model reasons.
        var greetingIdle = TimeSpan.FromSeconds(60);
        using var timeout = new CancellationTokenSource(greetingIdle);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        _greetingIdleCts = timeout;

        try
        {
            await foreach (var token in AgentService.StreamAsync(
                $"Soul entering the chat is named {SessionState.CurrentUser!.Name}. Present yourself briefly in Warhammer 40K lore tone. " +
                "This is a cosmetic self-introduction only: reply with one or two sentences of prose and DO NOT call any tools, functions, or memory operations.",
                capturedAgent, capturedSession, linked.Token, OnUsage))
            {
                try { timeout.CancelAfter(greetingIdle); } catch (ObjectDisposedException) { }  // reset idle on each token
                var lastG = greeting.Sections.LastOrDefault();
                if (lastG?.Type == MessageSection.SectionType.Content)
                    lastG.Text += token;
                else
                {
                    CollapseThinking(greeting);   // answer begins — fold the cogitation
                    greeting.Sections.Add(new MessageSection { Type = MessageSection.SectionType.Content, Text = token });
                }
                _smartScrollPending = true;
                ScheduleStreamRender();
            }
        }
        catch (OperationCanceledException) { /* greeting cancelled or timed out — no fault message */ }
        catch (Exception ex) when (StreamingErrorHelper.IsCancellation(ex)) { /* cancellation surfaced as a wrapped exception */ }
        catch (Aria.Shared.ContextApprovalRequiredException)
        {
            // The model tried to call a governed tool during its cosmetic intro (some models — e.g.
            // granite — reach for INSCRIBE while "presenting themselves"). The greeting runs on a
            // throwaway session outside the run registry, so it has no approval-window plumbing; do
            // NOT surface a scary "COGITATOR FAULT" or a dead-end seal request. Just end the intro
            // quietly — a real turn will open the approval window correctly when it matters.
            greeting.Sections.RemoveAll(s => s.Type == MessageSection.SectionType.Thinking);
        }
        catch (Exception ex) { greeting.Content += $"\n// COGITATOR FAULT: {ex.Message} //"; }
        finally
        {
            _greetingIdleCts = null;   // stop OnThinkingToken from touching this (about-to-be-disposed) CTS
            await InvokeAsync(() =>
            {
                // Only reset streaming state if this greeting is still the active one
                if (_streamingMsg == greeting)
                {
                    _isStreaming  = false;
                    _streamingMsg = null;
                }
                // A greeting that produced no visible prose (e.g. the model went straight for a tool
                // call we declined) would otherwise leave an empty assistant bubble — drop it.
                if (!greeting.Sections.Any(s => s.Type == MessageSection.SectionType.Content && !string.IsNullOrWhiteSpace(s.Text)))
                    _messages.Remove(greeting);
                StateHasChanged();
            });
            await ScrollToBottomAsync();

            if (!string.IsNullOrWhiteSpace(_queuedInput) && _agent != null && _session != null)
            {
                _input       = _queuedInput;
                _queuedInput = "";
                await SendAsync();
            }
        }
    }

    private async Task OnFileSelected(InputFileChangeEventArgs e)
    {
        _attachError = null;
        var file = e.File;
        const long maxBytes = 512 * 1024; // 512 KB

        if (file.Size > maxBytes)
        {
            _attachError = $"// FILE TOO LARGE: {file.Name} ({file.Size / 1024} KB) — limit is 512 KB";
            StateHasChanged();
            return;
        }

        try
        {
            using var stream = file.OpenReadStream(maxAllowedSize: maxBytes);
            using var reader = new System.IO.StreamReader(stream);
            _attachedFileContent = await reader.ReadToEndAsync();
            _attachedFileName    = file.Name;
        }
        catch (Exception ex)
        {
            _attachError = $"// ATTACHMENT FAULT: {ex.Message}";
            _attachedFileName    = null;
            _attachedFileContent = null;
        }

        StateHasChanged();
    }

    private void ClearAttachment()
    {
        _attachedFileName    = null;
        _attachedFileContent = null;
        _attachError         = null;
    }

    private async Task SendAsync()
    {
        if (!SoulVerified) return;   // defense in depth: never transmit without a verified bridge
        if (_rebuilding) return;     // a soft session rebuild (tool/scope change) is swapping the agent
        if ((string.IsNullOrWhiteSpace(_input) && _attachedFileName == null) || _isStreaming) return;
        // Hive sends route through the Overmind pipeline, not the normal single-agent _agent/_session —
        // don't block on those (a Hive cogitation may open with no globally-selected channel at all).
        if (!_isHiveCogitation && (_agent == null || _session == null)) return;

        // Edit-and-replay: truncate transcript + reset thread before the normal send path appends
        // the edited user turn. Must run before we clear _input / append the new user bubble.
        if (_replayFromIndex != null)
        {
            if (!await PrepareReplayBeforeSendAsync()) return;
            // After truncate the live session is brand-new; SendAsync's null-session guard above
            // already passed, but Prepare may have replaced _session — re-check.
            if (!_isHiveCogitation && (_agent == null || _session == null)) return;
        }

        // Never let a null router reach StartRun: `req.Router.Target = run` would NRE unhandled and
        // kill the whole circuit (frozen chat, dead UI). AttachToRun now restores it, so this firing
        // means some new detach path left the component half-torn-down — surface it instead of dying.
        if (!_isHiveCogitation && _router == null)
        {
            _attachError = "// SESSION DESYNC: the stream router was lost — reopen this cogitation to continue.";
            StateHasChanged();
            return;
        }

        // Belt-and-suspenders: a cogitation with a live run should already show _isStreaming == true
        // via the reattach path, but guard directly too (e.g. a second browser tab on the same cogitation).
        if (_cogitationId.HasValue && Registry.IsActive(_cogitationId.Value))
        {
            _attachError = "// COGITATION BUSY: a response is still streaming — wait or press STOP.";
            StateHasChanged();
            return;
        }

        var userText     = _input.Trim();
        var fileContent  = _attachedFileContent;
        var fileName     = _attachedFileName;

        // "/governance …" (with arguments) runs locally — it configures the session, so it must
        // never reach the agent as a chat message. The palette path handles the bare command.
        if (userText.Equals("/governance", StringComparison.OrdinalIgnoreCase) ||
            userText.StartsWith("/governance ", StringComparison.OrdinalIgnoreCase))
        {
            _input = "";
            await ClosePickersAsync();
            await HandleGovernanceCommandAsync(userText["/governance".Length..]);
            await FocusInputAsync();
            return;
        }

        // "/compact …" also runs locally: bare opens the manual-compact confirmation; "auto …"
        // configures auto-compaction. Neither may reach the agent as a chat message.
        if (userText.Equals("/compact", StringComparison.OrdinalIgnoreCase) ||
            userText.StartsWith("/compact ", StringComparison.OrdinalIgnoreCase))
        {
            _input = "";
            await ClosePickersAsync();
            await HandleCompactCommandAsync(userText["/compact".Length..]);
            await FocusInputAsync();
            return;
        }

        // "/rewind …" also runs locally: it reverts the most recent mutating turn (or nth recent)
        // captured in the transcript's file-mutation metadata. It must never reach the agent.
        if (userText.Equals("/rewind", StringComparison.OrdinalIgnoreCase) ||
            userText.StartsWith("/rewind ", StringComparison.OrdinalIgnoreCase))
        {
            _input = "";
            await ClosePickersAsync();
            await HandleRewindCommandAsync(userText["/rewind".Length..]);
            await FocusInputAsync();
            return;
        }

        // "/scope …" (Wave 5) also runs locally: it lists the effective scope, asks the node for a
        // session path expansion, or revokes one — it must never reach the agent as a chat message.
        if (userText.Equals("/scope", StringComparison.OrdinalIgnoreCase) ||
            userText.StartsWith("/scope ", StringComparison.OrdinalIgnoreCase))
        {
            _input = "";
            await ClosePickersAsync();
            await HandleScopeCommandAsync(userText["/scope".Length..]);
            await FocusInputAsync();
            return;
        }

        // Snapshot "#"-picked files (reliable abs paths) before clearing; resolved to content below.
        var pickedRefs = _referencedFiles.ToList();
        _referencedFiles.Clear();
        await ClosePickersAsync();

        _input               = "";
        _attachedFileContent = null;
        _attachedFileName    = null;
        _attachError         = null;

        var displayText = fileName != null
            ? (string.IsNullOrEmpty(userText) ? $"[Attached: {fileName}]" : $"[Attached: {fileName}]\n{userText}")
            : userText;

        _messages.Add(new MessageEntry("user", displayText) { IsSoul = true });

        // A new directive starts a fresh task context: drop the previous manifest — the agent
        // re-posts one if this task needs it, instead of appending to a stale completed list.
        _currentManifest.Clear();
        _manifestCollapsed = false;

        bool isNewCogitation = !_cogitationId.HasValue;
        int? newCogitationFolderId = null;
        if (isNewCogitation)
        {
            var originNodeId = GetActiveBridgeNodeId();
            if (originNodeId == null)
            {
                _attachError = "// COGITATOR OFFLINE: cannot begin a bridge-owned cogitation while no node is connected.";
                StateHasChanged();
                return;
            }

            var cog = await CogitationService.CreateAsync(
                SessionState.CurrentUser!.Id,
                SessionState.ActiveSubAgent?.Id,
                string.IsNullOrEmpty(_ariaAvatarKey) ? PickAriaAvatar() : _ariaAvatarKey,
                originNodeId,
                folderId: SessionState.FocusedFolderId);
            newCogitationFolderId = cog.FolderId;
            _ariaAvatarKey = cog.AriaAvatarKey ?? _ariaAvatarKey;
            _cogitationId = cog.Id;
            _cogitationOriginNodeId = cog.OriginNodeId;
            SessionState.ActiveCogitationId = cog.Id;
            SessionState.OpenTab(cog.Id);
            SessionState.NotifyCogitationsChanged();
            SyncChatUrl(cog.Id);
        }

        var cogIdNow     = _cogitationId!.Value;
        var bridgeUidNow = BridgeUserId();

        if (_cogitationOriginNodeId != null)
        {
            // Bridge-owned: write content only to the bridge, synchronously, so failures surface.
            // Keep the server index title/timestamp in sync so the history panel is accurate.
            if (bridgeUidNow != null)
            {
                var uid       = bridgeUidNow;
                var userId    = SessionState.CurrentUser!.Id;
                var avatarKey = _ariaAvatarKey;
                var agentId   = SessionState.ActiveSubAgent?.Id.ToString();
                var msg       = displayText;

                // Pin every write for this cogitation to its origin node. The assistant reply is already
                // persisted to _cogitationOriginNodeId (see CogitationRunRegistry) — the cogitation record
                // and the user message MUST land on the same node, or a multi-node soul splits the
                // conversation (user turns on the default node, agent turns on the origin) and reopening
                // shows only half of it.
                var originNode = _cogitationOriginNodeId;

                bool ok = true;
                if (isNewCogitation)
                    ok = await BridgeCogitation.EnsureCogitationAsync(uid, userId, cogIdNow, avatarKey, agentId, originNodeId: originNode, folderId: newCogitationFolderId);

                if (ok)
                    ok = await BridgeCogitation.AddMessageAsync(uid, cogIdNow, "user", msg, originNodeId: originNode);

                if (ok)
                    _ = CogitationService.TouchAsync(cogIdNow);

                if (!_cogitationTitled && ok)
                {
                    _cogitationTitled = true;
                    var title = string.IsNullOrEmpty(userText) ? $"[{fileName}]" : userText;
                    ok = await BridgeCogitation.UpdateTitleAsync(uid, cogIdNow, title, originNode);
                    await CogitationService.SetTitleAsync(cogIdNow, title);
                    SessionState.NotifyCogitationsChanged();
                }

                if (!ok)
                {
                    _attachError = "// COGITATOR FAULT: could not save message to your node. Check the bridge is connected.";
                    StateHasChanged();
                }
            }
        }
        else
        {
            // Legacy/server-stored content.
            await CogitationService.AddMessageAsync(cogIdNow, "user", displayText);

            if (!_cogitationTitled)
            {
                _cogitationTitled = true;
                var title = string.IsNullOrEmpty(userText) ? $"[{fileName}]" : userText;
                await CogitationService.SetTitleAsync(cogIdNow, title);
                SessionState.NotifyCogitationsChanged();
            }
        }

        // Hive cogitations have no single "_agent"/"_session" — the reply comes from the Overmind's
        // plan/dispatch/synthesise pipeline, which persists its own messages progressively. Route
        // there instead of the normal single-agent streaming path below.
        if (_isHiveCogitation && _hiveCollectiveId.HasValue)
        {
            await StartHiveOrchestrationAsync(_hiveCollectiveId.Value, cogIdNow, userText);
            return;
        }

        var reply = new MessageEntry("assistant", "")
        {
            SpriteKey   = EffectiveAgent?.AvatarSpriteKey ?? _ariaAvatarKey,
            AccentColor = EffectiveAgent?.AccentColor,
            AgentName   = EffectiveAgent?.GeneratedName,
            IsSoul      = false,
        };
        _messages.Add(reply);
        _streamingMsg       = reply;
        _thinkingTarget     = reply;
        _isStreaming         = true;
        _streamStart         = DateTime.UtcNow;
        _smartScrollPending = true;
        StateHasChanged();

        // Build the text sent to the AI (includes full file content if attached)
        var baseAiText = fileContent != null
            ? $"[ATTACHED FILE: {fileName}]\n{fileContent}\n[END OF FILE]\n\n{userText}"
            : userText;

        // Inject the user's shared terminal scrollback when the AGENT SEES toggle is ON. Fresh each
        // turn, never persisted to cogitation history, so it can't bloat long chats.
        var terminalContext = await BuildTerminalContextForAgentAsync();
        if (!string.IsNullOrEmpty(terminalContext))
            baseAiText = terminalContext + "\n\n" + baseAiText;

        // A per-turn, in-message reminder of the active project — not just the system prompt set at
        // session creation. A long conversation can carry earlier turns that mention a *different*
        // project's paths (e.g. before the user switched via the Explorer); a model weighing recent
        // conversational context over a system instruction from many turns back can otherwise keep
        // reusing the stale path. Restating it fresh, right next to the request, keeps it salient.
        if (SessionState.ActiveProject is { } activeProject)
            baseAiText = $"[ACTIVE PROJECT SCOPE: {activeProject.Name} — {activeProject.Path}. All file and " +
                "terminal actions this turn must stay within this directory — disregard any other project " +
                "path mentioned earlier in this conversation.]\n" + baseAiText;

        // Resolve "#path" references to absolute paths and tell the agent where they are, so it reads
        // them with its own file tools (no content upload). Surface any that couldn't be resolved.
        var unresolvedRefs = new List<string>();
        var refNote = await BuildReferenceNote(userText, pickedRefs, unresolvedRefs);
        if (!string.IsNullOrEmpty(refNote))
            baseAiText = refNote + baseAiText;
        if (unresolvedRefs.Count > 0)
        {
            _attachError = $"// Unresolved reference(s): {string.Join(", ", unresolvedRefs)} — pick the file from the # list, or set the project with /project.";
            await InvokeAsync(StateHasChanged);
        }

        string aiMessage = baseAiText;
        if (_historyLoaded && !_historyInjected)
        {
            _historyInjected = true;
            aiMessage = BuildHistoryContext(SessionState.CurrentUser!.Name, _messages, baseAiText);
        }

        // This turn's allowed scope for the governance scope-lock: just the selected project when one
        // is active (matching how the Terminal tools are scoped in HarnessOptions.ActiveProjectPath),
        // else every declared project. Plus any "#"-referenced files the user explicitly attached, and
        // any node-approved session path expansions (Wave 5 — the soft copy; the bridge still enforces).
        var turnScope = SessionState.ActiveProject != null
            ? new List<string> { SessionState.ActiveProject.Path }
            : new List<string>(SessionState.AllowedProjectPaths);
        turnScope.AddRange(pickedRefs.Select(r => r.AbsPath));
        turnScope.AddRange(SessionState.SessionScopeExpansions);

        var run = Registry.StartRun(new CogitationRunRequest(
            CogitationId:       cogIdNow,
            UserId:             SessionState.CurrentUser!.Id,
            OriginNodeId:       _cogitationOriginNodeId,
            SubAgentId:         SessionState.ActiveSubAgent?.Id,
            AgentSourceName:    _agentSource,
            AgentModel:         _agentModel,
            Agent:              _agent,
            Session:            _session,
            Router:             _router!,
            Reply:              reply,
            AiMessage:          aiMessage,
            UserText:           userText,
            TurnScopePaths:     turnScope,
            GovernanceMode:     SessionState.Governance,
            MemoryToolEnabled:  SessionState.IsToolEnabled("memory"),
            AutoMemoryMode:     SessionState.AutoMemory,
            AutoMemoryInterval: SessionState.AutoMemoryInterval,
            SessionId:          SessionState.SessionToken,
            BudgetToolCalls:    SessionState.GovernanceBudgetToolCalls,
            BudgetFileReads:    SessionState.GovernanceBudgetFileReads,
            FleetApprovalRequired: SessionState.FleetApprovalRequired));

        if (run == null)
        {
            // Race with another run already active for this cogitation (e.g. a second tab) — the
            // busy guard above should normally have caught this already.
            _messages.Remove(reply);
            _isStreaming    = false;
            _streamingMsg   = null;
            _thinkingTarget = null;
            _attachError    = "// COGITATION BUSY: a response is already streaming for this cogitation.";
            StateHasChanged();
            return;
        }

        AttachToRun(run);
    }

    // Fires the Overmind's plan/dispatch/synthesise pipeline against the already-open cogitation.
    // The pipeline persists its own messages as it goes (WriteMsg → CogitationService.AddMessageAsync)
    // and calls onMessageAdded each time — OnHiveCogitationUpdated (Chat.HiveGate.razor.cs) picks those
    // up and appends them here. OnHiveRunStateChanged clears the busy indicator once the run ends.
    private async Task StartHiveOrchestrationAsync(int collectiveId, int cogitationId, string userPrompt)
    {
        _isStreaming    = true;
        _statusOverride = "OVERMIND: PLANNING…";
        ShowHiveTyping();
        StateHasChanged();

        var result = await Orchestrator.RunOnExistingCogitationAsync(
            collectiveId, cogitationId, userPrompt,
            onMessageAdded: SessionState.NotifyHiveCogitationUpdated);

        if (!result.Success)
        {
            _isStreaming    = false;
            _statusOverride = null;
            HideHiveTyping();
            var faultText = $"⬡ **OVERMIND FAULT**: {result.Error}";
            await CogitationService.AddMessageAsync(cogitationId, "assistant", faultText);
            _messages.Add(new MessageEntry("assistant", faultText)
            {
                SpriteKey   = _historyAvatarKey,
                AccentColor = _historyAccentColor,
                AgentName   = _historyAgentName,
            });
            StateHasChanged();
        }
    }

    // Wires the component into a CogitationRunRegistry-hosted run: subscribes to its events and does
    // an initial paint so both a fresh send and a mid-stream reattach render the same way.
    //
    // The handlers close over `run` explicitly and are stored so DetachFromRun can unsubscribe the
    // exact same delegate instances. This matters because `run.Completed` (etc.) can already be
    // queued via InvokeAsync on the dispatcher by the time the user navigates on to a different
    // cogitation — the queued continuation still runs afterwards. Without capturing `run` and
    // checking `_attachedRun == run` inside that continuation (not just at subscribe time), a late
    // completion callback from an OLD run would read the CURRENT `_attachedRun` (by now a different,
    // newly-attached run) and tear its state down — the "new cogitation gets interrupted right when
    // the other one finishes" bug.
    private void AttachToRun(CogitationRun run)
    {
        _attachedRun          = run;
        run.HasAttachedViewer = true;
        // Adopt the run's router. Callers that went through DetachFromRun first (the context-approval
        // retry path, OnRegistryRunChanged's auto-attach) had _router nulled there and nothing else
        // restores it — the NEXT SendAsync then passed Router: _router! as null into StartRun, whose
        // `req.Router.Target = run` NRE'd and killed the whole circuit (the "frozen chat" after an
        // approval retry). The run's router is the same instance the agent's tool callbacks were
        // built against, so adopting it is always correct — and a no-op on the fresh-send path.
        _router               = run.Router;

        _runUpdatedHandler   = () => OnRunUpdated(run);
        _runCompletedHandler = () => OnRunCompleted(run);
        _runApprovalHandler  = () => OnRunApprovalChanged(run);
        run.Updated         += _runUpdatedHandler;
        run.Completed       += _runCompletedHandler;
        run.ApprovalChanged += _runApprovalHandler;

        OnRunUpdated(run);
        OnRunApprovalChanged(run);
    }

    // Copies the run's sections into the component's mirror — but only when the mirror is a
    // genuinely separate object (the reattach path in OnCogitationSelected creates one). On a fresh
    // send _streamingMsg IS run.Reply (the very same MessageEntry handed into StartRun), so
    // Sections is the same List<T> instance; Clear()-then-AddRange(itself) would wipe whatever
    // content just streamed in before the AddRange ever ran. Skip the copy entirely in that case —
    // the shared object already reflects every update live, no copying needed.
    private static void SyncMirrorFromRun(MessageEntry mirror, CogitationRun run)
    {
        if (ReferenceEquals(mirror, run.Reply)) return;
        lock (run.Sync)
        {
            mirror.Sections.Clear();
            mirror.Sections.AddRange(run.Reply.Sections);
        }
        // Carry the per-message token footer across to the separate mirror object.
        mirror.InputTokens  = run.Reply.InputTokens;
        mirror.OutputTokens = run.Reply.OutputTokens;
        mirror.Tps          = run.Reply.Tps;
    }

    // Fires per content token from the run thread. Do NO per-token work here (no InvokeAsync, no
    // manifest copy, no async file check) — that flood is exactly what froze the view. Just record
    // the run and let the throttled flush (FlushStreamRenderAsync) fold it in on the UI thread at
    // most once per interval. Content already lives in run.Reply (shared with _streamingMsg), so no
    // data is lost by coalescing; OnRunCompleted does a final unthrottled paint.
    private void OnRunUpdated(CogitationRun run)
    {
        if (_attachedRun != run || _streamingMsg == null) return;
        _pendingRunUpdate = run;
        ScheduleStreamRender();
    }

    private void OnRunCompleted(CogitationRun run) => _ = InvokeAsync(async () =>
    {
        if (_attachedRun != run) return;   // stale callback for a run we've since left — ignore
        if (_streamingMsg != null) SyncMirrorFromRun(_streamingMsg, run);
        if (run.WasInterrupted) MarkInterrupted();

        run.HasAttachedViewer = false;
        if (_runUpdatedHandler   != null) run.Updated         -= _runUpdatedHandler;
        if (_runCompletedHandler != null) run.Completed       -= _runCompletedHandler;
        if (_runApprovalHandler  != null) run.ApprovalChanged -= _runApprovalHandler;
        _attachedRun         = null;
        _runUpdatedHandler   = null;
        _runCompletedHandler = null;
        _runApprovalHandler  = null;
        _isStreaming    = false;
        _streamingMsg   = null;
        _statusOverride = null;
        StateHasChanged();
        await ScrollToBottomAsync();
        await CheckSuggestedFilingAsync();

        // Auto-compaction: the turn has fully finished (never mid-tool-loop), so if the context
        // crossed the session threshold, summarise now and continue on the fresh session. Uses the
        // reported prompt-token count when the source returned usage, else a char-based estimate.
        if (!run.WasInterrupted &&
            AutoCompaction.ShouldCompact(run.Reply.InputTokens, TranscriptChars(), SessionState.AutoCompactThreshold, _effectiveContextWindow))
        {
            await AutoCompactAsync();
        }

        // User-facing warning when estimated usage exceeds a known context window. Not injected into
        // the model — it is purely a UI hint to compact or switch model.
        if (_effectiveContextWindow is { Assumed: false } knownWindow)
        {
            var estimated = run.Reply.InputTokens ?? AutoCompaction.EstimateTokens(TranscriptChars());
            var usedPct = (double)estimated / knownWindow.Tokens * 100;
            _contextWindowWarning = usedPct > 100
                ? $"Context exceeded — estimated {estimated:N0} tokens is above the known {knownWindow.Tokens:N0}-token window. Replies may degrade; compact or switch model."
                : null;
            if (_contextWindowWarning != null) await InvokeAsync(StateHasChanged);
        }

        // Only auto-send if the user explicitly queued via Enter — never from _input
        // to avoid the oninput race condition causing spurious re-sends.
        if (!string.IsNullOrWhiteSpace(_queuedInput) && _agent != null && _session != null)
        {
            _input       = _queuedInput;
            _queuedInput = "";
            await SendAsync();
        }
        else
        {
            _queuedInput = "";
            await JS.InvokeVoidAsync("ariaInterop.focusElement", "chatInput");
        }
    });

    /// <summary>
    /// Fired when the registry starts or removes a run. Used to auto-attach to a retried run after an
    /// in-chat context approval completes without requiring the user to re-open the cogitation.
    /// </summary>
    private void OnRegistryRunChanged(string userId, int cogitationId) => _ = InvokeAsync(() =>
    {
        if (SessionState.CurrentUser?.Id != userId) return;
        if (_cogitationId != cogitationId) return;
        if (_attachedRun != null) return;   // already attached (normal send path)

        var run = Registry.TryGet(cogitationId);
        if (run == null) return;

        // The normal send path already added run.Reply to _messages and will attach itself right after
        // StartRun returns. If we mirror it here we end up with two bubbles for the same reply.
        if (_streamingMsg != null && ReferenceEquals(_streamingMsg, run.Reply)) return;
        if (_messages.Contains(run.Reply)) return;

        // Externally-started run (e.g. context-approval retry after the chat was detached) — mirror its
        // reply into the chat and attach.
        _awaitingContextApprovalSessionId = null;
        _isStreaming = true;
        _streamingMsg = new MessageEntry("assistant", "")
        {
            SpriteKey   = run.Reply.SpriteKey,
            AccentColor = run.Reply.AccentColor,
            AgentName   = run.Reply.AgentName,
            IsSoul      = false,
        };
        _messages.Add(_streamingMsg);
        _thinkingTarget = _streamingMsg;
        _statusOverride = null;
        AttachToRun(run);
        StateHasChanged();
    });

    private void OnRunApprovalChanged(CogitationRun run) => _ = InvokeAsync(() =>
    {
        if (_attachedRun != run) return;
        _pendingApproval = run.PendingApproval;
        _pendingAskUser  = run.PendingAskUser;
        if (run.Status == CogitationRunStatus.AwaitingContextApproval)
        {
            _awaitingContextApprovalSessionId = run.ContextApprovalSessionId;
            // Auto-drive the node ceremony (opens the approval page + polls + retries the turn) so the
            // human just approves on their node and the turn resumes — no in-chat banner click needed.
            // The banner stays as a manual fallback; ApproveContextAsync guards against a double start.
            if (run.ContextApprovalSessionId is { } sid)
                _ = ApproveContextAsync(sid);
        }
        _smartScrollPending = true;
        StateHasChanged();
    });

    // After any interruption or fault the live AgentSession may be missing the turn that was in
    // flight. Force the next send to replay the full conversation as context.
    private void MarkInterrupted()
    {
        _historyLoaded   = _messages.Any(m => m.Role != "system");
        _historyInjected = false;
    }

    private static string BuildHistoryContext(string userName, List<MessageEntry> messages, string currentMessage)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[PRIOR COGITATION HISTORY — context only, use this to remember the conversation. Tools mentioned in this history may no longer be active; your current tool registry is the only authoritative source of available capabilities.]");
        sb.AppendLine();

        foreach (var m in messages)
        {
            if (m.Role == "system") continue;
            // Screenshot messages carry image bytes that must NEVER be replayed to the model — only
            // their text caption, as a note so the model knows an image was shown to the user.
            if (m.Role == "screenshot")
            {
                sb.AppendLine($"[A screenshot was captured and shown to the user: {m.Content}]");
                continue;
            }
            var prefix = m.Role == "user" ? userName : "ARIA";
            sb.AppendLine($"{prefix}: {m.Content}");
        }

        sb.AppendLine();
        sb.AppendLine("[END HISTORY — now respond to the following current message]");
        sb.AppendLine();
        sb.Append(currentMessage);

        return sb.ToString();
    }

    private async Task OnInputAsync(string value)
    {
        _input = value ?? "";
        await UpdatePickersAsync();
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        // While a "#"/"/project" picker is open the JS interceptor drives navigation (OnPickerKey);
        // swallow these here so Enter accepts a selection instead of sending the message.
        if (AnyPickerOpen && e.Key is "Enter" or "ArrowDown" or "ArrowUp" or "Tab" or "Escape") return;

        // Arrow-up on an empty input recalls a queued message for editing/cancelling — same gesture
        // as Claude Code's history recall. Editing it and pressing Enter re-queues it (below); leaving
        // the input and doing nothing effectively "cancels" the queue.
        if (e.Key == "ArrowUp" && string.IsNullOrEmpty(_input) && !string.IsNullOrEmpty(_queuedInput))
        {
            RecallQueued();
            return;
        }

        // Ctrl+Enter (or Cmd+Enter on Mac) sends. Plain Enter and Shift+Enter insert a newline —
        // this avoids the frequent, frustrating mis-send from a stray Enter mid-compose.
        if (e.Key != "Enter" || !(e.CtrlKey || e.MetaKey)) return;

        if (_isStreaming)
        {
            // DebouncedTextArea flushes the pending text (including the browser-inserted
            // trailing "\n") into _input before this handler runs — Trim handles it.
            var text = _input.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                _queuedInput = text;
                _input       = "";
                StateHasChanged();
            }
        }
        else
        {
            await SendAsync();
        }
    }

    // Pulls the queued message back into the composer for editing — same gesture as ArrowUp on an
    // empty input. Pressing Ctrl+Enter afterwards re-queues (while streaming) or sends it.
    private void RecallQueued()
    {
        if (string.IsNullOrEmpty(_queuedInput)) return;
        _input       = _queuedInput;
        _queuedInput = "";
        StateHasChanged();
    }

    // Discards the queued message without sending it (the ✕ on the queue block).
    private void CancelQueued()
    {
        _queuedInput = "";
        StateHasChanged();
    }

    // User declined the "/compact" confirmation — just close the modal, nothing was touched yet.
    private async Task CancelCompactAsync()
    {
        _compactConfirmOpen = false;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// "/compact": asks the live agent to summarize the conversation so far (reusing the normal
    /// streaming pipeline for full-history context), then replaces the persisted transcript —
    /// bridge-owned or legacy server-stored, whichever this cogitation uses — with just that
    /// summary. It also spins a fresh thread from the already-built agent (same cheap reset
    /// StartFreshSessionAsync uses for "/clear" — no format reprobe, no tool reload) so the live
    /// context window is actually reclaimed immediately, not just on the next session recreation.
    /// </summary>
    private async Task CompactAsync()
    {
        _compactConfirmOpen = false;
        if (_isHiveCogitation || _agent == null || _session == null || !_cogitationId.HasValue || _isStreaming) return;
        if (!_messages.Any(m => m.Role != "system")) return;   // nothing to compact yet

        _isStreaming = true;
        StateHasChanged();

        // Don't trust the live _session thread to already hold the full transcript (it may not —
        // e.g. a reloaded cogitation whose history was never replayed into this session). Build the
        // prompt from _messages directly, the same way SendAsync's BuildHistoryContext does, so the
        // model actually has something to summarize instead of just this instruction.
        var summarizePrompt = BuildHistoryContext(
            SessionState.CurrentUser!.Name,
            _messages,
            "Summarize the conversation above concisely — key facts, decisions, and open threads — " +
            "for context compaction. Respond with only the summary text, no preamble.");

        var summary = "";
        try
        {
            await foreach (var token in AgentService.StreamAsync(
                summarizePrompt, _agent, _session, CancellationToken.None, OnUsage))
            {
                summary += token;
            }
        }
        catch (Exception ex)
        {
            _attachError = $"// COMPACT FAILED: {ex.Message}";
            _isStreaming = false;
            await InvokeAsync(StateHasChanged);
            return;
        }

        summary = summary.Trim();
        if (summary.Length == 0)
        {
            _isStreaming = false;
            await InvokeAsync(StateHasChanged);
            return;
        }

        var cogId = _cogitationId.Value;
        if (_cogitationOriginNodeId != null)
        {
            var uid = BridgeUserId();
            if (uid != null) await BridgeCogitation.CompactAsync(uid, cogId, summary, _cogitationOriginNodeId);
        }
        else
        {
            await CogitationService.CompactAsync(cogId, summary);
        }

        _messages.Clear();
        _messages.Add(new MessageEntry("assistant", summary) { IsCompactSummary = true });
        await ResetAgentThreadAfterTranscriptChangeAsync();

        _isStreaming = false;
        await InvokeAsync(StateHasChanged);
        await ScrollToBottomAsync();
    }

    /// <summary>
    /// After Compact or edit-and-replay rewrites the persisted transcript: spin a fresh agent
    /// thread and force the next send to reinject history via <see cref="BuildHistoryContext"/>.
    /// </summary>
    private async Task ResetAgentThreadAfterTranscriptChangeAsync()
    {
        _historyLoaded   = _messages.Any(m => m.Role != "system");
        _historyInjected = false;

        // Spin a fresh thread on the already-built agent so the live context window matches the
        // rewritten transcript. If this faults, the next session recreation will catch up.
        try
        {
            if (_agent != null)
                _session = await _agent.CreateSessionAsync();
        }
        catch { /* keep old thread until next recreation */ }
    }
}
