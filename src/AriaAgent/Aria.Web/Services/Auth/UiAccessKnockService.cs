using System.Text;
using Aria.Web.Data;
using Aria.Web.Data.Context;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.Auth;

/// <summary>
/// Stores and validates transient UI-access knocks from authenticated bridges.
/// The stored IP addresses are encrypted at rest using ASP.NET Data Protection.
/// </summary>
public class UiAccessKnockService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IDataProtector _protector;

    public UiAccessKnockService(IDbContextFactory<AppDbContext> dbFactory, IDataProtectionProvider dataProtection)
    {
        _dbFactory = dbFactory;
        _protector = dataProtection.CreateProtector("Aria.UiAccessKnock");
    }

    /// <summary>
    /// Records (or refreshes) a knock for the given user from the given IP. The IP is encrypted
    /// before storage and the record expires after <paramref name="ttl"/>.
    /// </summary>
    public async Task RecordAsync(string userId, string ipAddress, TimeSpan ttl, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // A soul can have SEVERAL bridges knocking from different IPs (one per machine, and IPv4 vs
        // IPv6 differ even on one LAN). Keeping only the latest knock per user made each machine's
        // knock evict the other's, so the access gate flip-flopped between them. Replace only THIS
        // IP's row and cap the per-user row count instead.
        var mine = await db.UiAccessKnocks.Where(k => k.UserId == userId).ToListAsync(ct);
        foreach (var existing in mine)
        {
            try
            {
                var plain = Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(existing.IpAddressProtected)));
                if (plain == ipAddress) db.UiAccessKnocks.Remove(existing);
            }
            catch { db.UiAccessKnocks.Remove(existing); } // unreadable row — drop it
        }
        var live = mine.Count(e => db.Entry(e).State != EntityState.Deleted);
        if (live >= 8)
            foreach (var oldest in mine.Where(e => db.Entry(e).State != EntityState.Deleted)
                         .OrderBy(e => e.ExpiresAt).Take(live - 7))
                db.UiAccessKnocks.Remove(oldest);

        var protectedIp = Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(ipAddress)));

        db.UiAccessKnocks.Add(new UiAccessKnock
        {
            UserId = userId,
            IpAddressProtected = protectedIp,
            ExpiresAt = DateTime.UtcNow + ttl,
        });

        await PruneAsync(db, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns true if any non-expired knock matches the supplied IP address.
    /// </summary>
    public async Task<bool> IsAllowedAsync(string ipAddress, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await PruneAsync(db, ct);

        var active = await db.UiAccessKnocks
            .Where(k => k.ExpiresAt > DateTime.UtcNow)
            .ToListAsync(ct);

        foreach (var knock in active)
        {
            try
            {
                var plain = Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(knock.IpAddressProtected)));
                if (plain == ipAddress)
                    return true;
            }
            catch
            {
                // Corrupted or unprotectable entry — ignore.
            }
        }

        return false;
    }

    private static async Task PruneAsync(AppDbContext db, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow;
        await db.UiAccessKnocks
            .Where(k => k.ExpiresAt <= cutoff)
            .ExecuteDeleteAsync(ct);
    }
}
