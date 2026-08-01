using Aria.Harness.Context;

namespace Aria.Web.Components.Pages;

/// <summary>
/// The "/compact" chat command: bare runs the manual summarisation flow (same confirmation the
/// palette opens), "auto" shows the auto-compaction status, "auto &lt;tokens&gt;" sets a per-session
/// threshold, and "auto off" disables it. Runs locally — never reaches the agent.
/// </summary>
public partial class Chat
{
    private async Task HandleCompactCommandAsync(string args)
    {
        var cmd = CompactCommand.Parse(args);
        string note;

        switch (cmd.Kind)
        {
            case CompactCommandKind.Manual:
                // Bare "/compact" typed out — behave exactly like the palette entry.
                _compactConfirmOpen = true;
                await InvokeAsync(StateHasChanged);
                return;

            case CompactCommandKind.SetThreshold:
                SessionState.AutoCompactThreshold = cmd.Threshold;
                note = CompactCommand.Describe(SessionState.AutoCompactThreshold);
                break;

            case CompactCommandKind.Disable:
                SessionState.AutoCompactThreshold = 0;
                note = CompactCommand.Describe(SessionState.AutoCompactThreshold);
                break;

            case CompactCommandKind.Invalid:
                note = $"// COMPACT: {cmd.Error} //";
                break;

            default: // Status
                note = CompactCommand.Describe(SessionState.AutoCompactThreshold);
                break;
        }

        _messages.Add(new MessageEntry("system", note));
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Auto-compaction, fired from OnRunCompleted once a turn has fully finished (never mid-tool-loop):
    /// when the context crossed the session threshold, run the same summarisation flow "/compact"
    /// uses and continue on the fresh session. A system note marks where it happened.
    /// </summary>
    private async Task AutoCompactAsync()
    {
        await CompactAsync();

        // CompactAsync no-ops (leaving the transcript intact) when there is nothing to compact or the
        // summarisation failed — only annotate when the transcript was actually replaced by a summary.
        if (_messages.Count == 1 && _messages[0].IsCompactSummary)
        {
            _messages.Add(new MessageEntry("system",
                "// CONTEXT COMPACTED AUTOMATICALLY — the conversation crossed the auto-compaction " +
                "threshold and was summarised · /compact auto off to disable //"));
            await InvokeAsync(StateHasChanged);
        }
    }

    // Estimated transcript size for the compaction fallback: visible content plus tool call args and
    // results (the big context consumers in a coding session). Used only when the model source
    // reports no token usage for the turn.
    private long TranscriptChars() =>
        _messages.Where(m => m.Role != "system").Sum(m =>
            (long)m.Content.Length + m.ToolCalls.Sum(t => (long)(t.Result?.Length ?? 0) + t.ArgsJson.Length));

    // Per-session snapshot for the always-on context_status tool: last reported usage (null when the
    // source returns none), the same transcript estimate auto-compaction falls back to, the session's
    // threshold override, and cheap message/tool-call counts. Assembly of the report itself lives in
    // Aria.Harness (ContextStatusReport) so it stays host-agnostic.
    private ContextStatusSnapshot BuildContextStatusSnapshot() =>
        new(_messages.LastOrDefault(m => m.InputTokens.HasValue)?.InputTokens,
            TranscriptChars(),
            SessionState.AutoCompactThreshold,
            _messages.Count,
            _messages.Sum(m => m.ToolCalls.Count),
            _effectiveContextWindow);
}
