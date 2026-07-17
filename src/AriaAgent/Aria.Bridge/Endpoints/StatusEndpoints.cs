using Aria.Bridge.Services.Logging;
using Aria.Bridge.Services.Metrics;
using Aria.Bridge.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

public static class StatusEndpoints
{
    public static void MapStatusEndpoints(this WebApplication app)
    {
        // Status page — opens in the browser when the bridge starts.
        app.MapGet("/", (SessionStore store) => Results.Content(BridgeStatusPage.Build(), "text/html"));

        // Machine-readable status — polled by the status page JS every 5 s.
        app.MapGet("/status", (SessionStore store) =>
        {
            var up = DateTimeOffset.UtcNow - BridgeLogger.StartedAt;
            var uptime = up.TotalSeconds < 60
                ? $"{(int)up.TotalSeconds}s"
                : up.TotalMinutes < 60
                    ? $"{(int)up.TotalMinutes}m {up.Seconds}s"
                    : $"{(int)up.TotalHours}h {up.Minutes}m";
            var sessions = store.GetAll()
                .Select(s => new { label = s.Label, idleSecs = (int)(DateTime.UtcNow - s.LastUsed).TotalSeconds })
                .ToArray();
            return Results.Ok(new { status = "ok", version = BridgeLogger.Version, sessions, uptime });
        });

        // Health — accepts GET and POST (the WASM bridge tunnel always uses POST).
        app.MapMethods("/health", ["GET", "POST"],
            () => Results.Ok(new { status = "ok", version = BridgeLogger.Version }));

        // Live bridge process performance metrics (RAM, CPU, GPU best-effort).
        // Served from a background loop snapshot so the request never waits on subprocess probes.
        app.MapGet("/metrics", async (BridgeMetricsCollector collector, CancellationToken ct) =>
        {
            var latest = collector.GetLatest();
            return latest is not null
                ? Results.Ok(latest)
                : Results.Ok(await collector.GetMetricsAsync(ct));
        });

        // Privileged telemetry control (macOS sudo powermetrics).
        app.MapPost("/metrics/sudo", (PowermetricsTelemetrySource source, SudoRequest req) =>
        {
            source.Start(req.Password);
            return Results.Ok(new { source.IsRunning, source.LastError });
        });
        app.MapDelete("/metrics/sudo", (PowermetricsTelemetrySource source) =>
        {
            source.Stop();
            return Results.Ok(new { source.IsRunning });
        });
        app.MapGet("/metrics/sudo/status", (PowermetricsTelemetrySource source) =>
            Results.Ok(new
            {
                source.IsRunning,
                latestGpuUtilizationPercent = source.LatestGpuUtilizationPercent,
                latestGpuPowerMw = source.LatestGpuPowerMw,
                source.LastError
            }));

        // Recent log entries — polled by the status page.
        app.MapGet("/logs", () => Results.Ok(BridgeLogger.LogEntries.ToArray()));

        // F-8: security audit trail — recent sensitive capability invocations visible on the node.
        app.MapGet("/audit/log", async (SecurityAuditLog audit, int? limit) =>
        {
            var events = await audit.ListRecentAsync(Math.Clamp(limit ?? 100, 1, 500));
            return Results.Ok(events.Select(e => new
            {
                e.Id,
                timestamp = e.Timestamp,
                e.Category,
                e.Action,
                e.Capability,
                e.Detail,
                e.Allowed,
            }));
        });
    }
}

public sealed record SudoRequest(string Password);
