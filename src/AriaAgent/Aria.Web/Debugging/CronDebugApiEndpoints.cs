#if DEBUG
using Aria.Web.Data;
using Aria.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Debug;

// Debug endpoints for inspecting and manually triggering cron vigil jobs.
//
// curl http://localhost:5129/api/debug/cron/status
// curl -X POST http://localhost:5129/api/debug/cron/trigger
// curl http://localhost:5129/api/debug/cron/jobs
// curl -X POST http://localhost:5129/api/debug/cron/jobs/7/run   ← force-run specific job id

public static class CronDebugApiEndpoints
{
    public static void MapCronDebugEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/debug/cron");

        // Scheduler health + last tick info
        grp.MapGet("/status", (CronSchedulerHostedService scheduler) => Results.Ok(new
        {
            utcNow       = DateTime.UtcNow.ToString("o"),
            lastTickUtc  = scheduler.LastTickUtc == default ? (string?)null : scheduler.LastTickUtc.ToString("o"),
            lastJobCount = scheduler.LastJobCount,
            lastError    = scheduler.LastError,
        }));

        // All jobs — any status
        grp.MapGet("/jobs", async (IDbContextFactory<AppDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var jobs = await db.AgentCronJobs
                .Include(j => j.User)
                .OrderByDescending(j => j.CreatedAt)
                .Take(50)
                .Select(j => new
                {
                    j.Id,
                    user          = j.User.Name,
                    j.UserId,
                    scheduledUtc  = j.ScheduledDate.ToString("yyyy-MM-dd") + $" {j.ScheduledHour:D2}:00",
                    status        = j.Status.ToString(),
                    j.TaskPrompt,
                    j.StartedAt,
                    j.CompletedAt,
                    j.ErrorMessage,
                    j.CogitationId,
                    overdue       = j.Status == CronJobStatus.Pending
                                    && (j.ScheduledDate < DateOnly.FromDateTime(DateTime.UtcNow)
                                        || (j.ScheduledDate == DateOnly.FromDateTime(DateTime.UtcNow)
                                            && j.ScheduledHour <= DateTime.UtcNow.Hour)),
                })
                .ToListAsync();
            return Results.Ok(jobs);
        });

        // Manually fire the due/overdue check right now
        grp.MapPost("/trigger", async (CronSchedulerHostedService scheduler) =>
        {
            await scheduler.ManualTriggerAsync();
            return Results.Ok(new { triggered = true, utcNow = DateTime.UtcNow.ToString("o") });
        });

        // Force-dispatch a specific job by id (bypasses due-time check)
        grp.MapPost("/jobs/{id:int}/run", async (
            int id,
            IDbContextFactory<AppDbContext> dbFactory,
            AgentBackgroundExecutor executor) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var job = await db.AgentCronJobs.Include(j => j.User).FirstOrDefaultAsync(j => j.Id == id);
            if (job is null)           return Results.NotFound(new { error = $"Job {id} not found" });
            if (job.Status != CronJobStatus.Pending)
                return Results.BadRequest(new { error = $"Job {id} is {job.Status}, only Pending jobs can be force-run" });

            _ = executor.ExecuteJobAsync(job, CancellationToken.None);
            return Results.Ok(new { dispatched = true, jobId = id, utcNow = DateTime.UtcNow.ToString("o") });
        });
    }
}
#endif
