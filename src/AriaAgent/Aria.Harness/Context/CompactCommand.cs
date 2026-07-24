namespace Aria.Harness.Context;

/// <summary>What a parsed <c>/compact</c> chat command asks for.</summary>
public enum CompactCommandKind { Manual, Status, SetThreshold, Disable, Invalid }

/// <summary>Parsed form of a <c>/compact</c> command — pure data, no host concerns.</summary>
public sealed record CompactCommandParse(
    CompactCommandKind Kind,
    int?               Threshold = null,
    string?            Error     = null);

/// <summary>
/// Parses the argument text of the <c>/compact</c> chat command: bare is the manual summarisation
/// flow, <c>auto</c> shows the auto-compaction status, <c>auto &lt;n&gt;</c> sets the per-session
/// threshold in tokens, and <c>auto off</c> disables it. Pure logic so the chat layer stays a thin
/// executor and the grammar is unit-testable.
/// </summary>
public static class CompactCommand
{
    public const string Usage =
        "usage: /compact — summarise now · /compact auto — show auto-compaction status · " +
        "/compact auto <tokens> — set the session threshold · /compact auto off — disable";

    public static CompactCommandParse Parse(string? args)
    {
        var text = (args ?? "").Trim();
        if (text.Length == 0)
            return new(CompactCommandKind.Manual);

        if (!text.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
            return new(CompactCommandKind.Invalid, Error: Usage);

        var rest = text["auto".Length..].Trim();
        if (rest.Length == 0)
            return new(CompactCommandKind.Status);

        if (rest.Equals("off", StringComparison.OrdinalIgnoreCase))
            return new(CompactCommandKind.Disable);

        if (int.TryParse(rest, out var n) && n > 0)
            return new(CompactCommandKind.SetThreshold, Threshold: n);

        return new(CompactCommandKind.Invalid, Error: Usage);
    }

    /// <summary>One-line status of the session's auto-compaction setting.</summary>
    public static string Describe(int? sessionOverride)
    {
        if (sessionOverride == 0)
            return "// COMPACT: auto-compaction OFF — the context window will not be summarised automatically //";

        var threshold = AutoCompaction.ResolveThreshold(sessionOverride);
        var origin    = sessionOverride == null ? " (default)" : "";
        return $"// COMPACT: auto-compaction ON — triggers at ~{threshold:N0} tokens{origin} · " +
               "/compact auto <tokens> to change · /compact auto off to disable //";
    }
}
