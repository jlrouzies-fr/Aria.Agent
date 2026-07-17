namespace Aria.Bridge.Services.Metrics;

/// <summary>
/// Keeps a fresh metrics snapshot available at all times by collecting on a background loop.
/// This avoids making the HTTP request path wait for subprocess probes (top/vm_stat/ioreg)
/// when the bridge is busy with long-running inference or tunnel traffic.
/// </summary>
public sealed class BridgeMetricsHostedService : BackgroundService
{
    private readonly BridgeMetricsCollector _collector;
    private readonly TimeSpan _tickInterval = TimeSpan.FromSeconds(2);

    public BridgeMetricsHostedService(BridgeMetricsCollector collector)
    {
        _collector = collector;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Collect once immediately so the first /metrics request has data.
        await CollectOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_tickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await CollectOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CollectOnceAsync(CancellationToken ct)
    {
        try
        {
            var metrics = await _collector.GetMetricsAsync(ct).ConfigureAwait(false);
            _collector.SetLatest(metrics);
        }
        catch
        {
            // Best-effort telemetry: failures are reported inside BridgeMetrics.Error when available.
        }
    }
}
