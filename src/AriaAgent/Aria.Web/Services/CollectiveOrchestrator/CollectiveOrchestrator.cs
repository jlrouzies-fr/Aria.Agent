using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Aria.Web.Services.CollectiveOrchestrator;

// ── Plan DTOs ─────────────────────────────────────────────────────────────────

internal record PlanDirective(
    string Title,
    int    AssignedMemberId,
    string Instruction,
    int[]? DependsOn);

internal record PlanResponse(PlanDirective[] Directives);

internal record ReviewResponse(string Decision, string? Summary, string? Feedback);

// ── Orchestrator ─────────────────────────────────────────────────────────────

/// <summary>
/// Singleton hosted service that drives collective (Hive) orchestration loops.
/// Each collective gets its own loop task + CancellationTokenSource.
/// </summary>
public partial class CollectiveOrchestrator : IHostedService
{
    private const int MaxTasksPerRound   = 12;
    private const int MaxDroneParallel   = 3;

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly AgentBackgroundExecutor           _executor;
    private readonly BridgeHiveClient                  _bridgeHive;
    private readonly ModelBridgeRegistry               _bridgeRegistry;
    private readonly ILogger<CollectiveOrchestrator>   _logger;
    private readonly ConcurrentDictionary<int, CancellationTokenSource>       _loops      = new();
    private readonly SemaphoreSlim                                             _droneSemaphore = new(MaxDroneParallel);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string?>>           _humanGates  = new();
    private readonly ConcurrentDictionary<(int, int), TaskCompletionSource<string?>>   _memberGates = new();

    // collectiveId → (userId, approvalNode) for a "this run only" Layer B seal that must be revoked when
    // the run ends, so the next launch re-asks. Absent for durationed seals (those lapse on their own).
    private readonly ConcurrentDictionary<int, (string UserId, string? Node)> _oneShotSeals = new();

    /// <summary>Registers (or clears, when <paramref name="seal"/> is null) a one-shot Hive seal to revoke
    /// at run completion. Set from the Hive launch after the human chose "this run only".</summary>
    public void SetOneShotSeal(int collectiveId, (string UserId, string? Node)? seal)
    {
        if (seal is { } s) _oneShotSeals[collectiveId] = s;
        else               _oneShotSeals.TryRemove(collectiveId, out _);
    }

