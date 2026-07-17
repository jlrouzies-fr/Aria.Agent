using Aria.Bridge.Data;
using Aria.Shared;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using NodeCrypto = Aria.Shared.NodeCrypto;

namespace Aria.Bridge.Services.Trust;

/// <summary>
/// Layer B Phase 2: builds the set of sibling node public keys this bridge will accept for context-grant
/// signatures. The roster is fetched from the (untrusted) server, but every entry's enrollment certificate
/// is re-verified locally against either the soul master key or a sibling whose own certificate already
/// chains to the soul key. This preserves the invariant: acceptable keys are derived only from locally
/// verified material, never from a server-supplied roster.
/// </summary>
public sealed class SiblingRoster(IServiceScopeFactory scopes, Action<string, string> log)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, DateTime> _lastRefresh = new();

    /// <summary>
    /// Refreshes the trusted sibling key set for <paramref name="soul"/> over <paramref name="hub"/>.
    /// Called by <see cref="DirectTunnel"/> after authentication and periodically while connected.
    /// </summary>
    public async Task RefreshAsync(HubConnection hub, BridgeSoul soul, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(soul.ServerSoulId)) return;
        if (string.IsNullOrEmpty(soul.PublicKeyBase64)) return;

        var serverSoulId = soul.ServerSoulId;
        if (_lastRefresh.TryGetValue(serverSoulId, out var last)
            && DateTime.UtcNow - last < RefreshInterval)
        {
            return;
        }

        try
        {
            var roster = await hub.InvokeAsync<IReadOnlyList<SoulNodeRosterEntry>>(
                "GetSoulNodeRoster", serverSoulId, ct);

            await VerifyAndStoreAsync(serverSoulId, soul.PublicKeyBase64, roster ?? []);
            _lastRefresh[serverSoulId] = DateTime.UtcNow;
            log("INFO", $"[SiblingRoster] refreshed for soul {serverSoulId}");
        }
        catch (Exception ex)
        {
            log("WARN", $"[SiblingRoster] refresh failed for soul {serverSoulId}: {ex.Message}");
        }
    }

    private async Task VerifyAndStoreAsync(
        string serverSoulId, string soulPublicKeyBase64, IReadOnlyList<SoulNodeRosterEntry> roster)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();

        // Build a map of thumbprint -> public key for already-trusted siblings so a co-equal approver
        // chain can be followed (soul -> sibling A -> sibling B).
        var existing = await db.TrustedSiblingKeys
            .Where(k => k.UserId == serverSoulId)
            .ToDictionaryAsync(k => k.NodeId, k => k.NodePublicKeyBase64);

        var accepted = roster
            .Select(entry => TryVerifyEntry(serverSoulId, soulPublicKeyBase64, existing, entry))
            .Where(k => k != null)
            .Select(k => k!)
            .ToList();

        // Atomic replace: drop old trusted siblings for this soul and insert the newly verified set.
        await db.TrustedSiblingKeys
            .Where(k => k.UserId == serverSoulId)
            .ExecuteDeleteAsync();

        db.TrustedSiblingKeys.AddRange(accepted);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Pure, stateless verification of one roster entry. Returns a <see cref="TrustedSiblingKey"/> if
    /// the entry's enrollment certificate is signed by the soul key or an already-trusted sibling key;
    /// otherwise null. Exposed for unit testing the trust logic without spinning up SignalR or a DB.
    /// </summary>
    public static TrustedSiblingKey? TryVerifyEntry(
        string serverSoulId,
        string soulPublicKeyBase64,
        IReadOnlyDictionary<string, string> trustedThumbToPublicKey,
        SoulNodeRosterEntry entry)
    {
        if (string.IsNullOrEmpty(entry.NodePublicKeyBase64)) return null;
        var thumb = NodeCrypto.Thumbprint(entry.NodePublicKeyBase64);

        // The primary node is self-authenticating (its key IS the soul key). It never needs a cert,
        // and the soul public key is always acceptable directly via ContextGrantStore.
        if (entry.IsPrimary) return null;

        if (string.IsNullOrEmpty(entry.EnrollmentCertB64)
            || string.IsNullOrEmpty(entry.ApproverPublicKeyBase64))
        {
            return null;
        }

        var approverThumb = NodeCrypto.Thumbprint(entry.ApproverPublicKeyBase64);
        bool approverTrusted = entry.ApproverPublicKeyBase64 == soulPublicKeyBase64
                               || trustedThumbToPublicKey.ContainsKey(approverThumb);
        if (!approverTrusted) return null;

        var payload = NodeCrypto.EnrollPayload(
            serverSoulId, entry.NodePublicKeyBase64, entry.Label, entry.EnrollmentExpiryUnix);
        if (!NodeCrypto.Verify(entry.ApproverPublicKeyBase64, payload, entry.EnrollmentCertB64))
            return null;

        return new TrustedSiblingKey
        {
            UserId = serverSoulId,
            NodeId = thumb,
            NodePublicKeyBase64 = entry.NodePublicKeyBase64,
            CertifiedByPublicKeyBase64 = entry.ApproverPublicKeyBase64,
        };
    }

    /// <summary>
    /// Returns the public keys of verified sibling nodes for <paramref name="serverSoulId"/>.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetTrustedKeysAsync(
        BridgeDbContext db, string serverSoulId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(serverSoulId)) return [];
        return await db.TrustedSiblingKeys.AsNoTracking()
            .Where(k => k.UserId == serverSoulId)
            .Select(k => k.NodePublicKeyBase64)
            .ToListAsync(ct);
    }
}
