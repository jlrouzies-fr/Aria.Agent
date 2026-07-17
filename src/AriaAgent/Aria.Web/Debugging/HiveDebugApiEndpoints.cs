#if DEBUG
using Aria.Web.Data;
using Aria.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Debug;

// Debug endpoints for inspecting and manually stepping Hive collectives.
//
// curl http://localhost:5129/api/debug/hive/collectives
// curl -X POST http://localhost:5129/api/debug/hive/collectives/1/start
// curl -X POST http://localhost:5129/api/debug/hive/collectives/1/pause
// curl -X POST http://localhost:5129/api/debug/hive/collectives/1/reset

public static class HiveDebugApiEndpoints
{
    public static void MapHiveDebugEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/debug/hive");

        // List all collectives
        grp.MapGet("/collectives", async (IDbContextFactory<AppDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var collectives = await db.AgentCollectives
                .OrderByDescending(c => c.UpdatedAt)
                .Take(50)
                .Select(c => new
                {
                    c.Id, c.Name, c.UserId,
                    status       = c.Status.ToString(),
                    c.CurrentRound, c.MaxRounds,
                    c.Objective,
                    c.UpdatedAt,
                })
                .ToListAsync();
            return Results.Ok(collectives);
        });

        // Tasks for a collective
        grp.MapGet("/collectives/{id:int}/tasks", async (
            int id, IDbContextFactory<AppDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var tasks = await db.CollectiveTasks
                .Where(t => t.CollectiveId == id)
                .OrderBy(t => t.Round).ThenBy(t => t.Id)
                .Select(t => new
                {
                    t.Id, t.Round, t.Title,
                    status = t.Status.ToString(),
                    t.AssignedMemberId,
                    t.Result,
                    t.ErrorMessage,
                })
                .ToListAsync();
            var tasksOut = tasks.Select(t => new
            {
                t.Id, t.Round, t.Title, t.status, t.AssignedMemberId,
                resultPreview = t.Result == null ? null : (t.Result.Length > 200 ? t.Result[..200] + "…" : t.Result),
                t.ErrorMessage,
            }).ToList();
            return Results.Ok(tasksOut);
        });

        // Events for a collective
        grp.MapGet("/collectives/{id:int}/events", async (
            int id, IDbContextFactory<AppDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var events = await db.CollectiveEvents
                .Where(e => e.CollectiveId == id)
                .OrderByDescending(e => e.Id)
                .Take(100)
                .Select(e => new
                {
                    e.Id, e.Timestamp,
                    type = e.Type.ToString(),
                    e.ActorMemberId, e.TaskId,
                    e.Message,
                })
                .ToListAsync();
            return Results.Ok(events);
        });

        // Start a collective
        grp.MapPost("/collectives/{id:int}/start", async (
            int id, CollectiveOrchestrator orchestrator) =>
        {
            await orchestrator.StartCollectiveAsync(id);
            return Results.Ok(new { started = true, collectiveId = id });
        });

        // Pause a collective
        grp.MapPost("/collectives/{id:int}/pause", async (
            int id, CollectiveOrchestrator orchestrator) =>
        {
            await orchestrator.PauseAsync(id);
            return Results.Ok(new { paused = true, collectiveId = id });
        });

        // Reset a collective
        grp.MapPost("/collectives/{id:int}/reset", async (
            int id, CollectiveOrchestrator orchestrator) =>
        {
            await orchestrator.ResetAsync(id);
            return Results.Ok(new { reset = true, collectiveId = id });
        });

        // Running status
        grp.MapGet("/collectives/{id:int}/running", (
            int id, CollectiveOrchestrator orchestrator) =>
            Results.Ok(new { collectiveId = id, isRunning = orchestrator.IsRunning(id) }));

        // Members of a collective
        grp.MapGet("/collectives/{id:int}/members", async (
            int id, IDbContextFactory<AppDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var members = await db.CollectiveMembers
                .Where(m => m.CollectiveId == id)
                .Select(m => new
                {
                    m.Id,
                    m.CollectiveId,
                    m.SubAgentId,
                    m.RoleLabel,
                    agentName = m.SubAgent.GeneratedName,
                    agentModelSource = m.SubAgent.ModelSourceName,
                    agentModelId = m.SubAgent.ModelId,
                })
                .ToListAsync();
            return Results.Ok(members);
        });

        // Seed a ready-to-run test scenario for a given user
        // curl -X POST "http://localhost:5129/api/debug/hive/seed?userId=5"
        grp.MapPost("/seed", async (
            string userId, IDbContextFactory<AppDbContext> dbFactory,
            CollectiveService collectiveService) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            // Get sub-agents for this user
            var agents = await db.SubAgents.Where(a => a.UserId == userId).ToListAsync();
            if (agents.Count < 1)
                return Results.BadRequest(new { error = "Need at least 1 sub-agent for user " + userId });

            var c = await collectiveService.CreateAsync(userId, "OPERATION CODEX");

            // Update config
            await db.AgentCollectives
                .Where(x => x.Id == c.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Objective,
                        "Research the top 3 advantages of multi-agent AI architectures. " +
                        "Cover: (1) parallelism and speed gains, (2) role specialisation, (3) error-checking through peer review. " +
                        "Produce a concise synthesis at the end.")
                    .SetProperty(x => x.OvermindSourceName, agents[0].ModelSourceName ?? "Local LLM - Mac")
                    .SetProperty(x => x.OvermindModelId,    agents[0].ModelId)
                    .SetProperty(x => x.MaxRounds, 2));

            // Add members
            foreach (var a in agents)
                await collectiveService.AddMemberAsync(c.Id, a.Id, null);

            var members = await db.CollectiveMembers
                .Where(m => m.CollectiveId == c.Id)
                .Select(m => new { m.Id, agentId = m.SubAgentId, agentName = m.SubAgent.GeneratedName })
                .ToListAsync();

            return Results.Ok(new { collectiveId = c.Id, members });
        });
    }
}
#endif
