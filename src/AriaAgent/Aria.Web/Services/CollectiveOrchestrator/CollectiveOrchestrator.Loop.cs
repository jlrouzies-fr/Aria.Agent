using Aria.Web.Data;

namespace Aria.Web.Services.CollectiveOrchestrator;

public partial class CollectiveOrchestrator
{
    // ── Main orchestration loop ───────────────────────────────────────────

    private async Task RunCollectiveAsync(int collectiveId, CancellationToken ct)
    {
        _logger.LogInformation("[HIVE] Collective {Id} starting", collectiveId);

        // Stamp every headless Overmind/drone call in this run with hive:{id}, so the grant the human
        // pre-authorised at launch (see PreauthorizeHiveAsync) clears the Layer B gate for the whole
        // fan-out — including drones bound to remote, unattended bridges.
        using var _hiveSession = Services.Agent.AgentBackgroundExecutor.WithAmbientSession($"hive:{collectiveId}");

        try
        {
            // Load collective config once at start
            AgentCollective? collective = await LoadCollectiveAsync(collectiveId);
            if (collective == null)
            {
                _logger.LogWarning("[HIVE] Collective {Id} not found", collectiveId);
                return;
            }

            // Bridge-owned collectives require their origin node to be online.
            if (!string.IsNullOrEmpty(collective.OriginNodeId) &&
                !_bridgeRegistry.GetNodes(collective.UserId).Any(n => n.NodeId == collective.OriginNodeId))
            {
                _logger.LogWarning("[HIVE] Collective {Id} origin node {Node} is offline", collectiveId, collective.OriginNodeId);
                await SetFailedAsync(collectiveId, "Collective's bridge node is offline. Connect the node to run this Hive.");
                await AppendEventAsync(collectiveId, CollectiveEventType.Failed,
                    "Collective's bridge node is offline. Connect the node to run this Hive.", null, null);
                FireChanged(collectiveId);
                return;
            }

            await SetStatusAsync(collectiveId, CollectiveStatus.Planning);
            await AppendEventAsync(collectiveId, CollectiveEventType.Info, "Collective starting — OVERMIND online.", null, null);
            FireChanged(collectiveId);

            string? feedback = collective.LastFeedback;

            while (!ct.IsCancellationRequested)
            {
                // Reload to get fresh CurrentRound
                collective = await LoadCollectiveAsync(collectiveId);
                if (collective == null) break;

                if (collective.CurrentRound >= collective.MaxRounds)
                {
                    _logger.LogInformation("[HIVE] Collective {Id} reached MaxRounds {Max}", collectiveId, collective.MaxRounds);
                    await SetFailedAsync(collectiveId, $"Maximum rounds ({collective.MaxRounds}) reached without completion.");
                    await AppendEventAsync(collectiveId, CollectiveEventType.Failed, $"Max rounds ({collective.MaxRounds}) reached.", null, null);
                    FireChanged(collectiveId);
                    break;
                }

                // Increment round
                int round = collective.CurrentRound + 1;
                await IncrementRoundAsync(collectiveId, round);
                collective = await LoadCollectiveAsync(collectiveId);
                if (collective == null) break;

                await SetStatusAsync(collectiveId, CollectiveStatus.Running);
                FireChanged(collectiveId);

                // ── PLAN ─────────────────────────────────────────────────────────
                ct.ThrowIfCancellationRequested();
                SetPhase(collectiveId, "Planning");
                bool planOk = collective.Behavior == CollectiveBehavior.HiveMind
                    ? await RunHiveMindPlanAsync(collective, round, feedback, ct)
                    : await RunPlanPhaseAsync(collective, round, feedback, ct);
                if (!planOk)
                {
                    await SetFailedAsync(collectiveId, "PLAN phase failed — could not parse Overmind directives.");
                    FireChanged(collectiveId);
                    break;
                }
                FireChanged(collectiveId);

                // ── DISPATCH ─────────────────────────────────────────────────────
                ct.ThrowIfCancellationRequested();
                SetPhase(collectiveId, "Dispatching");
                await RunDispatchPhaseAsync(collective, ct);
                FireChanged(collectiveId);

                // ── REVIEW ───────────────────────────────────────────────────────
                ct.ThrowIfCancellationRequested();
                SetPhase(collectiveId, "Reviewing");
                var (decision, summary, newFeedback) = await RunReviewPhaseAsync(collective, round, ct);

                switch (decision)
                {
                    case "COMPLETE":
                        await SetCompletedAsync(collectiveId, summary ?? "Objective completed.");
                        await AppendEventAsync(collectiveId, CollectiveEventType.Completed,
                            $"COMPLETE — {summary}", null, null);
                        FireChanged(collectiveId);
                        return;  // done

                    case "ABORT":
                        await SetFailedAsync(collectiveId, summary ?? "Overmind aborted.");
                        await AppendEventAsync(collectiveId, CollectiveEventType.Failed,
                            $"ABORT — {summary}", null, null);
                        FireChanged(collectiveId);
                        return;

                    default:  // CONTINUE
                        feedback = newFeedback;
                        // Persist feedback for resumability
                        await SaveFeedbackAsync(collectiveId, feedback);
                        await AppendEventAsync(collectiveId, CollectiveEventType.Reviewed,
                            $"CONTINUE — {newFeedback ?? "proceeding to next round."}", null, null);
                        FireChanged(collectiveId);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[HIVE] Collective {Id} loop cancelled (pause/stop).", collectiveId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HIVE] Collective {Id} loop crashed", collectiveId);
            try
            {
                await SetFailedAsync(collectiveId, $"Unexpected error: {ex.Message}");
                await AppendEventAsync(collectiveId, CollectiveEventType.Failed, $"Error: {ex.Message}", null, null);
                FireChanged(collectiveId);
            }
            catch { }
        }
        finally
        {
            ClearRunState(collectiveId);
            _loops.TryRemove(collectiveId, out _);
        }
    }
}
