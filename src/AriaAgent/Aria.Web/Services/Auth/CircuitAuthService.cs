using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aria.Shared;
using Aria.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.Auth;

/// <summary>
/// Per-circuit (per browser tab) soul verification (§12). A circuit is unlocked only after ITS OWN
/// browser proves it controls a bridge enrolled for the selected soul: the circuit issues a nonce,
/// the browser relays a sign request to its localhost bridge (<c>/node/attest</c>), and we verify the
/// signature against the soul's live key set { soul pubkey ∪ non-revoked nodes }. Scoped to the
/// circuit; clears its verification entries on disposal.
/// </summary>
public class CircuitAuthService(
    IDbContextFactory<AppDbContext> dbFactory,
    ModelBridgeRegistry registry,
    UserSessionState session,
    IHttpContextAccessor httpContextAccessor,
    IWebHostEnvironment environment,
    IDataProtectionProvider dataProtection,
    ILogger<CircuitAuthService> log) : IDisposable
{
    // verifyKey → nonce awaiting the browser's signature (this circuit only).
    private readonly ConcurrentDictionary<string, byte[]> _nonces = new();
    private readonly IDataProtector _trustedProtector = dataProtection.CreateProtector("Aria.TrustedBrowser");

    private string VerifyKey(string userId) => $"circuit-{session.SessionToken}-{userId}";

    public bool IsVerified(string userId) => registry.SoulVerified(VerifyKey(userId));

    /// <summary>Issues a fresh challenge and returns the exact payload string the local bridge must
    /// sign (<c>attest|userId|token|nonceB64</c>).</summary>
    public string Begin(string userId)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        _nonces[VerifyKey(userId)] = nonce;
        return $"attest|{userId}|{session.SessionToken}|{Convert.ToBase64String(nonce)}";
    }

    /// <summary>Verifies the bridge's signature over the last-issued payload and, if the signing key
    /// belongs to this soul, marks this circuit verified for that soul. Single-use nonce.</summary>
    public async Task<bool> CompleteAsync(string userId, string nodePublicKeyB64, string signatureB64)
    {
        var key = VerifyKey(userId);
        if (!_nonces.TryRemove(key, out var nonce))
        { log.LogWarning("[CircuitAuth] Attest failed for {UserId}: no pending nonce (stale or concurrent attempt)", userId); return false; }
        if (string.IsNullOrWhiteSpace(nodePublicKeyB64) || string.IsNullOrWhiteSpace(signatureB64)) return false;

        var payload = Encoding.UTF8.GetBytes(
            $"attest|{userId}|{session.SessionToken}|{Convert.ToBase64String(nonce)}");
        if (!NodeCrypto.Verify(nodePublicKeyB64, payload, signatureB64))
        { log.LogWarning("[CircuitAuth] Attest failed for {UserId}: bad signature from node {Thumb}", userId, NodeCrypto.Thumbprint(nodePublicKeyB64)); return false; }

        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (string.IsNullOrEmpty(user?.PublicKey))
        { log.LogWarning("[CircuitAuth] Attest failed for {UserId}: no PublicKey on record", userId); return false; }

        // The signing key must be this soul's master key (primary) or a non-revoked enrolled node.
        var isSoulKey = nodePublicKeyB64 == user.PublicKey;
        var thumb     = NodeCrypto.Thumbprint(nodePublicKeyB64);
        var enrolled  = await db.SoulNodeKeys.AnyAsync(k => k.UserId == userId && k.NodeId == thumb && !k.Revoked);
        if (!isSoulKey && !enrolled)
        { log.LogWarning("[CircuitAuth] Attest failed for {UserId}: node {Thumb} is not the soul key and not enrolled", userId, thumb); return false; }

        MarkVerified(userId);
        return true;
    }

    // ── Bridge-discovered soul selection (one bridge = one soul) ─────────────────────────────

    private string DiscoverKey() => $"discover-{session.SessionToken}";

    /// <summary>Issues a fresh challenge for discovering which soul the local bridge owns. The payload
    /// is signed by the bridge via <c>/node/attest</c>; the returned public key identifies the soul.</summary>
    public string DiscoverBegin()
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        _nonces[DiscoverKey()] = nonce;
        return $"discover|{session.SessionToken}|{Convert.ToBase64String(nonce)}";
    }

    /// <summary>Verifies a discovery signature and returns the matching user. The signing key must be
    /// a soul's master key or a non-revoked enrolled node. Marks the circuit verified for that user.</summary>
    public async Task<Data.Users.User?> DiscoverCompleteAsync(string nodePublicKeyB64, string signatureB64)
    {
        var key = DiscoverKey();
        if (!_nonces.TryRemove(key, out var nonce))
        { log.LogWarning("[CircuitAuth] Discovery failed on circuit {Token}: no pending nonce (stale or concurrent attempt)", session.SessionToken); return null; }
        if (string.IsNullOrWhiteSpace(nodePublicKeyB64) || string.IsNullOrWhiteSpace(signatureB64))
        { log.LogWarning("[CircuitAuth] Discovery failed on circuit {Token}: empty key or signature from bridge", session.SessionToken); return null; }

        var payload = Encoding.UTF8.GetBytes(
            $"discover|{session.SessionToken}|{Convert.ToBase64String(nonce)}");
        if (!NodeCrypto.Verify(nodePublicKeyB64, payload, signatureB64))
        { log.LogWarning("[CircuitAuth] Discovery failed on circuit {Token}: bad signature from node {Thumb}", session.SessionToken, NodeCrypto.Thumbprint(nodePublicKeyB64)); return null; }

        await using var db = await dbFactory.CreateDbContextAsync();
        var thumb = NodeCrypto.Thumbprint(nodePublicKeyB64);

        // Master key matches the soul directly.
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.PublicKey == nodePublicKeyB64);

        // Otherwise the signing key may be an enrolled (non-revoked) node for a soul.
        if (user == null)
        {
            var node = await db.SoulNodeKeys.AsNoTracking()
                .FirstOrDefaultAsync(k => k.NodeId == thumb && !k.Revoked);
            if (node != null)
                user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == node.UserId);
        }

        if (user != null)
        {
            MarkVerified(user.Id);
            log.LogInformation("[CircuitAuth] Discovered and verified soul {UserId} from local bridge", user.Id);
        }
        else
            log.LogWarning("[CircuitAuth] Discovery: node {Thumb} signed correctly but matches no soul key or enrolled node", thumb);

        return user;
    }

    // Per-circuit rate limit for the manual-code fallback (CompleteWithCodeAsync).
    private int _codeAttempts;
    private DateTime _codeWindowStart = DateTime.UtcNow;

    /// <summary>Fallback for insecure-context browsers (http://LAN-IP) that can't do the automatic
    /// loopback attestation. The user reads their bridge's session code from its localhost status page
    /// (a co-location proof) and pastes it. The code is <b>self-identifying</b>: we search EVERY connected
    /// bridge, and the one whose live code matches tells us which soul this browser belongs to — so the
    /// user never has to pre-pick the right soul. On match we unlock that soul for THIS circuit and return
    /// its userId so the UI can switch to it.</summary>
    public async Task<(bool Ok, string? UserId, string? Error)> UnlockByCodeAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return (false, null, "Enter the session code from your bridge.");

        // Rate limit: max 10 tries / minute per circuit (40-bit code → brute force is hopeless anyway).
        if (DateTime.UtcNow - _codeWindowStart > TimeSpan.FromMinutes(1)) { _codeWindowStart = DateTime.UtcNow; _codeAttempts = 0; }
        if (++_codeAttempts > 10) return (false, null, "Too many attempts — wait a minute and try again.");

        var want = Normalize(code);
        if (want.Length < 6) return (false, null, "That code looks too short.");

        var all = registry.AllNodes().ToList();
        if (all.Count == 0)
            return (false, null, "No bridge is connected to the server yet. Start your local bridge and " +
                                 "confirm it shows as linked, then retry.");

        foreach (var (uid, node) in all)
        {
            var resp = await registry.SendLocalRestAsync(uid, "GET", "/node/session-code", null, node.NodeId);
            if (resp is not { StatusCode: 200, Body: { } body }) continue;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var nodeCode = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
                var norm     = nodeCode == null ? "" : Normalize(nodeCode);
                log.LogInformation("[Attest/Code] candidate userId={U} node={Label}/{Node} match={M}",
                    uid, node.Label, node.NodeId, norm == want);
                if (nodeCode != null && norm == want)
                {
                    // NOTE: we don't flip verification here — the caller switches the UI to this soul
                    // first, then calls MarkVerified so the status event fires with the soul already
                    // current (otherwise gates keyed to the new soul miss the change).
                    log.LogInformation("[Attest/Code] circuit {Token} matched soul userId={U} via {Label}",
                        session.SessionToken, uid, node.Label);
                    return (true, uid, null);
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "[Attest/Code] node {Node} bad response", node.NodeId); }
        }

        return (false, null, "That code didn't match any connected bridge. Re-copy it from " +
                             "http://localhost:5741 on this machine, and make sure your local bridge is linked.");
    }

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>Marks this circuit verified for a soul. Call AFTER the UI has switched to that soul so
    /// the status event fires with the soul current (see UnlockByCodeAsync).</summary>
    public void MarkVerified(string userId)
    {
        registry.SetSoulVerified(VerifyKey(userId), true);
        log.LogInformation("[CircuitAuth] Circuit {Token} verified for soul {UserId}", session.SessionToken, userId);
        // Best-effort: over a WebSocket-backed circuit the response has already started, so the
        // cookie append can throw — verification must survive that.
        try { AppendTrustedCookie(userId); }
        catch (Exception ex) { log.LogWarning(ex, "[CircuitAuth] Could not append aria-trusted cookie (circuit context)"); }
    }

    private void AppendTrustedCookie(string userId)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext == null) return;

        var expiry = DateTime.UtcNow.AddDays(90);
        var payload = Encoding.UTF8.GetBytes($"{userId}|{expiry:O}");
        var protectedPayload = Convert.ToBase64String(_trustedProtector.Protect(payload));

        httpContext.Response.Cookies.Append("aria-trusted", protectedPayload, new CookieOptions
        {
            HttpOnly = true,
            Secure = !environment.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expiry,
        });
    }

    /// <summary>Drops this circuit's verification for a soul (e.g. when switching souls).</summary>
    public void Clear(string userId) => registry.SetSoulVerified(VerifyKey(userId), false);

    public void Dispose()
    {
        // Circuit ending (tab closed): revoke every per-circuit verification it established.
        foreach (var key in _nonces.Keys) _nonces.TryRemove(key, out _);
        registry.ClearCircuit(session.SessionToken);
    }
}
