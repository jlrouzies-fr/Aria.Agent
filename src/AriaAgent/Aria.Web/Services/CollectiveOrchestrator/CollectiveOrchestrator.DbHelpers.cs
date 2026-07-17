using System.Text;
using System.Text.Json;
using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.CollectiveOrchestrator;

public partial class CollectiveOrchestrator
{
    private record CollectiveMeta(int CollectiveId, string UserId, string? OriginNodeId);

    private bool IsBridgeOwned(CollectiveMeta? meta) => !string.IsNullOrEmpty(meta?.OriginNodeId);

    private async Task<CollectiveMeta?> LoadCollectiveMetaAsync(int id)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        var c = await db.AgentCollectives
            .AsNoTracking()
            .Select(c => new { c.Id, c.UserId, c.OriginNodeId })
            .FirstOrDefaultAsync(c => c.Id == id);
        return c == null ? null : new CollectiveMeta(c.Id, c.UserId, c.OriginNodeId);
    }

    private async Task<CollectiveMeta?> LoadTaskCollectiveMetaAsync(int taskId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        var t = await db.CollectiveTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.CollectiveId })
            .FirstOrDefaultAsync();
        return t == null ? null : await LoadCollectiveMetaAsync(t.CollectiveId);
    }

    // ── DB helpers ────────────────────────────────────────────────────────

    private async Task<AgentCollective?> LoadCollectiveAsync(int id)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        var c = await db.AgentCollectives.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (c == null) return null;

        if (!string.IsNullOrEmpty(c.OriginNodeId))
        {
            var content = await _bridgeHive.LoadContentAsync(c.UserId, c.Id, c.OriginNodeId);
            if (content != null)
            {
                c.Objective     = content.Collective.Objective;
                c.ResultSummary = content.Collective.ResultSummary;
                c.LastFeedback  = content.Collective.LastFeedback;
                c.SynapseMemory = content.Collective.SynapseMemory;
            }
        }
        return c;
    }

    private async Task<List<CollectiveMember>> GetMembersAsync(int collectiveId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        return await db.CollectiveMembers
            .Include(m => m.SubAgent).ThenInclude(a => a.ToolStates)
            .Include(m => m.SubAgent).ThenInclude(a => a.SubAgentSkills)
            .Include(m => m.EdgeNodes)
            .AsSplitQuery()
            .AsNoTracking()
            .Where(m => m.CollectiveId == collectiveId)
            .ToListAsync();
    }

    private async Task<CollectiveMember?> GetMemberAsync(int memberId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        return await db.CollectiveMembers
            .Include(m => m.SubAgent).ThenInclude(a => a.ToolStates)
            .Include(m => m.SubAgent).ThenInclude(a => a.SubAgentSkills)
            .Include(m => m.EdgeNodes)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == memberId);
    }

    private async Task<List<CollectiveTask>> GetRunnableTasksAsync(int collectiveId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        // Pending tasks with no dependencies or whose deps are satisfied will be returned
        // We can't do complex dep check in SQL easily — return Pending and filter in memory
        var pending = await db.CollectiveTasks
            .AsNoTracking()
            .Where(t => t.CollectiveId == collectiveId && t.Status == CollectiveTaskStatus.Pending)
            .ToListAsync();

        var runnable = new List<CollectiveTask>();
        foreach (var t in pending)
        {
            if (await AreDepsSatisfiedAsync(t.Id, collectiveId, db))
                runnable.Add(t);
        }
        await HydrateTaskContentAsync(collectiveId, runnable);
        return runnable;
    }

    private async Task<List<CollectiveTask>> GetBlockedTasksAsync(int collectiveId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        var tasks = await db.CollectiveTasks
            .AsNoTracking()
            .Where(t => t.CollectiveId == collectiveId && t.Status == CollectiveTaskStatus.Blocked)
            .ToListAsync();
        await HydrateTaskContentAsync(collectiveId, tasks);
        return tasks;
    }

    private async Task<bool> AreDepsSatisfiedAsync(int taskId, int collectiveId, AppDbContext? existingDb = null)
    {
        AppDbContext? db = existingDb;
        bool ownDb = false;
        IAsyncDisposable? scope = null;

        if (db == null)
        {
            scope = _scopeFactory.CreateAsyncScope();
            var s = (IServiceScope)scope;
            var dbf = s.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            db = await dbf.CreateDbContextAsync();
            ownDb = true;
        }

        try
        {
            var task = await db.CollectiveTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId);
            if (task?.DependsOnJson == null) return true;

            int[] depIds;
            try { depIds = JsonSerializer.Deserialize<int[]>(task.DependsOnJson) ?? []; }
            catch { return true; }

            if (depIds.Length == 0) return true;

            // A skipped dependency is terminal (intentionally not applicable) — it shouldn't block dependents.
            var completedCount = await db.CollectiveTasks
                .CountAsync(t => depIds.Contains(t.Id) &&
                    (t.Status == CollectiveTaskStatus.Completed || t.Status == CollectiveTaskStatus.Skipped));
            return completedCount == depIds.Length;
        }
        finally
        {
            if (ownDb && db != null) await db.DisposeAsync();
            if (scope != null) await scope.DisposeAsync();
        }
    }

    private async Task<List<CollectiveTask>> GetCompletedTasksAsync(int collectiveId, int maxRound)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        var tasks = await db.CollectiveTasks
            .AsNoTracking()
            .Where(t => t.CollectiveId == collectiveId
                     && t.Status == CollectiveTaskStatus.Completed
                     && t.Round <= maxRound)
            .OrderBy(t => t.Round).ThenBy(t => t.Id)
            .ToListAsync();
        await HydrateTaskContentAsync(collectiveId, tasks);
        return tasks;
    }

    private async Task<List<CollectiveTask>> GetRoundTasksAsync(int collectiveId, int round)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        var tasks = await db.CollectiveTasks
            .AsNoTracking()
            .Where(t => t.CollectiveId == collectiveId && t.Round == round)
            .ToListAsync();
        await HydrateTaskContentAsync(collectiveId, tasks);
        return tasks;
    }

    private async Task HydrateTaskContentAsync(int collectiveId, List<CollectiveTask> tasks)
    {
        if (tasks.Count == 0) return;
        var meta = await LoadCollectiveMetaAsync(collectiveId);
        if (!IsBridgeOwned(meta)) return;

        var content = await _bridgeHive.GetTasksAsync(meta!.UserId, collectiveId, meta.OriginNodeId);
        var byId = content.ToDictionary(t => t.Id);
        foreach (var task in tasks)
        {
            if (byId.TryGetValue(BridgeHiveClient.TaskId(task.Id), out var bt))
            {
                task.Title                = bt.Title;
                task.Instruction          = bt.Instruction;
                task.EffectiveInstruction = bt.EffectiveInstruction;
                task.Result               = bt.Result;
            }
        }
    }

    private async Task<string> BuildDroneInstructionAsync(CollectiveTask task, int collectiveId)
    {
        var sb = new StringBuilder(task.Instruction);

        if (task.DependsOnJson != null)
        {
            try
            {
                var depIds = JsonSerializer.Deserialize<int[]>(task.DependsOnJson) ?? [];
                if (depIds.Length > 0)
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                    await using var db = await dbf.CreateDbContextAsync();
                    var depTasks = await db.CollectiveTasks
                        .AsNoTracking()
                        .Where(t => depIds.Contains(t.Id) && t.Status == CollectiveTaskStatus.Completed)
                        .ToListAsync();

                    if (depTasks.Count > 0)
                    {
                        sb.AppendLine("\n\n--- UPSTREAM RESULTS (context for your task) ---");
                        foreach (var dep in depTasks)
                        {
                            var excerpt = dep.Result?.Length > 500 ? dep.Result[..500] + "…" : dep.Result;
                            sb.AppendLine($"\n[{dep.Title}]:\n{excerpt}");
                        }
                    }
                }
            }
            catch { /* ignore dep parse errors */ }
        }

        return sb.ToString();
    }

    private async Task MarkTaskRunningAsync(int taskId, string? effectiveInstruction = null)
    {
        var meta = await LoadTaskCollectiveMetaAsync(taskId);
        if (IsBridgeOwned(meta))
        {
            await _bridgeHive.UpsertTaskContentAsync(
                meta!.UserId, meta.CollectiveId, taskId, meta.OriginNodeId!,
                effectiveInstruction: effectiveInstruction);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        if (IsBridgeOwned(meta))
        {
            await db.CollectiveTasks
                .Where(t => t.Id == taskId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status,    CollectiveTaskStatus.Running)
                    .SetProperty(t => t.StartedAt, DateTime.UtcNow));
        }
        else
        {
            await db.CollectiveTasks
                .Where(t => t.Id == taskId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status,               CollectiveTaskStatus.Running)
                    .SetProperty(t => t.StartedAt,            DateTime.UtcNow)
                    .SetProperty(t => t.EffectiveInstruction, effectiveInstruction));
        }
    }

    private async Task MarkTaskCompletedAsync(int taskId, string result)
    {
        var meta = await LoadTaskCollectiveMetaAsync(taskId);
        if (IsBridgeOwned(meta))
        {
            await _bridgeHive.UpsertTaskContentAsync(
                meta!.UserId, meta.CollectiveId, taskId, meta.OriginNodeId!,
                result: result);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        if (IsBridgeOwned(meta))
        {
            await db.CollectiveTasks
                .Where(t => t.Id == taskId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status,      CollectiveTaskStatus.Completed)
                    .SetProperty(t => t.CompletedAt, DateTime.UtcNow));
        }
        else
        {
            await db.CollectiveTasks
                .Where(t => t.Id == taskId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status,      CollectiveTaskStatus.Completed)
                    .SetProperty(t => t.Result,      result)
                    .SetProperty(t => t.CompletedAt, DateTime.UtcNow));
        }
    }

    private async Task MarkTaskFailedAsync(int taskId, string error)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        await db.CollectiveTasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status,       CollectiveTaskStatus.Failed)
                .SetProperty(t => t.ErrorMessage, error)
                .SetProperty(t => t.CompletedAt,  DateTime.UtcNow));
    }

    private async Task UpdateTaskStatusAsync(int taskId, CollectiveTaskStatus status)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        await db.CollectiveTasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, status));
    }

    private async Task SetStatusAsync(int collectiveId, CollectiveStatus status)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        await db.AgentCollectives
            .Where(c => c.Id == collectiveId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status,    status)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow));
    }

    private async Task IncrementRoundAsync(int collectiveId, int round)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        await db.AgentCollectives
            .Where(c => c.Id == collectiveId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.CurrentRound, round)
                .SetProperty(c => c.UpdatedAt,    DateTime.UtcNow));
    }

    private async Task SetCompletedAsync(int collectiveId, string summary)
    {
        await RevokeOneShotSealAsync(collectiveId);   // "this run only" seal — retire it now the run is done
        var meta = await LoadCollectiveMetaAsync(collectiveId);
        if (IsBridgeOwned(meta))
        {
            await _bridgeHive.EnsureCollectiveAsync(meta!.UserId, collectiveId, meta.OriginNodeId!);
            await _bridgeHive.UpdateCollectiveContentAsync(
                meta.UserId, collectiveId, meta.OriginNodeId!, resultSummary: summary);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        if (IsBridgeOwned(meta))
        {
            await db.AgentCollectives
                .Where(c => c.Id == collectiveId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status,      CollectiveStatus.Completed)
                    .SetProperty(c => c.CompletedAt, DateTime.UtcNow)
                    .SetProperty(c => c.UpdatedAt,   DateTime.UtcNow));
        }
        else
        {
            await db.AgentCollectives
                .Where(c => c.Id == collectiveId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status,        CollectiveStatus.Completed)
                    .SetProperty(c => c.ResultSummary, summary)
                    .SetProperty(c => c.CompletedAt,   DateTime.UtcNow)
                    .SetProperty(c => c.UpdatedAt,     DateTime.UtcNow));
        }
    }

    private async Task SetFailedAsync(int collectiveId, string reason)
    {
        await RevokeOneShotSealAsync(collectiveId);   // "this run only" seal — retire it now the run has ended
        var meta = await LoadCollectiveMetaAsync(collectiveId);
        if (IsBridgeOwned(meta))
        {
            await _bridgeHive.EnsureCollectiveAsync(meta!.UserId, collectiveId, meta.OriginNodeId!);
            await _bridgeHive.UpdateCollectiveContentAsync(
                meta.UserId, collectiveId, meta.OriginNodeId!, resultSummary: reason);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        if (IsBridgeOwned(meta))
        {
            await db.AgentCollectives
                .Where(c => c.Id == collectiveId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status,      CollectiveStatus.Failed)
                    .SetProperty(c => c.UpdatedAt,   DateTime.UtcNow));
        }
        else
        {
            await db.AgentCollectives
                .Where(c => c.Id == collectiveId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status,        CollectiveStatus.Failed)
                    .SetProperty(c => c.ResultSummary, reason)
                    .SetProperty(c => c.UpdatedAt,     DateTime.UtcNow));
        }
    }

    private async Task SaveFeedbackAsync(int collectiveId, string? feedback)
    {
        var meta = await LoadCollectiveMetaAsync(collectiveId);
        if (IsBridgeOwned(meta))
        {
            await _bridgeHive.EnsureCollectiveAsync(meta!.UserId, collectiveId, meta.OriginNodeId!);
            await _bridgeHive.UpdateCollectiveContentAsync(
                meta.UserId, collectiveId, meta.OriginNodeId!, lastFeedback: feedback);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbf.CreateDbContextAsync();
        await db.AgentCollectives
            .Where(c => c.Id == collectiveId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastFeedback, feedback));
    }

    private async Task AppendEventAsync(
        int collectiveId, CollectiveEventType type, string message,
        int? actorMemberId, int? taskId)
    {
        try
        {
            var meta = await LoadCollectiveMetaAsync(collectiveId);
            var timestamp = DateTime.UtcNow;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbf.CreateDbContextAsync();
            var ev = new CollectiveEvent
            {
                CollectiveId  = collectiveId,
                Type          = type,
                Message       = IsBridgeOwned(meta) ? "" : message,
                ActorMemberId = actorMemberId,
                TaskId        = taskId,
                Timestamp     = timestamp,
            };
            db.CollectiveEvents.Add(ev);
            await db.SaveChangesAsync();

            if (IsBridgeOwned(meta))
            {
                await _bridgeHive.EnsureCollectiveAsync(meta!.UserId, collectiveId, meta.OriginNodeId!);
                await _bridgeHive.AppendEventAsync(
                    meta.UserId, collectiveId, meta.OriginNodeId!,
                    timestamp, type.ToString(), actorMemberId, taskId, message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HIVE] Failed to append event for collective {Id}", collectiveId);
        }
    }

    private void FireChanged(int collectiveId) =>
        OnCollectiveChanged?.Invoke(collectiveId);

    // ── JSON parsers ──────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static PlanResponse? TryParsePlan(string text)
    {
        var json = ExtractFirstJsonObject(text);
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<PlanResponse>(json, _jsonOpts); }
        catch { return null; }
    }

    private static ReviewResponse? TryParseReview(string text)
    {
        var json = ExtractFirstJsonObject(text);
        if (json != null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<ReviewResponse>(json, _jsonOpts);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Decision))
                    return parsed with { Decision = parsed.Decision.Trim().ToUpperInvariant() };
            }
            catch { /* fall through to lenient keyword scan */ }
        }

        // Lenient fallback: the model answered in prose (no/invalid JSON). Detect the decision keyword
        // and keep the raw text as the summary/feedback so the round still resolves.
        if (string.IsNullOrWhiteSpace(text)) return null;
        var upper = text.ToUpperInvariant();
        string? decision =
            upper.Contains("COMPLETE") ? "COMPLETE" :
            upper.Contains("ABORT")    ? "ABORT"    :
            upper.Contains("CONTINUE") ? "CONTINUE" : null;
        if (decision == null) return null;

        var trimmed = text.Trim();
        return new ReviewResponse(
            decision,
            Summary:  decision == "COMPLETE" ? trimmed : null,
            Feedback: decision == "CONTINUE" ? trimmed : null);
    }

    /// <summary>Extracts the first balanced {...} block from text.</summary>
    private static string? ExtractFirstJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) return null;

        int depth  = 0;
        bool inStr = false;
        bool esc   = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (esc) { esc = false; continue; }
            if (c == '\\' && inStr) { esc = true; continue; }
            if (c == '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return null;
    }
}
