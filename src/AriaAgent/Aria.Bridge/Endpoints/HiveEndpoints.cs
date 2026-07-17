using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

public static class HiveEndpoints
{
    public static void MapHiveEndpoints(this WebApplication app)
    {
        // POST /hive/collectives/init — create or return an existing bridge-side collective.
        app.MapPost("/hive/collectives/init", async (
            InitHiveCollectiveRequest req,
            BridgeDbContext db,
            ILogger<HiveLogCategory> logger) =>
        {
            if (string.IsNullOrWhiteSpace(req.ServerUserId)) return Results.BadRequest("ServerUserId required");
            if (string.IsNullOrEmpty(req.Id)) return Results.BadRequest("Id required");

            var soul = await db.Souls.FirstOrDefaultAsync(s => s.ServerSoulId == req.ServerUserId && s.Name != "");
            if (soul == null)
                return Results.Problem(
                    statusCode: 412,
                    title: "Soul not configured",
                    detail: $"No named soul linked to serverUserId={req.ServerUserId}.");

            var existing = await db.HiveCollectives
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == req.Id);
            if (existing != null)
            {
                logger.LogInformation("[Hive] Init returned existing collective={CollectiveId} soul={SoulId}", existing.Id, existing.SoulId);
                return Results.Ok(ToCollectiveDto(existing));
            }

            var c = new BridgeHiveCollective
            {
                Id            = req.Id,
                SoulId        = soul.Id,
                Objective     = req.Objective ?? "",
                ResultSummary = req.ResultSummary,
                LastFeedback  = req.LastFeedback,
                SynapseMemory = req.SynapseMemory,
            };
            db.HiveCollectives.Add(c);
            await db.SaveChangesAsync();

            logger.LogInformation("[Hive] Created collective={CollectiveId} soul={SoulId}", c.Id, c.SoulId);
            return Results.Created($"/hive/collectives/{c.Id}", ToCollectiveDto(c));
        });

        // GET /hive/collectives/{id}
        app.MapGet("/hive/collectives/{id}", async (
            string id,
            BridgeDbContext db,
            ILogger<HiveLogCategory> logger) =>
        {
            var c = await db.HiveCollectives.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (c == null) return Results.NotFound();
            logger.LogInformation("[Hive] Fetched collective={CollectiveId}", id);
            return Results.Ok(ToCollectiveDto(c));
        });

        // PUT /hive/collectives/{id}/content — update Objective / ResultSummary / LastFeedback / SynapseMemory.
        app.MapPut("/hive/collectives/{id}/content", async (
            string id, UpdateHiveCollectiveRequest req,
            BridgeDbContext db,
            ILogger<HiveLogCategory> logger) =>
        {
            var c = await db.HiveCollectives.FirstOrDefaultAsync(c => c.Id == id);
            if (c == null) return Results.NotFound();

            if (req.Objective is not null) c.Objective = req.Objective;
            if (req.ResultSummary is not null) c.ResultSummary = req.ResultSummary;
            if (req.LastFeedback is not null) c.LastFeedback = req.LastFeedback;
            if (req.SynapseMemory is not null) c.SynapseMemory = req.SynapseMemory;
            c.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            logger.LogInformation("[Hive] Updated collective content={CollectiveId}", id);
            return Results.Ok(ToCollectiveDto(c));
        });

        // DELETE /hive/collectives/{id}
        app.MapDelete("/hive/collectives/{id}", async (
            string id,
            BridgeDbContext db,
            ILogger<HiveLogCategory> logger) =>
        {
            await db.HiveTasks.Where(t => t.CollectiveId == id).ExecuteDeleteAsync();
            await db.HiveEvents.Where(e => e.CollectiveId == id).ExecuteDeleteAsync();
            await db.HiveCollectives.Where(c => c.Id == id).ExecuteDeleteAsync();
            logger.LogInformation("[Hive] Deleted collective={CollectiveId}", id);
            return Results.NoContent();
        });

        // GET /hive/collectives/{id}/tasks
        app.MapGet("/hive/collectives/{id}/tasks", async (
            string id,
            BridgeDbContext db,
            ILogger<HiveLogCategory> logger) =>
        {
            if (!await db.HiveCollectives.AnyAsync(c => c.Id == id))
                return Results.NotFound();

            var tasks = await db.HiveTasks
                .AsNoTracking()
                .Where(t => t.CollectiveId == id)
                .OrderBy(t => t.UpdatedAt)
                .Select(t => new HiveTaskDto(t.Id, t.CollectiveId, t.Title, t.Instruction, t.EffectiveInstruction, t.Result, t.UpdatedAt))
                .ToListAsync();

            logger.LogInformation("[Hive] Fetched {Count} tasks for collective={CollectiveId}", tasks.Count, id);
            return Results.Ok(tasks);
        });

