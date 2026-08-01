using System.Collections.Concurrent;
using Aria.Harness.Core;
using Aria.Shared;
using Aria.Web.Services.AgentServices;
using Aria.Web.Services.Chat;
using Aria.Web.Services.Memory;
using Aria.Web.Services.ModelBridge;
using Microsoft.Extensions.DependencyInjection;

namespace Aria.Web.Services.Cogitations;

/// <summary>
/// Singleton registry of in-flight cogitation turns, keyed by cogitationId — same shape as
/// <c>CollectiveOrchestrator</c>'s per-collective loop dictionary, but the run here <em>adopts</em>
/// an already-built agent/session/router handed over by the Chat component instead of building its
/// own (Hive's orchestrator builds its own agent from drone config, so its code can't be reused
/// directly — see docs/ideas/background-cogitation-continuation-plan.md).
///
/// A run started here keeps streaming — and, at the end, keeps persisting the reply — even after the
/// component that started it is disposed by navigation, a cogitation switch, or a page refresh.
/// </summary>
public sealed class CogitationRunRegistry(
    AgentService agentService,
    BridgeCogitationClient bridgeCogitation,
    BridgeMemoryClient memoryClient,
    SealService sealService,
    IServiceScopeFactory scopeFactory,
    ILogger<CogitationRunRegistry> logger)
{
    private readonly ConcurrentDictionary<int, CogitationRun> _runs = new();
    private readonly ConcurrentDictionary<int, CogitationRunRequest> _pendingContextRetries = new();
    private readonly ConcurrentDictionary<string, AutoMemoryBuffer> _autoMemory = new();

    // Cogitations that finished with nobody watching (background completion) — cleared once the user
    // opens that cogitation or the COGITATIONS panel. Runtime-only, mirrors CronSlot's "unseen vigil"
    // count but doesn't need DB persistence: it resets naturally on server restart along with the runs.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> _unseenCompletions = new();

    /// <summary>Raised on run start, completion, and removal so scoped components (NavMenu's sidebar,
    /// the Chat page) can react without polling. Second arg is the cogitationId.</summary>
    public event Action<string, int>? RunsChanged;

    /// <summary>Raised when a user's unseen-completion set changes (mirrors RunsChanged's role for the
    /// vigil-style green dot). Arg is the userId.</summary>
    public event Action<string>? UnseenChanged;

    public CogitationRun? TryGet(int cogitationId) => _runs.TryGetValue(cogitationId, out var r) ? r : null;

    public bool IsActive(int cogitationId) => _runs.ContainsKey(cogitationId);

    /// <summary>True while any of this user's cogitations has a run streaming in the background —
    /// drives the sidebar's "// COGITATIONS" nav icon blink, independent of which one (if any) is open.</summary>
    public bool AnyActiveForUser(string userId) => _runs.Values.Any(r => r.UserId == userId);

    /// <summary>True while this user has at least one cogitation that finished in the background and
    /// hasn't been opened since — drives the green "unseen" dot, same idea as unseen vigils.</summary>
    public bool HasUnseenCompletions(string userId) =>
        _unseenCompletions.TryGetValue(userId, out var set) && !set.IsEmpty;

    /// <summary>True for the specific cogitation that finished unseen — drives the per-row dot in the
    /// COGITATIONS panel so the user can tell which one(s) are unread without opening each in turn.</summary>
    public bool IsUnseen(string userId, int cogitationId) =>
        _unseenCompletions.TryGetValue(userId, out var set) && set.ContainsKey(cogitationId);

    /// <summary>Clears the unseen flag for one cogitation (called when the user opens it).</summary>
    public void MarkSeen(string userId, int cogitationId)
    {
        if (_unseenCompletions.TryGetValue(userId, out var set) && set.TryRemove(cogitationId, out _))
            UnseenChanged?.Invoke(userId);
    }

    private void MarkUnseen(string userId, int cogitationId)
    {
        var set = _unseenCompletions.GetOrAdd(userId, _ => new ConcurrentDictionary<int, byte>());
        set[cogitationId] = 0;
        UnseenChanged?.Invoke(userId);
    }

    public void Cancel(int cogitationId)
    {
        if (!_runs.TryGetValue(cogitationId, out var run)) return;
        // A run halted for context approval has already left RunLoopAsync — its CTS has no listener,
        // so Cancel() alone would be a no-op and the cogitation would stay bricked. Abandon it instead.
        if (run.Status == CogitationRunStatus.AwaitingContextApproval)
        {
            _ = AbandonContextApprovalAsync(cogitationId,
                "\n// SEAL CEREMONY CANCELLED — the action was not performed. //");
            return;
        }
        try { run.Cts.Cancel(); } catch { }
    }

    /// <summary>
    /// Abandons a turn halted for context approval (the human refused the grant, the ceremony timed
    /// out, or the user pressed STOP). Persists the partial reply with <paramref name="note"/> appended
    /// and releases the cogitation so new messages can be sent again — without this, the halted run
    /// stayed in <c>_runs</c> forever: every send hit the busy guard and STOP was a no-op.
    /// </summary>
    public async Task AbandonContextApprovalAsync(int cogitationId, string note)
    {
        if (!_pendingContextRetries.TryRemove(cogitationId, out var req)) return;
        if (!_runs.TryGetValue(cogitationId, out var run) ||
            run.Status != CogitationRunStatus.AwaitingContextApproval) return;

        run.AppendNote(note);
        run.MarkInterrupted();
        try { await FinishRunAsync(run, req); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Abandoned-approval finalize failed for cogitation {CogitationId}", cogitationId);
        }
        run.SetStatus(CogitationRunStatus.Completed);
        if (!run.HasAttachedViewer)
            MarkUnseen(req.UserId, req.CogitationId);
        _runs.TryRemove(cogitationId, out _);
        run.RaiseCompleted();
        RunsChanged?.Invoke(req.UserId, req.CogitationId);
    }

    /// <summary>Starts a new background run for a cogitation. Returns null if one is already active
    /// for that cogitationId — the caller (Chat.SendAsync) should treat that as "busy, don't send".</summary>
    public CogitationRun? StartRun(CogitationRunRequest req)
    {
        var run = new CogitationRun(
            req.CogitationId, req.UserId, req.OriginNodeId, req.SubAgentId,
            req.AgentSourceName, req.AgentModel, req.Agent, req.Session, req.Router, req.Reply, sealService);

        if (!_runs.TryAdd(req.CogitationId, run)) return null;

        req.Router.Target = run;
        RunsChanged?.Invoke(req.UserId, req.CogitationId);

        _ = Task.Run(() => RunLoopAsync(run, req));
        return run;
    }

    /// <summary>
    /// Retries a turn that was halted for context approval. Removes the halted run (without persisting
    /// its partial reply) and starts a fresh run from the stored request. Returns the new run, or null
    /// if no halted request is pending for this cogitation.
    /// </summary>
    public CogitationRun? RetryContextApproval(int cogitationId)
    {
        if (!_pendingContextRetries.TryRemove(cogitationId, out var req)) return null;
        _runs.TryRemove(cogitationId, out _);
        return StartRun(req with { IsContextRetry = true, ContextRetryCount = req.ContextRetryCount + 1 });
    }

    // A granted seal retried ONCE already; a second ContextApprovalRequiredException for the same
    // turn means the executing node is not honoring the replicated session grant (e.g. missing
    // sibling trust) — without this cap the turn ping-pongs approve → refuse → retry forever.
    private const int MaxContextRetries = 1;

    // Sent to the model instead of re-sending the original prompt when a turn resumes after the user
    // grants the node seal. The session already holds the original user turn, the model's own tool
    // call, and the PAUSED tool result (GovernedTool) — replaying the identical prompt as a second
    // user turn made the model re-reason from scratch and, seeing its own "paused" result in history,
    // often just apologize instead of retrying. This nudge continues the same task instead.
    private const string ContextRetryNudge =
        "[NODE SEAL GRANTED] The pending approval was granted by the user. The previously PAUSED tool " +
        "call is now authorized — retry it immediately and complete the original request. Do not " +
        "apologize for or mention the interruption.";

    private async Task RunLoopAsync(CogitationRun run, CogitationRunRequest req)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(run.Cts.Token, timeout.Token);

        var haltedForContextApproval = false;

        // Mint a per-turn checkpoint so every file mutation in this run can be batch-reverted by
        // /rewind. Flows into BridgeMcpTool via HarnessContext.CurrentTurnCheckpoint (AsyncLocal).
        var previousCheckpoint = HarnessContext.CurrentTurnCheckpoint;
        HarnessContext.CurrentTurnCheckpoint = Guid.NewGuid().ToString("N");
        run.CheckpointId = HarnessContext.CurrentTurnCheckpoint;

        try
        {
            var outgoing = req.IsContextRetry ? ContextRetryNudge : req.AiMessage;
            // ModelAuto leaves the save decision to the model; the heuristic nudge makes sure the
            // question is actually asked on turns that look like preferences/decisions/deferrals.
            // Skipped in Off (user wants explicit-only) and Regular/Always (harness inscribes anyway).
            if (!req.IsContextRetry && req.MemoryToolEnabled
                && req.AutoMemoryMode == AutoMemoryMode.ModelAuto
                && MemoryNudge.ShouldNudge(req.UserText))
                outgoing += MemoryNudge.NudgeText;

            await foreach (var token in agentService.StreamAsync(
                outgoing, run.Agent, run.Session, linked.Token,
                onUsage: run.SetUsage,
                turnScopePaths: req.TurnScopePaths, governanceMode: req.GovernanceMode,
                budgetToolCalls: req.BudgetToolCalls, budgetFileReads: req.BudgetFileReads,
                fleetApprovalRequired: req.FleetApprovalRequired))
            {
                run.AppendContent(token);
            }
        }
        catch (OperationCanceledException)
        {
            run.AppendNote("\n// TRANSMISSION INTERRUPTED //");
            run.MarkInterrupted();
        }
        catch (Exception ex) when (StreamingErrorHelper.IsCancellation(ex))
        {
            run.AppendNote("\n// TRANSMISSION INTERRUPTED //");
            run.MarkInterrupted();
        }
        catch (ContextApprovalRequiredException capEx)
        {
            if (req.ContextRetryCount >= MaxContextRetries)
            {
                // The seal was granted and the retried call was STILL refused: the executing node
                // is not honoring the session grant. Make it terminal instead of re-halting into
                // another identical ceremony (the infinite approve/refuse loop this replaces).
                run.AppendNote(
                    "\n// CONTEXT GRANT NOT HONORED — the executing node refused the action even after " +
                    "the session seal was granted and the call was retried. The approved grant is not " +
                    "taking effect on the target node (sibling trust missing or grant replication " +
                    "failing). The action was NOT performed — retrying the same call is pointless " +
                    "until the node's trust configuration is fixed. //");
                run.MarkInterrupted();
            }
            else
            {
                haltedForContextApproval = true;
                run.SetAwaitingContextApproval(capEx.SessionId);
                _pendingContextRetries[run.CogitationId] = req;
                run.AppendNote("\n// CONTEXT APPROVAL REQUIRED — approve sensitive operations on your node to continue. //");
            }
        }
        catch (Exception ex)
        {
            run.AppendNote($"\n// COGITATOR FAULT: {StreamingErrorHelper.FriendlyError(ex.Message, req.AgentSourceName)} //");
            run.MarkInterrupted();
        }
        try
        {
            if (!haltedForContextApproval)
            {
                try { await FinishRunAsync(run, req); }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Cogitation run finalize failed for cogitation {CogitationId}", run.CogitationId);
                }

                run.SetStatus(CogitationRunStatus.Completed);
                // Checked BEFORE RaiseCompleted() notifies (and detaches) any attached viewer — reflects
                // whether someone was watching live as this run actually finished.
                if (!run.HasAttachedViewer)
                    MarkUnseen(req.UserId, req.CogitationId);
                _runs.TryRemove(run.CogitationId, out _);
                run.RaiseCompleted();
                RunsChanged?.Invoke(req.UserId, req.CogitationId);
            }
        }
        finally
        {
            HarnessContext.CurrentTurnCheckpoint = previousCheckpoint;
        }
    }

    // One persisted row: either an assistant text chunk (with its tool-activity sections) or a
    // screenshot. Built by SplitReplyForPersistence in true chronological order, so a reload replays
    // tool cards and screenshots exactly where they happened in the live stream.
    private readonly record struct ReplyChunk(
        bool IsScreenshot,
        string Text,
        string? Thinking,
        string? SectionsJson,
        string? ImageBase64,
        string? ImageMediaType);

    private static readonly System.Text.Json.JsonSerializerOptions SectionJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // Walks the reply's sections in order, splitting into assistant rows at every image-bearing tool
    // call. Each assistant row carries the text and any non-screenshot tool sections that appeared since
    // the last screenshot, serialized so diff cards survive a reload.
    private static List<ReplyChunk> SplitReplyForPersistence(CogitationRun run)
    {
        var chunks = new List<ReplyChunk>();
        lock (run.Sync)
        {
            var thinking  = run.Reply.ThinkingContent.Length > 0 ? run.Reply.ThinkingContent : null;
            var firstText = true;
            var sb        = new System.Text.StringBuilder();
            var sections  = new List<MessageSection>();

            void FlushText()
            {
                if (sb.Length == 0 && sections.Count == 0) return;
                var sectionsJson = sections.Count > 0
                    ? System.Text.Json.JsonSerializer.Serialize(sections, SectionJsonOptions)
                    : null;
                chunks.Add(new ReplyChunk(false, sb.ToString(), firstText ? thinking : null, sectionsJson, null, null));
                firstText = false;
                sb.Clear();
                sections.Clear();
            }

            foreach (var s in run.Reply.Sections)
            {
                if (s.Type == MessageSection.SectionType.Content)
                {
                    sb.Append(s.Text);
                }
                else if (s.Type == MessageSection.SectionType.ToolActivity &&
                         s.ToolCall?.ImageBase64 is { Length: > 0 } image)
                {
                    FlushText();
                    var caption = string.IsNullOrWhiteSpace(s.ToolCall!.Result) ? "Screenshot" : s.ToolCall!.Result!;
                    chunks.Add(new ReplyChunk(true, caption, null, null, image, s.ToolCall.ImageMediaType ?? "image/png"));
                }
                else if (s.Type == MessageSection.SectionType.ToolActivity)
                {
                    // Non-image tool activity rides with the surrounding assistant chunk.
                    sections.Add(s);
                }
            }
            FlushText();
        }
        return chunks;
    }

    private async Task FinishRunAsync(CogitationRun run, CogitationRunRequest req)
    {
        run.CollapseThinking();
        run.SetStatus(CogitationRunStatus.Persisting);

        var chunks  = SplitReplyForPersistence(run);
        var content = run.Reply.Content;   // full text, for auto-inscribe below — independent of the split

        if (req.OriginNodeId != null)
        {
            // Bridge-owned content: persist only on the node; bump the server index timestamp.
            foreach (var chunk in chunks)
            {
                if (chunk.IsScreenshot)
                    await bridgeCogitation.AddMessageAsync(req.UserId, req.CogitationId, "screenshot", chunk.Text,
                        originNodeId: req.OriginNodeId, imageBase64: chunk.ImageBase64, imageMediaType: chunk.ImageMediaType);
                else
                    await bridgeCogitation.AddMessageAsync(req.UserId, req.CogitationId, "assistant", chunk.Text, chunk.Thinking, chunk.SectionsJson,
                        originNodeId: req.OriginNodeId);
            }

            await using var scope  = scopeFactory.CreateAsyncScope();
            var cogitationService = scope.ServiceProvider.GetRequiredService<CogitationService>();
            await cogitationService.TouchAsync(req.CogitationId);
        }
        else
        {
            // Legacy/server-stored content.
            await using var scope  = scopeFactory.CreateAsyncScope();
            var cogitationService = scope.ServiceProvider.GetRequiredService<CogitationService>();
            foreach (var chunk in chunks)
            {
                if (chunk.IsScreenshot)
                    await cogitationService.AddMessageAsync(req.CogitationId, "screenshot", chunk.Text,
                        imageBase64: chunk.ImageBase64, imageMediaType: chunk.ImageMediaType);
                else
                    await cogitationService.AddMessageAsync(req.CogitationId, "assistant", chunk.Text, chunk.Thinking, chunk.SectionsJson);
            }
        }

        if (!string.IsNullOrEmpty(content))
            await MaybeAutoInscribeAsync(req, content);
    }

    // ── Auto-memory (moved from the Chat component so it survives detachment) ──

    private sealed class AutoMemoryBuffer
    {
        public readonly List<string> Turns = [];
        public int Count;
    }

    private async Task MaybeAutoInscribeAsync(CogitationRunRequest req, string assistantText)
    {
        if (!req.MemoryToolEnabled) return;
        if (string.IsNullOrWhiteSpace(req.UserText) && string.IsNullOrWhiteSpace(assistantText)) return;

        var turnText = $"User: {req.UserText}\nAssistant: {assistantText}";

        switch (req.AutoMemoryMode)
        {
            case AutoMemoryMode.Always:
                await memoryClient.InscribeAsync(req.UserId, turnText);
                break;

            case AutoMemoryMode.Regular:
            {
                var buffer = _autoMemory.GetOrAdd(req.UserId, _ => new AutoMemoryBuffer());
                string? batch = null;
                lock (buffer)
                {
                    buffer.Turns.Add(turnText);
                    buffer.Count++;
                    if (buffer.Count >= Math.Max(1, req.AutoMemoryInterval))
                    {
                        batch = string.Join("\n\n", buffer.Turns);
                        buffer.Turns.Clear();
                        buffer.Count = 0;
                    }
                }
                if (batch != null)
                    await memoryClient.InscribeAsync(req.UserId, batch);
                break;
            }

            // Off / ModelAuto: no harness-triggered inscribe — left entirely to the model's own tool use.
            default:
                break;
        }
    }
}
