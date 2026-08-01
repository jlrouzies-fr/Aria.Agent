namespace Aria.Harness.Governance;

/// <summary>The governance verdict for a single attempted tool call.</summary>
public enum ToolSeverity
{
    /// <summary>Runs immediately.</summary>
    Allowed,
    /// <summary>Refused outright — a synthetic result is returned so the model self-corrects.</summary>
    Blocked,
    /// <summary>Pauses for an in-chat human approval before running.</summary>
    NeedsApproval,
    /// <summary>Pauses for a node-signed Inquisitorial Seal before running.</summary>
    NeedsSeal
}

/// <summary>
/// A classified, human-readable description of an attempted tool call — surfaced to the
/// approval UI and used to build the refusal/denial message.
/// </summary>
/// <param name="Diff">
/// Prospective unified diff for a paused file mutation (fetched from the bridge, read-only).
/// Null when the preview was unavailable or the tool is not a file mutation — the approval UI
/// then falls back to <see cref="ArgsPreview"/>.
/// </param>
public sealed record ActionDescriptor(
    string       ToolName,
    string       ArgsPreview,
    string       Reason,
    string?      TargetPath,
    ToolSeverity Severity,
    string?      Diff = null);