        // POST /hive/collectives/{id}/tasks/{taskId}/content — create or update a task.
        app.MapPost("/hive/collectives/{id}/tasks/{taskId}/content", async (
            string id, string taskId, UpsertHiveTaskRequest req,
            BridgeDbContext db,
            ILogger<HiveLogCategory> logger) =>
        {
            if (!await db.HiveCollectives.AnyAsync(c => c.Id == id))
                return Results.NotFound("Collective not found");

            var task = await db.HiveTasks.FirstOrDefaultAsync(t => t.Id == taskId && t.CollectiveId == id);
            if (task == null)
            {
                task = new BridgeHiveTask { Id = taskId, CollectiveId = id };
                db.HiveTasks.Add(task);
            }

            if (req.Title is not null) task.Title = req.Title;
            if (req.Instruction is not null) task.Instruction = req.Instruction;
            if (req.EffectiveInstruction is not null) task.EffectiveInstruction = req.EffectiveInstruction;
            if (req.Result is not null) task.Result = req.Result;
            task.UpdatedAt = DateTime.UtcNow;

            var collective = await db.HiveCollectives.FirstAsync(c => c.Id == id);
            collective.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            logger.LogInformation("[Hive] Upserted task={TaskId} collective={CollectiveId}", taskId, id);
            return Results.Ok(new HiveTaskDto(task.Id, task.CollectiveId, task.Title, task.Instruction, task.EffectiveInstruction, task.Result, task.UpdatedAt));
        });

        // GET /hive/collectives/{id}/events
        app.MapGet("/hive/collectives/{id}/events", async (
            string id,
            BridgeDbContext db,
            ILogger<HiveLogCategory> logger) =>
        {
            if (!await db.HiveCollectives.AnyAsync(c => c.Id == id))
                return Results.NotFound();

            var events = await db.HiveEvents
                .AsNoTracking()
                .Where(e => e.CollectiveId == id)
                .OrderBy(e => e.Timestamp)
                .Select(e => new HiveEventDto(e.Id, e.CollectiveId, e.Timestamp, e.Type, e.ActorMemberId, e.TaskId, e.Message))
                .ToListAsync();

            logger.LogInformation("[Hive] Fetched {Count} events for collective={CollectiveId}", events.Count, id);
            return Results.Ok(events);
        });

        // POST /hive/collectives/{id}/events — append an event.
        app.MapPost("/hive/collectives/{id}/events", async (
            string id, AppendHiveEventRequest req,
            BridgeDbContext db,
            ILogger<HiveLogCategory> logger) =>
        {
            var collective = await db.HiveCollectives.FirstOrDefaultAsync(c => c.Id == id);
            if (collective == null) return Results.NotFound();

            var ev = new BridgeHiveEvent
            {
                Id            = req.Id ?? Guid.NewGuid().ToString(),
                CollectiveId  = id,
                Timestamp     = req.Timestamp == default ? DateTime.UtcNow : req.Timestamp,
                Type          = req.Type,
                ActorMemberId = req.ActorMemberId,
                TaskId        = req.TaskId,
                Message       = req.Message,
            };
            db.HiveEvents.Add(ev);
            collective.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            logger.LogInformation("[Hive] Appended event={EventId} collective={CollectiveId}", ev.Id, id);
            return Results.Created($"/hive/collectives/{id}/events/{ev.Id}",
                new HiveEventDto(ev.Id, ev.CollectiveId, ev.Timestamp, ev.Type, ev.ActorMemberId, ev.TaskId, ev.Message));
        });

        static HiveCollectiveDto ToCollectiveDto(BridgeHiveCollective c) =>
            new(c.Id, c.SoulId, c.Objective, c.ResultSummary, c.LastFeedback, c.SynapseMemory, c.UpdatedAt);
    }
}

internal sealed class HiveLogCategory;

public record InitHiveCollectiveRequest(
    string Id,
    string ServerUserId,
    string? Objective = null,
    string? ResultSummary = null,
    string? LastFeedback = null,
    string? SynapseMemory = null);

public record UpdateHiveCollectiveRequest(
    string? Objective = null,
    string? ResultSummary = null,
    string? LastFeedback = null,
    string? SynapseMemory = null);

public record UpsertHiveTaskRequest(
    string? Title = null,
    string? Instruction = null,
    string? EffectiveInstruction = null,
    string? Result = null);

public record AppendHiveEventRequest(
    string? Id,
    DateTime Timestamp,
    string Type,
    int? ActorMemberId,
    int? TaskId,
    string Message);

public record HiveCollectiveDto(
    string Id,
    string SoulId,
    string Objective,
    string? ResultSummary,
    string? LastFeedback,
    string? SynapseMemory,
    DateTime UpdatedAt);

public record HiveTaskDto(
    string Id,
    string CollectiveId,
    string Title,
    string Instruction,
    string? EffectiveInstruction,
    string? Result,
    DateTime UpdatedAt);

public record HiveEventDto(
    string Id,
    string CollectiveId,
    DateTime Timestamp,
    string Type,
    int? ActorMemberId,
    int? TaskId,
    string Message);
