namespace Aria.Harness.Context;

/// <summary>
/// The inputs the <c>context_status</c> tool reports on. Supplied by the host per session
/// (the chat layer reads its transcript mirror, the session's threshold override, and the
/// last reported usage); assembly of the report stays host-agnostic for unit testing.
/// </summary>
public sealed record ContextStatusSnapshot(
    int? LastInputTokens,
    long TranscriptChars,
    int? ThresholdOverride,
    int MessageCount,
    int ToolCallCount);

/// <summary>
/// Builds the <c>context_status</c> report: the last reported input-token count when the
/// model source returned usage, a char-based transcript estimate, the effective auto-compact
/// threshold, and how close the session is to it. The pressure percentage uses the reported
/// count when available (it reflects the real context window), else the estimate — the same
/// precedence <see cref="AutoCompaction.ShouldCompact"/> applies.
/// </summary>
public static class ContextStatusReport
{
    // Invariant formatting: the report is model-facing and unit-tested — no locale-dependent
    // group/decimal separators.
    public static string Build(ContextStatusSnapshot s)
    {
        var estimatedTokens = AutoCompaction.EstimateTokens(s.TranscriptChars);
        var threshold       = AutoCompaction.ResolveThreshold(s.ThresholdOverride);

        var lines = new List<string>
        {
            "Context status:",
            s.LastInputTokens is { } reported
                ? Invariant($"- Last reported input tokens: {reported:N0} (reported by the model source for the previous response)")
                : "- Last reported input tokens: none yet (the model source has reported no usage this session)",
            Invariant($"- Estimated transcript tokens: ~{estimatedTokens:N0} (chars/{AutoCompaction.CharsPerToken} heuristic over {s.TranscriptChars:N0} chars)"),
        };

        if (threshold <= 0)
        {
            lines.Add("- Auto-compact threshold: disabled (auto-compaction is off for this session)");
        }
        else
        {
            var current   = s.LastInputTokens ?? estimatedTokens;
            var usedPct   = (double)current / threshold * 100;
            var source    = s.LastInputTokens.HasValue ? "reported" : "estimated";
            lines.Add(Invariant($"- Auto-compact threshold: {threshold:N0} tokens") +
                      (s.ThresholdOverride.HasValue ? " (session override)" : " (default)"));
            lines.Add(Invariant($"- Context pressure: {usedPct:F1}% of the threshold used ({Math.Max(0, 100 - usedPct):F1}% headroom), based on the {source} count"));
        }

        lines.Add(Invariant($"- Messages this session: {s.MessageCount} (tool calls: {s.ToolCallCount})"));
        return string.Join('\n', lines);
    }

    private static string Invariant(FormattableString text) =>
        FormattableString.Invariant(text);
}
