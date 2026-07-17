namespace Aria.Shared;

/// <summary>
/// Thrown when a sensitive operation is blocked because the bridge has no node-approved context grant.
/// Carries the session id the grant should be scoped to, so the UI can drive an in-chat approval
/// ceremony and retry the blocked turn.
/// </summary>
public sealed class ContextApprovalRequiredException : Exception
{
    public string? SessionId { get; }

    public ContextApprovalRequiredException(string? sessionId, string message)
        : base(message)
    {
        SessionId = sessionId;
    }
}
