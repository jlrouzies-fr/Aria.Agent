using System.Security.Cryptography;
using Aria.Web.Data;
using Aria.Web.Data.Context;
using Aria.Web.Services.Node;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.Auth;

/// <summary>
/// Layer A device trust (defense-in-depth plan §3). Issues an opaque, unguessable device-id cookie,
/// and gates access on whether that device carries a still-valid node-signed <c>trust-device</c>
/// grant. Trust attaches to the node-approved device, not an IP, so it survives roaming / domestic-IP
/// churn. A fresh browser (no cookie, or an unapproved device) does NOT pass — it must be approved at
/// a node first, which only a soul key can sign.
/// </summary>
public sealed class TrustedDeviceService(
    IDbContextFactory<AppDbContext> dbFactory,
    IWebHostEnvironment environment)
{
    public const string CookieName = "aria-device";
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(365);

    /// <summary>Reads the device-id cookie, minting and setting a fresh one if absent. The id alone
    /// grants nothing until a node signs a grant for it.</summary>
    public string GetOrIssueDeviceId(HttpContext ctx)
    {
        var existing = ctx.Request.Cookies[CookieName];
        if (!string.IsNullOrWhiteSpace(existing)) return existing;

        var deviceId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        ctx.Response.Cookies.Append(CookieName, deviceId, new CookieOptions
        {
            HttpOnly = true,
            Secure   = !environment.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            Path     = "/",
            Expires  = DateTimeOffset.UtcNow.Add(CookieLifetime),
        });
        return deviceId;
    }

    public string? ReadDeviceId(HttpContext ctx)
    {
        var v = ctx.Request.Cookies[CookieName];
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    /// <summary>
    /// True iff some non-revoked, unexpired <c>TrustedDevices</c> row for this device carries a
    /// signature that still verifies against one of its soul's ACCEPTABLE keys (the soul master key or
    /// any non-revoked node key — co-equal model). Re-verifying the signature (not just trusting the
    /// row) means a tampered DB row can't grant access; verifying only against *current* keys means
    /// revoking the approver node drops the device automatically.
    /// </summary>
    public async Task<bool> IsDeviceTrustedAsync(string? deviceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // A device may be trusted for more than one soul; any valid grant opens the coarse gate
        // (soul-scoped data still needs key possession).
        var rows = await db.TrustedDevices.AsNoTracking()
            .Where(d => d.DeviceId == deviceId && !d.Revoked && d.ExpiryUnix > now)
            .Select(d => new { d.UserId, d.DeviceId, d.ExpiryUnix, d.SignatureBase64 })
            .ToListAsync(ct);

        foreach (var r in rows)
        {
            var keys  = await AcceptableKeysAsync(db, r.UserId, ct);
            var grant = new SignedGrant(GrantService.DeviceGrant, r.DeviceId, r.UserId, r.ExpiryUnix, r.SignatureBase64);
            if (GrantVerifier.VerifyAny(keys, grant)) return true;
        }
        return false;
    }

    /// <summary>
    /// Persists a node-signed device grant (upsert per soul+device). Verifies the grant against any of
    /// the soul's acceptable keys before storing, so an unsigned/mismatched grant can never be recorded.
    /// </summary>
    public async Task<bool> RecordTrustAsync(
        string userId, SignedGrant grant, string? label, string? ip, string? approvedByNodeId,
        CancellationToken ct = default)
    {
        if (grant.GrantType != GrantService.DeviceGrant || grant.ContextId != userId) return false;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var keys = await AcceptableKeysAsync(db, userId, ct);
        if (!GrantVerifier.VerifyAny(keys, grant)) return false;

        var row = await db.TrustedDevices
            .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == grant.SubjectId, ct);
        if (row == null)
        {
            row = new TrustedDevice { UserId = userId, DeviceId = grant.SubjectId };
            db.TrustedDevices.Add(row);
        }
        row.Label            = label;
        row.LastIp           = ip;
        row.SignatureBase64  = grant.SignatureBase64;
        row.ExpiryUnix       = grant.ExpiryUnix;
        row.ApprovedByNodeId = approvedByNodeId;
        row.Revoked          = false;
        row.RevokedAt        = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task RevokeAsync(string userId, string deviceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.TrustedDevices
            .Where(d => d.UserId == userId && d.DeviceId == deviceId && !d.Revoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Revoked, true)
                .SetProperty(d => d.RevokedAt, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// The set of public keys whose signature is accepted for this soul's grants: the soul master key
    /// plus every non-revoked node key. Only current keys are returned, so a revoked node can neither
    /// approve a new device nor keep alive one it previously approved.
    /// </summary>
    private static async Task<List<string>> AcceptableKeysAsync(AppDbContext db, string userId, CancellationToken ct)
    {
        var keys = new List<string>();

        var soulKey = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.PublicKey).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrEmpty(soulKey)) keys.Add(soulKey);

        var nodeKeys = await db.SoulNodeKeys.AsNoTracking()
            .Where(k => k.UserId == userId && !k.Revoked)
            .Select(k => k.NodePublicKeyBase64)
            .ToListAsync(ct);
        keys.AddRange(nodeKeys.Where(k => !string.IsNullOrEmpty(k)));

        return keys;
    }
}
