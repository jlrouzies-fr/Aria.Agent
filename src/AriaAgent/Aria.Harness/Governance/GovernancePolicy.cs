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
    Paranoid,
    /// <summary>Roomy budgets for real multi-file coding work; out-of-scope calls still ask for approval.</summary>
    Coding,
    /// <summary>Read-only exploration — mutations are blocked so the agent presents a plan first.</summary>
    Plan
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
    bool              BlockMutations,
    int               LoopThreshold)
{
    public static GovernancePolicy FromMode(GovernanceMode mode) => mode switch
    {
        GovernanceMode.Off      => new(mode, int.MaxValue, int.MaxValue, ScopeEnforcement.Off,     false, false, false, int.MaxValue),
        GovernanceMode.Balanced => new(mode, 30,           18,           ScopeEnforcement.Approve, false, false, false, 3),
        GovernanceMode.Strict   => new(mode, 12,           6,            ScopeEnforcement.Block,   true,  false, false, 3),
        GovernanceMode.Paranoid => new(mode, 8,            4,            ScopeEnforcement.Block,   true,  true,  false, 2),
        GovernanceMode.Coding   => new(mode, 60,           40,           ScopeEnforcement.Approve, false, false, false, 4),
        GovernanceMode.Plan     => new(mode, 40,           40,           ScopeEnforcement.Approve, false, false, true,  3),
        _                       => FromMode(GovernanceMode.Balanced)
    };

    /// <summary>
    /// Fleet routing gate: when true (and the mode is not Off), a tool call that the multi-node
    /// dispatcher resolves to a bridge OTHER than the session's default node escalates to an
    /// approval, so the user signs off the agent's cross-machine decision (which the agent made
    /// from fleet_status assumptions). Not part of the FromMode presets — hosts layer it on top
    /// of the user's mode via <c>with</c>. Enforced by GovernedTool.
    /// </summary>
    public bool ApproveCrossNodeCalls { get; init; }

    /// <summary>
    /// Post-mutation verify nudge: while a turn accumulates successful file mutations without a
    /// build/test verification, GovernedTool appends a one-line "consider verifying" reminder to
    /// the mutation's own result (at 1, then every 5). Advisory only — never blocks, never fails
    /// a call, never counts against budgets. Default ON in every mode; hosts layer the
    /// <c>Governance:VerifyNudge</c> config toggle on top via <c>with</c>.
    /// </summary>
    public bool VerifyNudge { get; init; } = true;

    /// <summary>Per-session budget overrides layered on top of the mode's defaults — a null leaves
    /// the mode's own limit in place. Session-scoped only; never persisted.</summary>
    public GovernancePolicy WithBudgetOverrides(int? maxToolCalls, int? maxFileReads) =>
        this with
        {
            MaxToolCallsPerTurn = maxToolCalls ?? MaxToolCallsPerTurn,
            MaxFileReadsPerTurn = maxFileReads ?? MaxFileReadsPerTurn
        };
}
