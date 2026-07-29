using Aria.Harness.Governance;
using Aria.Web.Services.Chat;
using Aria.Web.Services.ModelBridge;
using Microsoft.Agents.AI;
using OpenAI.Chat;
using TodoItem = Aria.Tools.TodoItem;

namespace Aria.Web.Services.Cogitations;

public enum CogitationRunStatus { Streaming, Persisting, Completed, AwaitingContextApproval }

/// <summary>
/// State for one cogitation's in-flight turn, hosted by <see cref="CogitationRunRegistry"/> instead of
/// a Blazor component — so it keeps streaming after the component that started it is disposed
/// (navigation, cogitation switch, page refresh). Owns the adopted agent/session/router so a
/// reattaching component can pick them back up, and is the single writer of <see cref="Reply"/>
/// (always under <see cref="Sync"/>); attached components only ever read a lock-copied mirror.
/// </summary>
public sealed class CogitationRun : ICogitationStreamSink
{
    private readonly SealService _sealService;

    public int     CogitationId    { get; }
    public string  UserId          { get; }
    public string? OriginNodeId    { get; }
    public int?    SubAgentId      { get; }
    public string? AgentSourceName { get; }
    public string? AgentModel      { get; }
    public DateTime StartedUtc     { get; } = DateTime.UtcNow;

    /// <summary>Per-turn FileUndo checkpoint id minted at run start — stamped onto bridge mutations
    /// so <c>/rewind</c> can revert the whole turn. Null until the run loop begins.</summary>
    public string? CheckpointId { get; set; }

    public AIAgent               Agent    { get; }
    public AgentSession           Session  { get; }
    public CogitationStreamRouter Router   { get; }
    public MessageEntry           Reply    { get; }

    public readonly object Sync = new();

    public List<TodoItem> Manifest   { get; private set; } = [];
    public string?         StatusText { get; private set; }
    public CogitationRunStatus Status { get; private set; } = CogitationRunStatus.Streaming;
    public bool             WasInterrupted { get; private set; }
    public string?          ContextApprovalSessionId { get; private set; }

    public int?    LastInputTokens  { get; private set; }
    public int?    LastOutputTokens { get; private set; }
    public double? LastTps          { get; private set; }

    /// <summary>True while a Chat component is attached (foreground). Set by AttachToRun/DetachFromRun.
    /// Read at completion time to decide whether this run finishing counts as an "unseen" completion —
    /// the user watched an attached run stream to its end live, so it isn't unread.</summary>
    public bool HasAttachedViewer { get; set; }

    public CancellationTokenSource Cts { get; } = new();

    public ActionDescriptor? PendingApproval { get; private set; }
    private TaskCompletionSource<bool>? _approvalTcs;
    private CancellationTokenSource?    _sealCts;

    // Pending ask_user question (null when none) — same single-slot reasoning as the approval gate:
    // tool calls are sequential within a turn.
    public AskUserPrompt? PendingAskUser { get; private set; }
    private TaskCompletionSource<string?>? _askUserTcs;

    public event Action? Updated;
    public event Action? Completed;
    public event Action? ApprovalChanged;

    public CogitationRun(
        int cogitationId, string userId, string? originNodeId, int? subAgentId,
        string? agentSourceName, string? agentModel,
        AIAgent agent, AgentSession session, CogitationStreamRouter router, MessageEntry reply,
        SealService sealService)
    {
        CogitationId    = cogitationId;
        UserId          = userId;
        OriginNodeId    = originNodeId;
        SubAgentId      = subAgentId;
        AgentSourceName = agentSourceName;
        AgentModel      = agentModel;
        Agent           = agent;
        Session         = session;
        Router          = router;
        Reply           = reply;
        _sealService    = sealService;
    }

    // ── Content streaming (called by CogitationRunRegistry's run loop) ─────────

    internal void AppendContent(string token)
    {
        lock (Sync)
        {
            var last = Reply.Sections.LastOrDefault();
            if (last?.Type == MessageSection.SectionType.Content)
                last.Text += token;
            else
            {
                CollapseThinkingLocked();
                Reply.Sections.Add(new MessageSection { Type = MessageSection.SectionType.Content, Text = token });
            }
        }
        StatusText = null;
        Updated?.Invoke();
    }

    internal void AppendNote(string note)
    {
        lock (Sync)
        {
            var lastContent = Reply.Sections.LastOrDefault(s => s.Type == MessageSection.SectionType.Content);
            if (lastContent != null)
                lastContent.Text += note;
            else
                Reply.Sections.Add(new MessageSection { Type = MessageSection.SectionType.Content, Text = note });
        }
        Updated?.Invoke();
    }

    internal void SetUsage(ChatTokenUsage usage)
    {
        LastInputTokens  = usage.InputTokenCount;
        LastOutputTokens = usage.OutputTokenCount;
        var elapsed = (DateTime.UtcNow - StartedUtc).TotalSeconds;
        LastTps = elapsed > 0 && usage.OutputTokenCount > 0
            ? Math.Round(usage.OutputTokenCount / elapsed, 1)
            : null;

        // Attach to the message itself so the transcript can render a per-message token footer
        // (survives navigation/refresh via the run mirror, since Reply IS the displayed MessageEntry).
        Reply.InputTokens  = LastInputTokens;
        Reply.OutputTokens = LastOutputTokens;
        Reply.Tps          = LastTps;

        Updated?.Invoke();
    }

    internal void MarkInterrupted() => WasInterrupted = true;

    internal void SetAwaitingContextApproval(string? sessionId)
    {
        ContextApprovalSessionId = sessionId;
        Status = CogitationRunStatus.AwaitingContextApproval;
        ApprovalChanged?.Invoke();
    }

