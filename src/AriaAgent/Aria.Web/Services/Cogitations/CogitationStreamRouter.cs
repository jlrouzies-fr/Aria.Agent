using Aria.Harness.Governance;
using Aria.Tools;

namespace Aria.Web.Services.Cogitations;

/// <summary>
/// The callbacks a streaming turn reports to (thinking tokens, tool activity, the task manifest,
/// approval gates). Implemented by both the Chat component (for the live/foreground view) and by
/// <see cref="CogitationRun"/> (for a run detached from any circuit).
/// </summary>
public interface ICogitationStreamSink
{
    void ThinkingToken(string text);
    void ToolStart(string name, string args);
    void ToolComplete(string name, string result, string? imageBase64 = null, string? imageMediaType = null, string? metadataJson = null);
    void TodoUpdate(IReadOnlyList<TodoItem> todos);
    Task<bool> ApprovalRequestedAsync(ActionDescriptor descriptor, CancellationToken ct);
    Task<string?> AskUserAsync(string question, string[]? options, CancellationToken ct);
    void ContextApprovalRequested(string sessionId);
}

/// <summary>
/// A retargetable indirection between a live <c>AIAgent</c>/<c>AgentSession</c> (whose tool wrappers
/// capture these callbacks once, at construction — see <c>Harness.CreateSessionAsync</c>) and whoever
/// is currently consuming the stream: the Chat component while attached, or a <see cref="CogitationRun"/>
/// once the component detaches (navigation, refresh) or before a component reattaches. A null target
/// drops events silently rather than throwing, so an unattached run's tool calls don't fault.
/// </summary>
public sealed class CogitationStreamRouter : ICogitationStreamSink
{
    private ICogitationStreamSink? _target;

    public ICogitationStreamSink? Target
    {
        get => Volatile.Read(ref _target);
        set => Volatile.Write(ref _target, value);
    }

    public void ThinkingToken(string text) => Target?.ThinkingToken(text);
    public void ToolStart(string name, string args) => Target?.ToolStart(name, args);
    public void ToolComplete(string name, string result, string? imageBase64 = null, string? imageMediaType = null, string? metadataJson = null) =>
        Target?.ToolComplete(name, result, imageBase64, imageMediaType, metadataJson);
    public void TodoUpdate(IReadOnlyList<TodoItem> todos) => Target?.TodoUpdate(todos);

    public Task<bool> ApprovalRequestedAsync(ActionDescriptor descriptor, CancellationToken ct) =>
        Target?.ApprovalRequestedAsync(descriptor, ct) ?? Task.FromResult(false);

    // No listener (unattached run): answer null — the tool returns "user did not answer" and the
    // agent proceeds on its best judgment rather than hanging the turn for the full timeout.
    public Task<string?> AskUserAsync(string question, string[]? options, CancellationToken ct) =>
        Target?.AskUserAsync(question, options, ct) ?? Task.FromResult<string?>(null);

    public void ContextApprovalRequested(string sessionId) => Target?.ContextApprovalRequested(sessionId);
}
