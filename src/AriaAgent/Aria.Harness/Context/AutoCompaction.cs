namespace Aria.Harness.Context;

/// <summary>
/// Auto-compaction decision logic: given the observed context size (a reported prompt-token count
/// when the model source returns usage, else a character-based estimate) and the session's
/// threshold, decide whether the conversation should be summarised before the next turn.
/// Pure logic, host-agnostic — the chat layer only supplies the numbers and executes the result.
/// </summary>
public static class AutoCompaction
{
    /// <summary>Default trigger, in estimated tokens. No model context-window size is discoverable
    /// from the configured sources today, so this is a deliberately conservative fixed default.</summary>
    public const int DefaultThresholdTokens = 100_000;

    /// <summary>Heuristic chars-per-token ratio used when a source reports no usage (~4 chars/token).</summary>
    public const int CharsPerToken = 4;

    /// <summary>Estimate a token count from a character count (chars/4, rounded up).</summary>
    public static long EstimateTokens(long chars) =>
        chars <= 0 ? 0 : (chars + CharsPerToken - 1) / CharsPerToken;

    /// <summary>The effective threshold for a session: the override when set, else the default.
    /// A value &lt;= 0 disables auto-compaction (set via "/compact auto off").</summary>
    public static int ResolveThreshold(int? sessionOverride) =>
        sessionOverride ?? DefaultThresholdTokens;

    /// <summary>
    /// Should the conversation be compacted before the next turn?
    /// <paramref name="reportedInputTokens"/> is the prompt-token count the model source reported
    /// for the last response (null when the source returns no usage); when absent the transcript
    /// size is estimated from <paramref name="transcriptChars"/>. A reported count always wins —
    /// it reflects the real context window, including tool results the transcript mirror may miss.
    /// </summary>
    public static bool ShouldCompact(int? reportedInputTokens, long transcriptChars, int? thresholdOverride)
    {
        var threshold = ResolveThreshold(thresholdOverride);
        if (threshold <= 0) return false;   // "/compact auto off"

        var tokens = reportedInputTokens ?? EstimateTokens(transcriptChars);
        return tokens >= threshold;
    }
}
