namespace Aria.Harness.Core;

/// <summary>
/// How the agent recalls Noosphere memory when a soul has more than one connected node. Memory stores
/// are always node-local (one SQLite vault per bridge, never replicated); this only governs how many of
/// those vaults a single Probe/Contemplate call reads from.
/// </summary>
public enum RecallScope
{
    /// <summary>Recall only from the node running the LLM turn (default). Deterministic, minimal exposure.</summary>
    ThisNode,

    /// <summary>Fan the query out to every connected node for the soul and merge the results by score.
    /// Lets the agent remember what was inscribed on any of the user's machines.</summary>
    AllNodes,
}
