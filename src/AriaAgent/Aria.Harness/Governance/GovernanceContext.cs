namespace Aria.Harness.Governance;

/// <summary>
/// Per-<c>AgentSession</c> governance state. Holds the active policy, the current turn's allowed
/// scope, per-turn budgets, and a short ring buffer of recent calls for loop detection.
/// One instance is shared (by reference) between the Harness and every <see cref="GovernedTool"/>
/// wrapping that session's tools. <see cref="BeginTurn"/> resets per-turn state.
/// </summary>
public sealed class GovernanceContext
{
    private const int RecentWindow = 16;

    private IReadOnlyList<string> _scope = [];
    private int _toolCalls;
    private int _fileReads;
    private int _mutations;
    private readonly LinkedList<string> _recent = new();

    /// <summary>The active policy. Re-applied each turn so a mode change takes effect on the existing
    /// session without rebuilding it.</summary>
    public GovernancePolicy Policy { get; private set; }

    public GovernanceContext(GovernancePolicy policy) => Policy = policy;

    public IReadOnlyList<string> AllowedScope => _scope;
    public int ToolCallsThisTurn => _toolCalls;
    public int FileReadsThisTurn => _fileReads;

    // ── Post-mutation verify nudge ──────────────────────────────────────────────────────────────
    // Per-turn state for GovernedTool's nudge: successful file mutations counted on one side, a
    // completed build/test verification on the other. While the first grows and the second stays
    // false, the nudge appends a reminder to mutation results (at 1, then every 5).
    public int  MutationsThisTurn => _mutations;
    public bool VerificationRan   { get; private set; }

    /// <summary>Record a successful file-mutation call and return the turn's running mutation
    /// count — the caller uses it for the nudge thresholds.</summary>
    public int RecordMutation() => ++_mutations;

    /// <summary>Mark that a build/test verification ran this turn, silencing the verify nudge.</summary>
    public void MarkVerificationRan() => VerificationRan = true;

    // ── Layer B seal pause ────────────────────────────────────────────────────────────────────
    // A sensitive tool hit the node gate with no live 8h grant. We can't let the exception bubble
    // through the function-invocation loop (Microsoft.Extensions.AI swallows it into a tool-result
    // error the model just retries), so GovernedTool stashes the signal here and terminates the loop;
    // the Harness re-raises it as ContextApprovalRequiredException AFTER the stream, above the framework.
    public bool    ContextApprovalPending   { get; private set; }
    public string? ContextApprovalSessionId { get; private set; }

    public void FlagContextApproval(string? sessionId)
    {
        ContextApprovalPending   = true;
        ContextApprovalSessionId = sessionId;
    }

    /// <summary>Reset per-turn counters and loop history; set the scope allowed for this turn, and
    /// (optionally) update the policy so a live mode change applies from this turn onward.</summary>
    public void BeginTurn(IReadOnlyList<string>? scope, GovernancePolicy? policy = null)
    {
        if (policy != null) Policy = policy;
        _scope     = scope ?? [];
        _toolCalls = 0;
        _fileReads = 0;
        _mutations = 0;
        VerificationRan = false;
        _recent.Clear();
        ContextApprovalPending   = false;   // cleared so a retry (grant now live) runs clean
        ContextApprovalSessionId = null;
    }

    /// <summary>Record an attempted call (always counts, even if later blocked) and return how many
    /// times this exact (name, args) has been seen in the current turn — used for loop detection.</summary>
    public int RecordAttempt(string name, string argsKey)
    {
        _toolCalls++;
        if (ToolCategories.IsFileRead(name)) _fileReads++;

        var key = name + "" + argsKey;
        _recent.AddLast(key);
        while (_recent.Count > RecentWindow) _recent.RemoveFirst();

        var count = 0;
        foreach (var k in _recent) if (k == key) count++;
        return count;
    }
}