    // Retire a one-shot seal on the node it was signed on. Called from every run-ending path
    // (complete / fail) so a "this run only" grant never survives into the next launch.
    private async Task RevokeOneShotSealAsync(int collectiveId)
    {
        if (!_oneShotSeals.TryRemove(collectiveId, out var seal)) return;
        var body = JsonSerializer.Serialize(new { session = $"hive:{collectiveId}" });
        try { await _bridgeRegistry.SendLocalRestAsync(seal.UserId, "POST", "/context/grants/revoke", body, seal.Node); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to revoke one-shot Hive seal for collective {Id}", collectiveId); }
    }

    // Fires with collectiveId when any collective state changes
    public event Action<int>? OnCollectiveChanged;
    // Fires with cogitationId when a human-in-the-loop gate opens/closes
    public event Action<int>? OnHiveGatePending;
    public event Action<int>? OnHiveGateResolved;
    // Fires with (cogitationId, memberId, droneName, pendingContent) for per-drone gates
    // pendingContent = instruction (pre-dispatch) or drone reply (post-response)
    public event Action<int, int, string, string?>? OnHiveMemberGatePending;
    public event Action<int, int>?                  OnHiveMemberGateResolved;

    // ── Live run state (drives the canvas animation) ──────────────────────────
    public enum DroneRunState { Idle, Running, AwaitingGate, Done, Skipped }

    // collectiveId → current phase ("", "Planning", "Dispatching", "Synthesising")
    private readonly ConcurrentDictionary<int, string> _phase = new();
    // (collectiveId, memberId) → drone state during a run
    private readonly ConcurrentDictionary<(int, int), DroneRunState> _droneState = new();

    /// <summary>Fires (collectiveId) whenever the live run phase or a drone's state changes.</summary>
    public event Action<int>? OnHiveRunStateChanged;

    public string GetPhase(int collectiveId) => _phase.GetValueOrDefault(collectiveId, "");
    public DroneRunState GetDroneState(int collectiveId, int memberId) =>
        _droneState.GetValueOrDefault((collectiveId, memberId), DroneRunState.Idle);

    private void SetPhase(int collectiveId, string phase)
    {
        _phase[collectiveId] = phase;
        OnHiveRunStateChanged?.Invoke(collectiveId);
    }
    private void SetDrone(int collectiveId, int memberId, DroneRunState s)
    {
        _droneState[(collectiveId, memberId)] = s;
        OnHiveRunStateChanged?.Invoke(collectiveId);
    }
    private void ClearRunState(int collectiveId)
    {
        _phase.TryRemove(collectiveId, out _);
        foreach (var k in _droneState.Keys.Where(k => k.Item1 == collectiveId).ToList())
            _droneState.TryRemove(k, out _);
        OnHiveRunStateChanged?.Invoke(collectiveId);
    }

    /// <summary>Framing prepended ahead of the Overmind's own persona charter for every LLM call it
    /// makes, so it stays aware of its role and drone roster even when the sub-agent bound to it has
    /// been given its own custom instructions.</summary>
    private static string OvermindPrefix(AgentCollective collective) =>
        $"You are the Overmind of the Hive \"{collective.Name}\". You have drones available at your " +
        "service — dispatch tasks to them and gather the most adapted, best possible results for the " +
        "user's request.";

    public bool HasPendingGate(int cogId)       => _humanGates.ContainsKey(cogId);
    public bool HasPendingMemberGate(int cogId, int memberId) => _memberGates.ContainsKey((cogId, memberId));

    public void ApproveHumanGate(int cogId, string? notes = null)
    {
        if (_humanGates.TryRemove(cogId, out var tcs))
        {
            tcs.TrySetResult(notes);
            OnHiveGateResolved?.Invoke(cogId);
        }
    }

    public void ApproveHiveMemberGate(int cogId, int memberId, string? notes = null)
    {
        if (_memberGates.TryRemove((cogId, memberId), out var tcs))
        {
            tcs.TrySetResult(notes);
            OnHiveMemberGateResolved?.Invoke(cogId, memberId);
        }
    }

    public CollectiveOrchestrator(
        IServiceScopeFactory            scopeFactory,
        AgentBackgroundExecutor         executor,
        BridgeHiveClient                bridgeHive,
        ModelBridgeRegistry             bridgeRegistry,
        ILogger<CollectiveOrchestrator> logger)
    {
        _scopeFactory   = scopeFactory;
        _executor       = executor;
        _bridgeHive     = bridgeHive;
        _bridgeRegistry = bridgeRegistry;
        _logger         = logger;
    }

    // ── IHostedService ────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // On startup, reset any collectives stuck in Running/Planning to Paused
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                await using var ctx = await db.CreateDbContextAsync();
                // Reset stuck collectives (Running/Planning) to Paused
                await ctx.AgentCollectives
                    .Where(c => c.Status == CollectiveStatus.Running || c.Status == CollectiveStatus.Planning)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CollectiveStatus.Paused));

                // Reset ALL stuck tasks (Running/Dispatched) to Pending — regardless of collective status,
                // since tasks can remain Running after an unclean shutdown even when the collective is Paused
                await ctx.CollectiveTasks
                    .Where(t => t.Status == CollectiveTaskStatus.Running || t.Status == CollectiveTaskStatus.Dispatched)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.Status, CollectiveTaskStatus.Pending)
                        .SetProperty(t => t.StartedAt, (DateTime?)null));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reset orphaned collective statuses on startup");
            }
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (_, cts) in _loops)
        {
            try { cts.Cancel(); } catch { }
        }
        await Task.Delay(500, CancellationToken.None);
    }

    // ── Public API ────────────────────────────────────────────────────────

    public bool IsRunning(int collectiveId) =>
        _loops.ContainsKey(collectiveId) && !_loops[collectiveId].IsCancellationRequested;

    public Task StartCollectiveAsync(int collectiveId)
    {
        if (_loops.ContainsKey(collectiveId))
        {
            _loops[collectiveId].Cancel();
            _loops.TryRemove(collectiveId, out _);
        }
        var cts = new CancellationTokenSource();
        _loops[collectiveId] = cts;
        _ = Task.Run(() => RunCollectiveAsync(collectiveId, cts.Token));
        return Task.CompletedTask;
    }

    public async Task PauseAsync(int collectiveId)
    {
        if (_loops.TryGetValue(collectiveId, out var cts))
        {
            cts.Cancel();
            _loops.TryRemove(collectiveId, out _);
        }
        await SetStatusAsync(collectiveId, CollectiveStatus.Paused);
        FireChanged(collectiveId);
    }

    public async Task ResetAsync(int collectiveId)
    {
        if (_loops.TryGetValue(collectiveId, out var cts))
        {
            cts.Cancel();
            _loops.TryRemove(collectiveId, out _);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();

        await db.CollectiveTasks .Where(t => t.CollectiveId == collectiveId).ExecuteDeleteAsync();
        await db.CollectiveEvents.Where(e => e.CollectiveId == collectiveId).ExecuteDeleteAsync();
        await db.AgentCollectives
            .Where(c => c.Id == collectiveId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status,        CollectiveStatus.Draft)
                .SetProperty(c => c.CurrentRound,  0)
                .SetProperty(c => c.ResultSummary, (string?)null)
                .SetProperty(c => c.LastFeedback,  (string?)null)
                .SetProperty(c => c.CompletedAt,   (DateTime?)null)
                .SetProperty(c => c.UpdatedAt,     DateTime.UtcNow));
        FireChanged(collectiveId);
    }
}
