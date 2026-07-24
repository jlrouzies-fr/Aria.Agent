namespace Aria.Harness.Governance;

/// <summary>What a parsed <c>/governance</c> chat command asks for.</summary>
public enum GovernanceCommandKind { Status, SwitchMode, SetBudget, ResetBudget, Invalid }

/// <summary>Parsed form of a <c>/governance</c> command — pure data, no host concerns.</summary>
public sealed record GovernanceCommandParse(
    GovernanceCommandKind Kind,
    GovernanceMode?       Mode  = null,
    int?                  Tools = null,
    int?                  Reads = null,
    string?               Error = null);

/// <summary>
/// Parses the argument text of the <c>/governance</c> chat command:
/// bare shows status, a mode name switches mode, and <c>budget tools=N reads=N</c> sets
/// per-session budget overrides (<c>budget reset</c> clears them). Pure logic so the chat
/// layer stays a thin executor and the grammar is unit-testable.
/// </summary>
public static class GovernanceCommand
{
    public const string Usage =
        "usage: /governance — show mode + budgets · /governance <off|balanced|coding|plan|strict|paranoid> — " +
        "switch mode · /governance budget tools=<n> reads=<n> — session overrides · /governance budget reset — clear";

    public static GovernanceCommandParse Parse(string? args)
    {
        var text = (args ?? "").Trim();
        if (text.Length == 0)
            return new(GovernanceCommandKind.Status);

        if (text.StartsWith("budget", StringComparison.OrdinalIgnoreCase))
        {
            var rest = text["budget".Length..].Trim();
            if (rest.Equals("reset", StringComparison.OrdinalIgnoreCase))
                return new(GovernanceCommandKind.ResetBudget);

            int? tools = null, reads = null;
            foreach (var token in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = token.IndexOf('=');
                if (eq <= 0 || !int.TryParse(token[(eq + 1)..], out var n) || n <= 0)
                    return new(GovernanceCommandKind.Invalid, Error: Usage);

                if (token[..eq].Equals("tools", StringComparison.OrdinalIgnoreCase))      tools = n;
                else if (token[..eq].Equals("reads", StringComparison.OrdinalIgnoreCase)) reads = n;
                else return new(GovernanceCommandKind.Invalid, Error: Usage);
            }

            return tools == null && reads == null
                ? new(GovernanceCommandKind.Invalid, Error: Usage)
                : new(GovernanceCommandKind.SetBudget, Tools: tools, Reads: reads);
        }

        if (Enum.TryParse<GovernanceMode>(text, ignoreCase: true, out var mode) && Enum.IsDefined(mode))
            return new(GovernanceCommandKind.SwitchMode, Mode: mode);

        return new(GovernanceCommandKind.Invalid, Error: Usage);
    }

    /// <summary>One-line status of the effective policy — budgets, scope and mutation behaviour.</summary>
    public static string Describe(GovernancePolicy p, bool hasOverrides)
    {
        string calls = p.MaxToolCallsPerTurn == int.MaxValue ? "unlimited" : p.MaxToolCallsPerTurn.ToString();
        string reads = p.MaxFileReadsPerTurn == int.MaxValue ? "unlimited" : p.MaxFileReadsPerTurn.ToString();
        var mutations = p.BlockMutations ? "mutations blocked"
                      : p.ApproveMutations ? "mutations ask"
                      :                      "mutations free";
        var note = hasOverrides ? " · session budget overrides active (/governance budget reset to clear)" : "";
        return $"// GOVERNANCE: {p.Mode.ToString().ToUpperInvariant()} — {calls} tool calls/turn · " +
               $"{reads} file reads/turn · {mutations}{note} //";
    }
}