    internal void CollapseThinking()
    {
        lock (Sync) { CollapseThinkingLocked(); }
    }

    private void CollapseThinkingLocked()
    {
        foreach (var s in Reply.Sections)
            if (s.Type == MessageSection.SectionType.Thinking) s.Collapsed = true;
    }

    internal void SetStatus(CogitationRunStatus status) => Status = status;

    internal void RaiseCompleted() => Completed?.Invoke();

    // ── ICogitationStreamSink (wired into the adopted agent via Router) ────────

    public void ThinkingToken(string text)
    {
        lock (Sync)
        {
            var last = Reply.Sections.LastOrDefault();
            if (last?.Type == MessageSection.SectionType.Thinking) last.Text += text;
            else Reply.Sections.Add(new MessageSection { Type = MessageSection.SectionType.Thinking, Text = text });
        }
        Updated?.Invoke();
    }

    public void ToolStart(string name, string args)
    {
        lock (Sync)
        {
            Reply.Sections.Add(new MessageSection
            {
                Type     = MessageSection.SectionType.ToolActivity,
                ToolCall = new ToolCallInfo { Name = name, ArgsJson = args }
            });
        }
        StatusText = $"// TOOL: {name.ToUpperInvariant()}…";
        Updated?.Invoke();
    }

    public void ToolComplete(string name, string result, string? imageBase64 = null, string? imageMediaType = null, string? metadataJson = null)
    {
        lock (Sync)
        {
            var section = Reply.Sections.LastOrDefault(s =>
                s.Type == MessageSection.SectionType.ToolActivity &&
                s.ToolCall?.Name == name &&
                s.ToolCall?.Result == null);
            if (section != null)
            {
                section.ToolCall!.Result        = result;
                section.ToolCall.ImageBase64    = imageBase64;
                section.ToolCall.ImageMediaType = imageMediaType;
                section.ToolCall.MetadataJson   = metadataJson;
            }
        }
        StatusText = null;
        Updated?.Invoke();
    }

    public void TodoUpdate(IReadOnlyList<TodoItem> todos)
    {
        // Small models sometimes send status-only entries (no text) when updating; carry the
        // previous directive text forward by position so the checklist doesn't go blank.
        var previous = Manifest;
        Manifest = todos.Select((t, i) => new TodoItem
        {
            Text   = !string.IsNullOrWhiteSpace(t.Text) ? t.Text
                     : i < previous.Count ? previous[i].Text : "",
            Status = (t.Status ?? "pending").Trim().ToLowerInvariant().Replace('-', '_')
        }).ToList();
        Updated?.Invoke();
    }

    public async Task<bool> ApprovalRequestedAsync(ActionDescriptor descriptor, CancellationToken ct)
    {
        if (descriptor.Severity == ToolSeverity.NeedsSeal)
            return await RequestSealAsync(descriptor, ct);
        return await RequestInRunApprovalAsync(descriptor, ct);
    }

    private async Task<bool> RequestInRunApprovalAsync(ActionDescriptor descriptor, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingApproval = descriptor;
        _approvalTcs    = tcs;
        ApprovalChanged?.Invoke();

        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        bool approved;
        try   { approved = await tcs.Task.WaitAsync(linked.Token); }
        catch (OperationCanceledException) { approved = false; }

        PendingApproval = null;
        _approvalTcs    = null;
        ApprovalChanged?.Invoke();
        return approved;
    }

    private async Task<bool> RequestSealAsync(ActionDescriptor descriptor, CancellationToken ct)
    {
        var sealCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sealCts        = sealCts;
        PendingApproval = descriptor;
        ApprovalChanged?.Invoke();

        bool approved;
        try   { approved = await _sealService.RequestSealAsync(UserId, descriptor, sealCts.Token); }
        catch (OperationCanceledException) { approved = false; }
        finally
        {
            sealCts.Dispose();
            _sealCts        = null;
            PendingApproval = null;
            ApprovalChanged?.Invoke();
        }
        return approved;
    }

    /// <summary>Resolves a pending in-run gate. For an in-chat approval this settles the TCS; for a
    /// Seal gate (verdict comes from the node, not here) a "deny" instead cancels the wait, mirroring
    /// the component's REFUSE-cancels-the-seal-wait behavior.</summary>
    public void ResolveApproval(bool approved)
    {
        if (!approved) { try { _sealCts?.Cancel(); } catch { } }
        _approvalTcs?.TrySetResult(approved);
    }

    // ask_user: parks the tool call until the user answers (chosen option or typed text), skips, or
    // the same 2h window the approval gate uses elapses. Timeout/skip resolve to null — the tool
    // turns that into a "proceed with your best judgment" result instead of failing the run.
    public async Task<string?> AskUserAsync(string question, string[]? options, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingAskUser = new AskUserPrompt(question, options);
        _askUserTcs    = tcs;
        ApprovalChanged?.Invoke();

        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        string? answer;
        try   { answer = await tcs.Task.WaitAsync(linked.Token); }
        catch (OperationCanceledException) { answer = null; }

        PendingAskUser = null;
        _askUserTcs    = null;
        ApprovalChanged?.Invoke();
        return answer;
    }

    /// <summary>Settles a pending ask_user question with the user's answer (null = skipped).</summary>
    public void ResolveAskUser(string? answer) => _askUserTcs?.TrySetResult(answer);

    public void ContextApprovalRequested(string sessionId)
    {
        ContextApprovalSessionId = sessionId;
        Status = CogitationRunStatus.AwaitingContextApproval;
        ApprovalChanged?.Invoke();
    }
}
