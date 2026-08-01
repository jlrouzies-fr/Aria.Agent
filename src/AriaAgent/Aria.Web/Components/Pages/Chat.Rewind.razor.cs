using System.Text;
using Aria.Harness.Context;
using Aria.Web.Services.Chat;

namespace Aria.Web.Components.Pages;

/// <summary>
/// The "/rewind" chat command: bare reverts the most recent mutating turn captured in the
/// transcript, and "/rewind &lt;n&gt;" reverts the nth recent mutating turn back. Runs locally —
/// never reaches the agent.
/// </summary>
public partial class Chat
{
    private const int MaxRewindDepth = 5;

    private async Task HandleRewindCommandAsync(string args)
    {
        var cmd = RewindCommand.Parse(args);
        if (cmd.Kind == RewindCommandKind.Invalid)
        {
            _messages.Add(new MessageEntry("system", $"// REWIND: {cmd.Error} //"));
            await InvokeAsync(StateHasChanged);
            await ScrollToBottomAsync();
            return;
        }

        var checkpoints = RecentRewindTargets().ToList();
        if (checkpoints.Count == 0)
        {
            _messages.Add(new MessageEntry("system",
                "// REWIND: no unreverted mutating turns are available in this transcript //"));
            await InvokeAsync(StateHasChanged);
            await ScrollToBottomAsync();
            return;
        }

        var steps = cmd.Steps ?? 1;
        if (steps > MaxRewindDepth)
        {
            _messages.Add(new MessageEntry("system",
                $"// REWIND: only the last {MaxRewindDepth} mutating turns may be selected explicitly //"));
            await InvokeAsync(StateHasChanged);
            await ScrollToBottomAsync();
            return;
        }

        if (steps > checkpoints.Count)
        {
            _messages.Add(new MessageEntry("system",
                $"// REWIND: only {checkpoints.Count} unreverted mutating turn(s) are currently available //"));
            await InvokeAsync(StateHasChanged);
            await ScrollToBottomAsync();
            return;
        }

        await RewindCheckpointAsync(checkpoints[steps - 1].Checkpoint);
    }

    private async Task RewindCheckpointAsync(string checkpoint)
    {
        var userId = BridgeUserId();
        if (userId == null) return;

        _messages.Add(new MessageEntry("system",
            "// REWIND: asking the bridge to revert this turn's file mutations atomically //"));
        await InvokeAsync(StateHasChanged);
        await ScrollToBottomAsync();

        var result = await ProjectFiles.RevertCheckpointAsync(
            userId, checkpoint, SessionState.AllowedProjectPaths, sessionId: SessionState.SessionToken);
        if (result == null)
        {
            _messages.Add(new MessageEntry("system",
                "// REWIND FAILED: bridge returned no response //"));
            await InvokeAsync(StateHasChanged);
            await ScrollToBottomAsync();
            return;
        }

        var revertedUndoTokens = result.Results
            .Where(r => r.Status == "reverted")
            .Select(r => r.UndoToken)
            .ToHashSet(StringComparer.Ordinal);

        // Reuse the same explorer/viewer auto-refresh path a single-file REVERT uses — otherwise
        // the tree and any open viewer keep showing stale (pre-rewind) content until manually
        // reopened, since a bridge-side revert never round-trips through the normal tool-call flow.
        foreach (var path in result.Results.Where(r => r.Status == "reverted").Select(r => r.Path).Distinct(StringComparer.Ordinal))
        {
            var refreshArgs = System.Text.Json.JsonSerializer.Serialize(new { path });
            await HandleFileToolCompletedAsync("write_file", refreshArgs);
        }

        foreach (var msg in _messages)
        {
            var changed = false;
            foreach (var tc in msg.ToolCalls)
            {
                var metadata = ParseFileMutationMetadata(tc.MetadataJson);
                if (metadata is null || metadata.Checkpoint != checkpoint || !revertedUndoTokens.Contains(metadata.UndoToken))
                    continue;

                tc.MetadataJson = System.Text.Json.JsonSerializer.Serialize(
                    metadata with { Reverted = true }, MetadataJsonOptions);
                changed = true;
            }

            if (changed)
                await PersistToolCardStateAsync(msg, userId);
        }

        var sb = new StringBuilder();
        sb.Append($"// REWIND {checkpoint[..Math.Min(8, checkpoint.Length)]}: ");
        sb.Append($"{result.Reverted} reverted");
        if (result.Skipped > 0) sb.Append($", {result.Skipped} skipped");
        if (result.Missing > 0) sb.Append($", {result.Missing} missing");

        foreach (var entry in result.Results.Where(r => r.Status != "reverted"))
            sb.Append($"\n   • {entry.Path} — {entry.Status}: {entry.Detail}");
        sb.Append(" //");

        _messages.Add(new MessageEntry("system", sb.ToString()));
        await InvokeAsync(StateHasChanged);
        await ScrollToBottomAsync();
    }

    private IEnumerable<(string Checkpoint, DateTime Timestamp)> RecentRewindTargets()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var msg in Enumerable.Reverse(_messages))
        {
            foreach (var tc in Enumerable.Reverse(msg.ToolCalls))
            {
                var metadata = ParseFileMutationMetadata(tc.MetadataJson);
                if (metadata is null || metadata.Reverted || string.IsNullOrWhiteSpace(metadata.Checkpoint))
                    continue;

                if (seen.Add(metadata.Checkpoint))
                    yield return (metadata.Checkpoint, msg.Timestamp);
            }
        }
    }
}
