namespace Aria.Harness.Core;

/// <summary>
/// Per-operation context for a harness call.
/// This is intentionally small; the host runtime carries the heavy state.
/// </summary>
public sealed class HarnessContext
{
    /// <summary>Server-side user identity, when available.</summary>
    public string? UserId { get; set; }

    /// <summary>Bridge-side user identity (may differ from server user id).</summary>
    public string? BridgeUserId { get; set; }

    /// <summary>Optional target bridge node id. When set, bridge-backed LLM/tool calls route to this node.</summary>
    public string? BridgeNodeId { get; set; }

    /// <summary>Optional browser-session id. Stamped on sensitive bridge calls so the node's Layer B
    /// gate can scope a context grant to this session rather than the whole soul.</summary>
    public string? SessionId { get; set; }

    public CancellationToken CancellationToken { get; set; }

    public static HarnessContext Empty { get; } = new();
}
