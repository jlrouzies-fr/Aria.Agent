using Aria.Bridge.Data;
using Aria.Bridge.Services.Trust;
using Aria.Shared;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Infrastructure;

/// <summary>
/// Local read/write for Layer B context grants (defense-in-depth plan §4–§5). A grant lets the bridge
/// run classified-sensitive server-relayed operations for a context without re-prompting, until it
/// expires or is revoked. Grants are node-SIGNED: verification is against the signing key, so a
/// tampered local row or a grant forged/altered by the relaying server cannot pass — and a signed
/// grant can be safely replicated to a soul's other nodes.
/// </summary>
public static class ContextGrantStore
{
    /// <summary>Persisted setting key for the Layer B enforcement toggle.</summary>
    public const string EnforcementSettingKey = "layerb.enforce";

    // Cached toggle, backed by the local Settings table. Defaults ON: a fresh node enforces Layer B
    // unless the human turns it off in the bridge UI. Loaded once at startup (LoadEnforcementAsync)
    // and updated in place whenever the toggle is flipped (SetEnforcementAsync), so the hot gate path
    // reads a plain bool with no DB round-trip.
    private static volatile bool _enforcementEnabled = true;

    /// <summary>Whether Layer B enforcement is switched on for this node. Owned locally by the human
    /// (bridge UI toggle), never by the hosted server. Default ON.</summary>
    public static bool EnforcementEnabled => _enforcementEnabled;

