namespace Aria.Shared;

/// <summary>
/// A bridge node's self-report of whether it holds a human-confirmed soul master key (the pinning
/// ceremony in Aria.Bridge's <c>SoulPinEndpoints</c>).
///
/// DISPLAY ONLY. This crosses the tunnel from node to server, so the server cannot verify it and
/// nothing may branch on it for trust. The check that matters — refusing grants until a human pinned
/// the key — runs locally on the node itself in <c>SiblingRoster.ResolveSoulMasterPublicKey</c>, and
/// a node lying here changes nothing about that. Its only purpose is to tell the human WHICH machine
/// still needs the ceremony, instead of leaving them to work out why seals stopped replicating there.
/// </summary>
public static class SoulKeyPinState
{
    /// <summary>Primary bridge, or a joined node whose pinned key matches the roster's primary.</summary>
    public const string Ok = "ok";

    /// <summary>Joined node with no human-confirmed soul key — it is refusing sibling grants.</summary>
    public const string Unpinned = "unpinned";

    /// <summary>The server presented a primary key differing from the one pinned on that node.</summary>
    public const string Mismatch = "mismatch";

    /// <summary>Offline, or connected by a bridge too old to report. No warning is shown.</summary>
    public const string Unknown = "unknown";

    /// <summary>Maps anything a node sends onto a known value, so an unexpected string can never
    /// reach the UI or widen the set of states the panel has to render.</summary>
    public static string Sanitize(string? reported) => reported switch
    {
        Ok or Unpinned or Mismatch => reported,
        _                          => Unknown,
    };

    /// <summary>True when the human needs to act on that machine.</summary>
    public static bool NeedsAttention(string? state) => state is Unpinned or Mismatch;
}
