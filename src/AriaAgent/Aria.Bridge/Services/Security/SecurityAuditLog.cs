using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Services.Security;

/// <summary>
/// Node-side audit trail for sensitive capability invocations (F-8). Records who asked the node to do
/// what, whether it was allowed, and when. Survives restarts because it is stored in the bridge SQLite
/// database, with a retention cap so the table does not grow without bound.
/// </summary>
public sealed class SecurityAuditLog(IServiceProvider serviceProvider, ILogger<SecurityAuditLog> logger)
{
    private const int MaxRetentionDays = 30;
    private const int MaxRetentionCount = 1000;

    /// <summary>
    /// Records an audit event asynchronously. Exceptions are logged, never thrown.
    /// </summary>
    public async Task RecordAsync(string category, string action, bool allowed, string? detail = null, string? capability = null)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            await using var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
            db.AuditEvents.Add(new BridgeAuditEvent
            {
                Timestamp = DateTime.UtcNow,
                Category = category,
                Action = action,
                Capability = capability,
                Detail = detail,
                Allowed = allowed,
            });

            await PruneAsync(db);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write security audit event {Category}/{Action}", category, action);
        }
    }

    /// <summary>
    /// Fire-and-forget variant for endpoints that do not need to await the write.
    /// </summary>
    public void Record(string category, string action, bool allowed, string? detail = null, string? capability = null)
        => _ = RecordAsync(category, action, allowed, detail, capability);

    /// <summary>
    /// Returns recent audit events, newest first, capped at <paramref name="limit"/>.
    /// </summary>
    public async Task<BridgeAuditEvent[]> ListRecentAsync(int limit = 100)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        await using var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        return await db.AuditEvents
            .AsNoTracking()
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .ToArrayAsync();
    }

    private static async Task PruneAsync(BridgeDbContext db)
    {
        var cutoff = DateTime.UtcNow.AddDays(-MaxRetentionDays);
        await db.AuditEvents
            .Where(e => e.Timestamp < cutoff)
            .ExecuteDeleteAsync();

        var count = await db.AuditEvents.CountAsync();
        if (count > MaxRetentionCount)
        {
            var toRemove = await db.AuditEvents
                .OrderBy(e => e.Timestamp)
                .Take(count - MaxRetentionCount)
                .Select(e => e.Id)
                .ToListAsync();
            await db.AuditEvents
                .Where(e => toRemove.Contains(e.Id))
                .ExecuteDeleteAsync();
        }
    }
}
