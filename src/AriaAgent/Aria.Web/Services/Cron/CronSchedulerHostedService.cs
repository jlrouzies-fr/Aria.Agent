namespace Aria.Web.Services.Cron;

/// <summary>
/// Wakes up every minute and dispatches any pending jobs whose scheduled slot is now or in the past.
/// Overdue jobs (missed due to server downtime) are recovered on the next tick.
/// </summary>
public class CronSchedulerHostedService(
    CronSlotService          cronService,
    AgentBackgroundExecutor  executor,
    ILogger<CronSchedulerHostedService> logger) : IHostedService, IDisposable
{
    private Timer? _timer;

    // Exposed for the debug endpoint.
    public DateTime LastTickUtc  { get; private set; }
    public int      LastJobCount { get; private set; }
    public string?  LastError    { get; private set; }

    public Task StartAsync(CancellationToken ct)
    {
        _timer = new Timer(OnTick, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        logger.LogInformation("Cron scheduler started — ticking every minute, dispatches due/overdue jobs");
        return Task.CompletedTask;
    }

    private void OnTick(object? _)
    {
        _ = Task.Run(async () =>
        {
            LastTickUtc = DateTime.UtcNow;
            try
            {
                var jobs = await cronService.GetDueJobsAsync();
                LastJobCount = jobs.Count;
                LastError    = null;

                if (jobs.Count > 0)
                {
                    logger.LogInformation("Cron tick {Time} UTC — {Count} job(s) due/overdue",
                        LastTickUtc.ToString("HH:mm:ss"), jobs.Count);
                    foreach (var job in jobs)
                    {
                        logger.LogInformation(
                            "  → Dispatching job {JobId} for user {UserId}, scheduled {Date} {Hour}:00 UTC",
                            job.Id, job.UserId, job.ScheduledDate, job.ScheduledHour);
                        _ = executor.ExecuteJobAsync(job, CancellationToken.None);
                    }
                }
                else
                {
                    logger.LogDebug("Cron tick {Time} UTC — no jobs due", LastTickUtc.ToString("HH:mm:ss"));
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                logger.LogError(ex, "Cron scheduler tick failed at {Time}", LastTickUtc.ToString("HH:mm:ss"));
            }
        });
    }

    public async Task ManualTriggerAsync()
    {
        var jobs = await cronService.GetDueJobsAsync();
        logger.LogInformation("Manual cron trigger — {Count} job(s) due/overdue", jobs.Count);
        foreach (var job in jobs)
        {
            logger.LogInformation("  → Dispatching job {JobId} for user {UserId}", job.Id, job.UserId);
            _ = executor.ExecuteJobAsync(job, CancellationToken.None);
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();
}