    /// <summary>Loads the persisted enforcement toggle into the in-memory cache. No row ⇒ default ON.</summary>
    public static async Task LoadEnforcementAsync(BridgeDbContext db, CancellationToken ct = default)
    {
        var row = await db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == EnforcementSettingKey, ct);
        if (row != null) _enforcementEnabled = row.Value is "1" or "true" or "TRUE";
    }

    /// <summary>Persists and applies the enforcement toggle (bridge UI action).</summary>
    public static async Task SetEnforcementAsync(BridgeDbContext db, bool enabled, CancellationToken ct = default)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == EnforcementSettingKey, ct);
        if (row == null)
            db.Settings.Add(new BridgeSetting { Key = EnforcementSettingKey, Value = enabled ? "1" : "0" });
        else
            row.Value = enabled ? "1" : "0";
        await db.SaveChangesAsync(ct);
        _enforcementEnabled = enabled;
    }

    private const string GrantType = "context";

    /// <summary>
    /// The grant context id for a soul, optionally narrowed to one browser session. A session-scoped
    /// grant covers only that session; the bare soul id is the legacy soul-wide grant. Kept as a
    /// single canonical string so it flows through the same sign/verify/replicate path unchanged.
    /// </summary>
    public static string ContextId(string serverSoulId, string? sessionId) =>
        string.IsNullOrEmpty(sessionId) ? serverSoulId : $"{serverSoulId}|{sessionId}";

    /// <summary>
    /// A request from <paramref name="sessionId"/> is authorised if either a grant for that specific
    /// session is live, or a soul-wide grant is live. So a session approval covers just that session,
    /// while a soul-wide approval (or a legacy grant) still covers everything — no double-prompting.
    /// </summary>
    public static async Task<bool> HasValidGrantForRequestAsync(
        BridgeDbContext db, BridgeSoul? soul, string? sessionId, CancellationToken ct = default)
    {
        if (soul?.ServerSoulId is not { Length: > 0 } serverSoulId) return false;
        if (!string.IsNullOrEmpty(sessionId)
            && await HasValidGrantAsync(db, soul, ContextId(serverSoulId, sessionId), ct)) return true;
        return await HasValidGrantAsync(db, soul, serverSoulId, ct);
    }

    // The public keys a grant's signature is accepted under: the soul master key (grants signed by the
    // primary, which replicate across nodes), this node's own key (grants it approved itself), and any
    // sibling node keys whose enrollment certificates this bridge has locally verified. The raw server
    // roster is never trusted directly.
    private static async Task<IReadOnlyList<string>> AcceptableKeysAsync(
        BridgeDbContext db, BridgeSoul soul, CancellationToken ct)
    {
        var keys = new List<string>();
        if (!string.IsNullOrEmpty(soul.PublicKeyBase64))     keys.Add(soul.PublicKeyBase64);
        if (!string.IsNullOrEmpty(soul.NodePublicKeyBase64)) keys.Add(soul.NodePublicKeyBase64);
        if (!string.IsNullOrEmpty(soul.ServerSoulId))
            keys.AddRange(await SiblingRoster.GetTrustedKeysAsync(db, soul.ServerSoulId, ct));
        return keys;
    }

    private static async Task<bool> VerifyGrantAsync(
        BridgeDbContext db, BridgeSoul soul, ContextGrant g, CancellationToken ct)
    {
        var payload = GrantCanonical.Payload(g.GrantType, g.ContextId, g.ContextId, g.ExpiryUnix);
        foreach (var key in await AcceptableKeysAsync(db, soul, ct))
            if (GrantCrypto.Verify(key, payload, g.SignatureBase64)) return true;
        return false;
    }

    public static async Task<bool> HasValidGrantAsync(
        BridgeDbContext db, BridgeSoul? soul, string contextId, CancellationToken ct = default)
    {
        if (soul == null || string.IsNullOrEmpty(contextId)) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = await db.ContextGrants.AsNoTracking()
            .Where(g => g.ContextId == contextId && !g.Revoked && g.ExpiryUnix > now)
            .ToListAsync(ct);
        // Verify the node signature — a live, unexpired row is not enough on its own.
        foreach (var g in rows)
            if (await VerifyGrantAsync(db, soul, g, ct)) return true;
        return false;
    }

    /// <summary>
    /// The Unix-seconds expiry of the grant that currently authorises requests from
    /// <paramref name="sessionId"/> — the later of a live session-scoped grant and a live soul-wide
    /// grant — or null when neither is live. Mirrors <see cref="HasValidGrantForRequestAsync"/>; used to
    /// drive the header seal countdown (how long until the agent must ask for a fresh seal).
    /// </summary>
    public static async Task<long?> EffectiveGrantExpiryAsync(
        BridgeDbContext db, BridgeSoul? soul, string? sessionId, CancellationToken ct = default)
    {
        if (soul?.ServerSoulId is not { Length: > 0 } serverSoulId) return null;
        long? best = null;
        if (!string.IsNullOrEmpty(sessionId))
            best = await LiveExpiryAsync(db, soul, ContextId(serverSoulId, sessionId), ct);
        var soulWide = await LiveExpiryAsync(db, soul, serverSoulId, ct);
        if (soulWide is { } sw && (best is not { } b || sw > b)) best = soulWide;
        return best;
    }

    // Max verified, non-revoked, unexpired ExpiryUnix for one context id, or null if none.
    private static async Task<long?> LiveExpiryAsync(
        BridgeDbContext db, BridgeSoul soul, string contextId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = await db.ContextGrants.AsNoTracking()
            .Where(g => g.ContextId == contextId && !g.Revoked && g.ExpiryUnix > now)
            .OrderByDescending(g => g.ExpiryUnix)
            .ToListAsync(ct);
        foreach (var g in rows)
            if (await VerifyGrantAsync(db, soul, g, ct)) return g.ExpiryUnix;
        return null;
    }

    /// <summary>
    /// Signs and stores a grant for the context (refreshing any live one). Signs with the soul master
    /// key when this node holds it (so the grant replicates), else with this node's own key. Returns
    /// false if the node has no usable private key.
    /// </summary>
    public static Task<bool> GrantAsync(
        BridgeDbContext db, BridgeSoul soul, string contextId, TimeSpan ttl, CancellationToken ct = default) =>
        GrantAtAsync(db, soul, contextId, DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds(), ct);

    /// <summary>
    /// Signs and stores a grant that lapses at an ABSOLUTE instant (Unix seconds) rather than now+ttl.
    /// Used for pre-authorised vigils: the human approves at booking time, but the grant must be scoped
    /// to the vigil's scheduled slot (which is in the future), not to the moment of approval. An expiry
    /// already in the past is refused — it would sign a dead grant.
    /// </summary>
    public static async Task<bool> GrantAtAsync(
        BridgeDbContext db, BridgeSoul soul, string contextId, long expiryUnix, CancellationToken ct = default)
    {
        var signingKey = soul.PrivateKeyBase64 ?? soul.NodePrivateKeyBase64;
        if (string.IsNullOrEmpty(signingKey)) return false;
        if (expiryUnix <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;

        var expiry    = expiryUnix;
        var payload   = GrantCanonical.Payload(GrantType, contextId, contextId, expiry);
        var signature = GrantCrypto.Sign(signingKey, payload);

        var existing = await db.ContextGrants.FirstOrDefaultAsync(g => g.ContextId == contextId && !g.Revoked, ct);
        if (existing != null)
        {
            existing.ExpiryUnix      = expiry;
            existing.SignatureBase64 = signature;
            existing.GrantType       = GrantType;
        }
        else
        {
            db.ContextGrants.Add(new ContextGrant
            {
                ContextId = contextId, GrantType = GrantType, ExpiryUnix = expiry, SignatureBase64 = signature,
            });
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Imports a grant received from a sibling node (relayed by the server). Stored only if its
    /// signature verifies under one of the soul's acceptable keys — so the server cannot inject or alter
    /// a grant in transit.
    /// </summary>
    public static async Task<bool> ImportGrantAsync(
        BridgeDbContext db, BridgeSoul soul, ContextGrant incoming, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(incoming.ContextId) || !await VerifyGrantAsync(db, soul, incoming, ct)) return false;

        var existing = await db.ContextGrants.FirstOrDefaultAsync(g => g.ContextId == incoming.ContextId && !g.Revoked, ct);
        // Keep the longer-lived grant; never shorten one we already trust.
        if (existing != null)
        {
            if (incoming.ExpiryUnix > existing.ExpiryUnix)
            {
                existing.ExpiryUnix      = incoming.ExpiryUnix;
                existing.SignatureBase64 = incoming.SignatureBase64;
                existing.GrantType       = incoming.GrantType;
                await db.SaveChangesAsync(ct);
            }
        }
        else
        {
            db.ContextGrants.Add(new ContextGrant
            {
                ContextId = incoming.ContextId, GrantType = incoming.GrantType,
                ExpiryUnix = incoming.ExpiryUnix, SignatureBase64 = incoming.SignatureBase64,
            });
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    public static async Task<List<ContextGrant>> ExportLiveGrantsAsync(BridgeDbContext db, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return await db.ContextGrants.AsNoTracking()
            .Where(g => !g.Revoked && g.ExpiryUnix > now && g.SignatureBase64 != null)
            .ToListAsync(ct);
    }

    public static async Task RevokeAsync(BridgeDbContext db, string contextId, CancellationToken ct = default)
    {
        await db.ContextGrants
            .Where(g => g.ContextId == contextId && !g.Revoked)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Revoked, true), ct);
    }
}
