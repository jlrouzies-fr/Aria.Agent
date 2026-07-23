using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.Cron;

public class CronSlotService(
    IDbContextFactory<AppDbContext> dbFactory,
    ModelBridgeRegistry registry)
{
    public const int MaxJobsPerUser  = 2;
    public const int MaxUsersPerSlot = 2;
    public const int MaxSlotsPerDay  = 2;

    public async Task<List<AgentCronJob>> GetUserJobsAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AgentCronJobs
            .Where(j => j.UserId == userId
                     && (j.Status == CronJobStatus.Pending || j.Status == CronJobStatus.Running))
            .OrderBy(j => j.ScheduledDate).ThenBy(j => j.ScheduledHour)
            .ToListAsync();
    }

    public async Task<List<AgentCronJob>> GetRecentCompletedAsync(string userId, int limit = 10)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AgentCronJobs
            .Where(j => j.UserId == userId
                     && (j.Status == CronJobStatus.Completed || j.Status == CronJobStatus.Failed))
            .OrderByDescending(j => j.CompletedAt)
            .Take(limit)
            .ToListAsync();
    }

    // Returns count of active bookings per (date, hour) for the requested week.
    public async Task<Dictionary<(DateOnly Date, int Hour), int>> GetWeekBookingsAsync(DateOnly weekStart)
    {
        var weekEnd = weekStart.AddDays(7);
        await using var db = await dbFactory.CreateDbContextAsync();

        var jobs = await db.AgentCronJobs
            .Where(j => j.ScheduledDate >= weekStart && j.ScheduledDate < weekEnd
                     && (j.Status == CronJobStatus.Pending || j.Status == CronJobStatus.Running))
            .Select(j => new { j.ScheduledDate, j.ScheduledHour, j.UserId })
            .ToListAsync();

        return jobs
            .GroupBy(j => (j.ScheduledDate, j.ScheduledHour))
            .ToDictionary(g => g.Key, g => g.Count());
    }

    // Returns (date, hour) slots that belong to the given user within the requested week.
    public async Task<HashSet<(DateOnly Date, int Hour)>> GetUserWeekSlotsAsync(string userId, DateOnly weekStart)
    {
        var weekEnd = weekStart.AddDays(7);
        await using var db = await dbFactory.CreateDbContextAsync();

        var slots = await db.AgentCronJobs
            .Where(j => j.UserId == userId
                     && j.ScheduledDate >= weekStart && j.ScheduledDate < weekEnd
                     && (j.Status == CronJobStatus.Pending || j.Status == CronJobStatus.Running))
            .Select(j => new { j.ScheduledDate, j.ScheduledHour })
            .ToListAsync();

        return slots.Select(s => (s.ScheduledDate, s.ScheduledHour)).ToHashSet();
    }

    public async Task<(bool Success, string? Error, AgentCronJob? Job)> BookAsync(
        string userId, DateOnly date, int hour,
        string taskPrompt, int? subAgentId,
        string? sourceName, string? modelId,
        int? targetCogitationId = null,
        string? bridgeNodeId = null,
        bool allowProjectTools = false)
    {
        // A named node must be connected at booking time.
        if (!string.IsNullOrEmpty(bridgeNodeId) && !registry.GetNodes(userId).Any(n => n.NodeId == bridgeNodeId))
            return (false, "Selected bridge device is offline or not enrolled.", null);

        await using var db = await dbFactory.CreateDbContextAsync();

        var userActive = await db.AgentCronJobs
            .Where(j => j.UserId == userId
                     && (j.Status == CronJobStatus.Pending || j.Status == CronJobStatus.Running))
            .ToListAsync();

        if (userActive.Count >= MaxJobsPerUser)
            return (false, $"Maximum {MaxJobsPerUser} scheduled vigils per soul reached.", null);

        if (userActive.Count(j => j.ScheduledDate == date) >= MaxSlotsPerDay)
            return (false, $"Maximum {MaxSlotsPerDay} vigils per day per soul reached.", null);

        var slotCount = await db.AgentCronJobs
            .CountAsync(j => j.ScheduledDate == date && j.ScheduledHour == hour
                          && (j.Status == CronJobStatus.Pending || j.Status == CronJobStatus.Running));

        if (slotCount >= MaxUsersPerSlot)
            return (false, "This hour slot is fully occupied. Choose another.", null);

        var slotUtc      = date.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc);
        var nowUtc       = DateTime.UtcNow;
        var nowHourStart = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc);
        if (slotUtc < nowHourStart)
            return (false, "Cannot schedule a vigil in the past.", null);

        var job = new AgentCronJob
        {
            UserId               = userId,
            SubAgentId           = subAgentId,
            TaskPrompt           = taskPrompt.Trim(),
            ScheduledDate        = date,
            ScheduledHour        = hour,
            SourceName           = sourceName,
            ModelId              = modelId,
            TargetCogitationId   = targetCogitationId,
            BridgeNodeId         = bridgeNodeId,
            AllowProjectTools    = allowProjectTools,
            Status               = CronJobStatus.Pending,
            CreatedAt            = DateTime.UtcNow,
        };

        db.AgentCronJobs.Add(job);
        await db.SaveChangesAsync();
        return (true, null, job);
    }

    public async Task<bool> CancelAsync(int jobId, string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var job = await db.AgentCronJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId);
        if (job is null || job.Status != CronJobStatus.Pending) return false;
        job.Status = CronJobStatus.Cancelled;
        await db.SaveChangesAsync();
        return true;
    }

    // Called by the scheduler to find jobs due now or overdue (pending but past their slot).
    public async Task<List<AgentCronJob>> GetDueJobsAsync()
    {
        var now  = DateTime.UtcNow;
        var date = DateOnly.FromDateTime(now);
        var hour = now.Hour;

        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AgentCronJobs
            .Include(j => j.User)
            .Where(j => j.Status == CronJobStatus.Pending
                     && (j.ScheduledDate < date
                         || (j.ScheduledDate == date && j.ScheduledHour <= hour)))
            .OrderBy(j => j.ScheduledDate).ThenBy(j => j.ScheduledHour)
            .Take(20)
            .ToListAsync();
    }

    public async Task MarkRunningAsync(int jobId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.AgentCronJobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status,    CronJobStatus.Running)
                .SetProperty(j => j.StartedAt, DateTime.UtcNow));
    }

    public async Task MarkCompletedAsync(int jobId, int cogitationId, string? summary)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.AgentCronJobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status,        CronJobStatus.Completed)
                .SetProperty(j => j.CogitationId,  cogitationId)
                .SetProperty(j => j.ResultSummary, summary)
                .SetProperty(j => j.CompletedAt,   DateTime.UtcNow)
                .SetProperty(j => j.IsSeenByUser,  false));
    }

    public async Task<int> GetUnseenCompletedCountAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AgentCronJobs
            .CountAsync(j => j.UserId == userId
                          && j.Status == CronJobStatus.Completed
                          && !j.IsSeenByUser);
    }

    public async Task MarkAllSeenAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.AgentCronJobs
            .Where(j => j.UserId == userId && !j.IsSeenByUser)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.IsSeenByUser, true));
    }

    public async Task MarkFailedAsync(int jobId, string error)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.AgentCronJobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status,       CronJobStatus.Failed)
                .SetProperty(j => j.ErrorMessage, error)
                .SetProperty(j => j.CompletedAt,  DateTime.UtcNow));
    }
}
