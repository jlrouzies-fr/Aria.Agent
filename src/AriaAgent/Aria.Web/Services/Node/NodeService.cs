using System.Text.Json;
using Aria.Shared;
using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.Node;

public record NodeInfo(string NodeId, string? Label, string? Platform, bool IsPrimary, bool Revoked,
    bool Online, DateTime EnrolledAt, DateTime LastSeenAt);

/// <summary>
/// Node (bridge) management for the remote-nodes feature: list, enroll, revoke, and channel pinning.
/// Enroll/revoke ask a connected (approver) bridge to sign over the existing tunnel
/// (<see cref="ModelBridgeRegistry.SendLocalRestAsync"/> → bridge /node/sign-*), then verify+apply
/// against the {soul ∪ non-revoked nodes} authority set. Used by both the Blazor UI and the API.
/// </summary>
public class NodeService(IDbContextFactory<AppDbContext> dbFactory, ModelBridgeRegistry registry,
    PendingEnrollmentService pendings, CircuitAuthService circuitAuth, ILogger<NodeService> log)
{
    public async Task<List<NodeInfo>> GetNodesAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows   = await db.SoulNodeKeys.AsNoTracking().Where(k => k.UserId == userId).ToListAsync();
        var online = registry.GetNodes(userId.ToString()).Select(n => n.NodeId).ToHashSet();
        return rows.Select(k => new NodeInfo(k.NodeId, k.Label, k.Platform, k.IsPrimary, k.Revoked,
            online.Contains(k.NodeId), k.EnrolledAt, k.LastSeenAt)).ToList();
    }

    public IReadOnlyList<PendingNodeInfo> GetPending(string userId) => pendings.List(userId);

    /// <summary>Approves a pending device: verifies the human-entered code, then runs the normal
    /// signed enrollment (an approver bridge signs the cert over its tunnel). Pairing code proves the
    /// human is looking at the real device; the signature proves co-equal-owner authority.</summary>
    public async Task<(bool Ok, string? Error, string? NodeId)> ApprovePendingAsync(string userId, string nodeId, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return (false, "Enter the join code shown on the device", null);
        var pending = pendings.TakeIfCodeMatches(userId, nodeId, code);
        if (pending == null) return (false, "Wrong or expired code", null);

        var (ok, error, thumb) = await RequestEnrollAsync(userId, pending.NodePublicKey, pending.Label, pending.Platform);
        // Re-queue on transient failure so the user can retry without re-pairing the device.
        if (!ok) pendings.Add(userId, pending);
        return (ok, error, thumb);
    }

    /// <summary>Fully removes a node from the allow-list (not just a revoke tombstone) so the device
    /// must re-pair from scratch to return. Authorized + the live connection dropped, same as revoke.</summary>
    public async Task<(bool Ok, string? Error)> DeleteNodeAsync(string userId, string nodeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (string.IsNullOrEmpty(user?.PublicKey)) return (false, "Unknown soul");
        var target = await db.SoulNodeKeys.FirstOrDefaultAsync(k => k.UserId == userId && k.NodeId == nodeId);
        if (target == null)   return (false, "Unknown node");
        if (target.IsPrimary) return (false, "Cannot delete the primary node");

        var (authorized, error) = await AuthorizeRemovalAsync(db, user.PublicKey, userId, nodeId, target.NodePublicKeyBase64, "delete");
        if (!authorized) return (false, error);

        db.SoulNodeKeys.Remove(target);
        await db.SaveChangesAsync();
        registry.RemoveNode(userId.ToString(), nodeId);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error, string? NodeId)> RequestEnrollAsync(
        string userId, string newNodePublicKey, string? label, string? platform)
    {
        if (string.IsNullOrWhiteSpace(newNodePublicKey)) return (false, "Node public key required", null);
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (string.IsNullOrEmpty(user?.PublicKey)) return (false, "Unknown soul", null);
        if (!registry.HasBridge(userId.ToString())) return (false, "No bridge connected to approve enrollment", null);

        var expiry   = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var signBody = JsonSerializer.Serialize(new { serverSoulId = userId, newNodePublicKey, label = label ?? "", expiryUnix = expiry });
        var resp = await registry.SendLocalRestAsync(userId.ToString(), "POST", "/node/sign-enrollment", signBody);
        if (resp is not { StatusCode: 200, Body: { } body }) return (false, "Approver bridge did not sign", null);

        string approverPub, cert; string? wrappedDek;
        try { using var d = JsonDocument.Parse(body); approverPub = d.RootElement.GetProperty("approverPublicKey").GetString()!; cert = d.RootElement.GetProperty("certificate").GetString()!;
              wrappedDek = d.RootElement.TryGetProperty("wrappedDek", out var w) ? w.GetString() : null; }
        catch { return (false, "Bad signer response", null); }

        if (!await ApproverInSet(db, userId, user.PublicKey, approverPub)) return (false, "Approver not authorized", null);
        if (!NodeCrypto.Verify(approverPub, NodeCrypto.EnrollPayload(userId, newNodePublicKey, label ?? "", expiry), cert))
            return (false, "Invalid certificate", null);

        var thumb    = NodeCrypto.Thumbprint(newNodePublicKey);
        var existing = await db.SoulNodeKeys.FirstOrDefaultAsync(k => k.UserId == userId && k.NodeId == thumb);
        if (existing == null)
            db.SoulNodeKeys.Add(new SoulNodeKey
            {
                UserId = userId, NodeId = thumb, NodePublicKeyBase64 = newNodePublicKey,
                Label = label, Platform = platform,
                EnrolledByNodeId = NodeCrypto.Thumbprint(approverPub),
                WrappedDek = wrappedDek,
                EnrollmentCertB64 = cert,
                ApproverPublicKeyBase64 = approverPub,
                EnrollmentExpiryUnix = expiry,
            });
        else
        {
            existing.Revoked = false; existing.RevokedAt = null;
            existing.Label = label; existing.Platform = platform;
            if (wrappedDek != null) existing.WrappedDek = wrappedDek;
            existing.EnrollmentCertB64 = cert;
            existing.ApproverPublicKeyBase64 = approverPub;
            existing.EnrollmentExpiryUnix = expiry;
        }
        await db.SaveChangesAsync();
        return (true, null, thumb);
    }

    public async Task<(bool Ok, string? Error)> RequestRevokeAsync(string userId, string nodeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (string.IsNullOrEmpty(user?.PublicKey)) return (false, "Unknown soul");
        var target = await db.SoulNodeKeys.FirstOrDefaultAsync(k => k.UserId == userId && k.NodeId == nodeId);
        if (target == null)   return (false, "Unknown node");
        if (target.IsPrimary) return (false, "Cannot revoke the primary node");

        var (authorized, error) = await AuthorizeRemovalAsync(db, user.PublicKey, userId, nodeId, target.NodePublicKeyBase64, "revoke");
        if (!authorized) return (false, error);

        target.Revoked = true; target.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        registry.RemoveNode(userId.ToString(), nodeId);
        return (true, null);
    }

    /// <summary>
    /// Authorizes removing (revoke or delete) a node. Co-equal-owner rule (§9.3): the action needs a
    /// signature from the soul key or any non-revoked enrolled node. We try EVERY connected bridge in
    /// turn (preferring a witness over the target itself) until one returns an in-set, verifiable
    /// signature — robust against <see cref="ModelBridgeRegistry.GetDefaultNode"/> happening to pick a
    /// connection whose key isn't (or is no longer) in the allow-list.
    /// <para/>
    /// Fallback: if no authorized bridge is online to sign, we permit the removal only when THIS circuit
    /// is already soul-verified for the soul (the human proved control of a co-equal bridge to unlock).
    /// This is the recovery path for orphaned / offline devices. It does NOT widen server authority:
    /// node management is in-process only (no LAN REST surface, §12), the server can already edit its own
    /// DB, and the revoke signature never protected against the trusted server operator — only against a
    /// network attacker, which §12 closed. See bridge-remote-nodes-security.md.
    /// </summary>
    private async Task<(bool Ok, string? Error)> AuthorizeRemovalAsync(
        AppDbContext db, string soulPubKey, string userId, string targetNodeId, string targetPubKey, string verb)
    {
        var now      = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signBody = JsonSerializer.Serialize(new { serverSoulId = userId, targetNodePublicKey = targetPubKey, nowUnix = now });

        // Prefer a co-equal witness (a node other than the one being removed); fall back to self-sign.
        var candidates = registry.GetNodes(userId.ToString())
            .OrderBy(n => n.NodeId == targetNodeId).ToList();

        foreach (var node in candidates)
        {
            var resp = await registry.SendLocalRestAsync(userId.ToString(), "POST", "/node/sign-revocation", signBody, node.NodeId);
            if (resp is not { StatusCode: 200, Body: { } body })
            {
                log.LogWarning("[Node/{Verb}] signer {Node} did not respond (status={Status})", verb, node.NodeId, resp?.StatusCode);
                continue;
            }

            string approverPub, sig;
            try { using var d = JsonDocument.Parse(body); approverPub = d.RootElement.GetProperty("approverPublicKey").GetString()!; sig = d.RootElement.GetProperty("signature").GetString()!; }
            catch { log.LogWarning("[Node/{Verb}] signer {Node} returned a malformed response", verb, node.NodeId); continue; }

            if (!await ApproverInSet(db, userId, soulPubKey, approverPub))
            {
                log.LogWarning("[Node/{Verb}] signer {Node} returned out-of-set key {Thumb} (soul={Uid}) — skipping",
                    verb, node.NodeId, NodeCrypto.Thumbprint(approverPub), userId);
                continue;
            }
            if (!NodeCrypto.Verify(approverPub, NodeCrypto.RevokePayload(userId, targetPubKey, now), sig))
            {
                log.LogWarning("[Node/{Verb}] signer {Node} produced an invalid signature", verb, node.NodeId);
                continue;
            }

            log.LogInformation("[Node/{Verb}] {Target} authorized by signer {Node} for soul {Uid}", verb, targetNodeId, node.NodeId, userId);
            return (true, null);
        }

        // No co-equal bridge online to sign — fall back to the soul-verified circuit.
        if (circuitAuth.IsVerified(userId))
        {
            log.LogWarning("[Node/{Verb}] {Target} authorized via soul-verified circuit for soul {Uid} (no online co-equal signer)",
                verb, targetNodeId, userId);
            return (true, null);
        }

        log.LogWarning("[Node/{Verb}] {Target} DENIED for soul {Uid}: no online authorized signer and circuit not soul-verified",
            verb, targetNodeId, userId);
        return (false, "No authorized bridge is online to approve this, and this session isn't soul-verified. " +
                       "Unlock this soul (loopback or session code) on a linked bridge, then retry.");
    }

    // Co-equal authority: the soul key, or any non-revoked enrolled node.
    private static async Task<bool> ApproverInSet(AppDbContext db, string userId, string soulPubKey, string approverPub)
    {
        if (approverPub == soulPubKey) return true;
        var thumb = NodeCrypto.Thumbprint(approverPub);
        return await db.SoulNodeKeys.AnyAsync(k => k.UserId == userId && k.NodeId == thumb && !k.Revoked);
    }
}
