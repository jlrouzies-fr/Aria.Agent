namespace Aria.Harness.Governance;

/// <summary>How strictly the agent's tool use is governed.</summary>
public enum GovernanceMode
{
    /// <summary>No governance — every tool call runs unchecked (legacy behaviour).</summary>
    Off,
    /// <summary>Generous budgets, loop detection, and out-of-scope calls ask for approval.</summary>
    Balanced,
    /// <summary>Tight budgets, scope lock (out-of-scope blocked), mutations require approval.</summary>
    Strict,
    /// <summary>As Strict, but high-stakes actions require a node-signed Inquisitorial Seal.</summary>
    Paranoid
}

/// <summary>How the scope lock treats a tool call whose target path is outside the allowed scope.</summary>
public enum ScopeEnforcement { Off, Approve, Block }

/// <summary>
/// The concrete limits and gating behaviour derived from a <see cref="GovernanceMode"/>.
/// Pure data — no host or bridge concerns.
/// </summary>
public sealed record GovernancePolicy(
    GovernanceMode    Mode,
    int               MaxToolCallsPerTurn,
    int               MaxFileReadsPerTurn,
    ScopeEnforcement  Scope,
    bool              ApproveMutations,
    bool              SealHighStakes,
    int               LoopThreshold)
{
    public static GovernancePolicy FromMode(GovernanceMode mode) => mode switch
    {
        GovernanceMode.Off      => new(mode, int.MaxValue, int.MaxValue, ScopeEnforcement.Off,     false, false, int.MaxValue),
        GovernanceMode.Balanced => new(mode, 30,           18,           ScopeEnforcement.Approve, false, false, 3),
        GovernanceMode.Strict   => new(mode, 12,           6,            ScopeEnforcement.Block,   true,  false, 3),
        GovernanceMode.Paranoid => new(mode, 8,            4,            ScopeEnforcement.Block,   true,  true,  2),
        _                       => FromMode(GovernanceMode.Balanced)
    };
}
