using Aria.Harness.Context;

namespace Aria.Harness.Core;

/// <summary>
/// Per-operation context for a harness call.
/// This is intentionally small; the host runtime carries the heavy state.
/// </summary>
public sealed class HarnessContext
{
    // Per-turn checkpoint id flows across awaits within a cogitation run loop (and headless child
    // runs). BridgeMcpTool stamps it onto every /tools/call so FileUndo rows can be batch-reverted
    // by /rewind. AsyncLocal (not an instance field) because tools capture this context at session
    // create and the same agent is reused across turns — the turn id must change without rebuilding
    // the tool graph.
    private static readonly AsyncLocal<string?> TurnCheckpointLocal = new();

    /// <summary>Checkpoint id for the currently executing agent turn, or null outside a turn.</summary>
    public static string? CurrentTurnCheckpoint
    {
        get => TurnCheckpointLocal.Value;
        set => TurnCheckpointLocal.Value = value;
    }

    /// <summary>Server-side user identity, when available.</summary>
    public string? UserId { get; set; }

    /// <summary>Bridge-side user identity (may differ from server user id).</summary>
    public string? BridgeUserId { get; set; }

    /// <summary>Optional target bridge node id. When set, bridge-backed LLM/tool calls route to this node.</summary>
    public string? BridgeNodeId { get; set; }

    /// <summary>Optional browser-session id. Stamped on sensitive bridge calls so the node's Layer B
    /// gate can scope a context grant to this session rather than the whole soul.</summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Resolved context window for the active source+model. Set during session creation and passed
    /// to bridge tool calls (e.g. read_file) so they can enforce known-window guards.
    /// </summary>
    public ContextWindow? ContextWindow { get; set; }

    public CancellationToken CancellationToken { get; set; }

    public static HarnessContext Empty { get; } = new();
}
