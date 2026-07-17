using Aria.Shared;
using Aria.Web.Data;
using Aria.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Endpoints;

public static class BridgeNodeEndpoints
{
    public static WebApplication MapBridgeNodeEndpoints(this WebApplication app)
    {
        // ── Bridge remote nodes: enrollment & revocation (co-equal owners, §9.3) ──────────
        // An enroll/revoke is valid only if signed by the soul key OR any non-revoked node already in the
        // allow-list. The certificate/signature is verified against that live set, so another user can't
        // enroll a node into this soul (their key won't verify against this soul's set).
        app.MapPost("/api/bridge/enroll-node", async (EnrollNodeRequest req,
            IDbContextFactory<AppDbContext> dbFactory) =>
        {
            if (string.IsNullOrWhiteSpace(req.NewNodePublicKey) || string.IsNullOrWhiteSpace(req.ApproverPublicKey)
                || string.IsNullOrWhiteSpace(req.Certificate))
                return Results.BadRequest("Missing fields");

            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.UserId);
            if (string.IsNullOrEmpty(user?.PublicKey)) return Results.NotFound("Unknown soul");

            if (!await ApproverInSet(db, req.UserId, user.PublicKey, req.ApproverPublicKey))
                return Results.Json(new { ok = false, error = "Approver not authorized" }, statusCode: 403);
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > req.ExpiryUnix)
                return Results.Json(new { ok = false, error = "Certificate expired" }, statusCode: 400);

            var payload = NodeCrypto.EnrollPayload(req.UserId, req.NewNodePublicKey, req.Label ?? "", req.ExpiryUnix);
            if (!NodeCrypto.Verify(req.ApproverPublicKey, payload, req.Certificate))
                return Results.Json(new { ok = false, error = "Invalid certificate" }, statusCode: 403);

            var thumb    = NodeCrypto.Thumbprint(req.NewNodePublicKey);
            var existing = await db.SoulNodeKeys.FirstOrDefaultAsync(k => k.UserId == req.UserId && k.NodeId == thumb);
            if (existing == null)
                db.SoulNodeKeys.Add(new SoulNodeKey
                {
                    UserId = req.UserId, NodeId = thumb, NodePublicKeyBase64 = req.NewNodePublicKey,
                    Label = req.Label, Platform = req.Platform,
                    EnrolledByNodeId = NodeCrypto.Thumbprint(req.ApproverPublicKey),
                    WrappedDek = req.WrappedDek,
                    EnrollmentCertB64 = req.Certificate,
                    ApproverPublicKeyBase64 = req.ApproverPublicKey,
                    EnrollmentExpiryUnix = req.ExpiryUnix,
                });
            else
            {
                existing.Revoked = false; existing.RevokedAt = null;
                existing.Label = req.Label; existing.Platform = req.Platform;
                if (req.WrappedDek != null) existing.WrappedDek = req.WrappedDek;
                existing.EnrollmentCertB64 = req.Certificate;
                existing.ApproverPublicKeyBase64 = req.ApproverPublicKey;
                existing.EnrollmentExpiryUnix = req.ExpiryUnix;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, nodeId = thumb });
        });

        app.MapPost("/api/bridge/revoke-node", async (RevokeNodeRequest req,
            IDbContextFactory<AppDbContext> dbFactory, ModelBridgeRegistry registry) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.UserId);
            if (string.IsNullOrEmpty(user?.PublicKey)) return Results.NotFound("Unknown soul");

            if (!await ApproverInSet(db, req.UserId, user.PublicKey, req.ApproverPublicKey))
                return Results.Json(new { ok = false, error = "Approver not authorized" }, statusCode: 403);

            var payload = NodeCrypto.RevokePayload(req.UserId, req.TargetNodePublicKey, req.NowUnix);
            if (!NodeCrypto.Verify(req.ApproverPublicKey, payload, req.Signature))
                return Results.Json(new { ok = false, error = "Invalid signature" }, statusCode: 403);

            var thumb  = NodeCrypto.Thumbprint(req.TargetNodePublicKey);
            var target = await db.SoulNodeKeys.FirstOrDefaultAsync(k => k.UserId == req.UserId && k.NodeId == thumb);
            if (target == null)     return Results.NotFound("Unknown node");
            if (target.IsPrimary)   return Results.Json(new { ok = false, error = "Cannot revoke the primary (soul-key) node" }, statusCode: 400);
            target.Revoked = true; target.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            registry.RemoveNode(req.UserId, thumb);   // stop routing to the revoked node immediately
            return Results.Ok(new { ok = true });
        });

        // NOTE (§12 hardening): node list / enroll / revoke / approve / delete are intentionally NOT exposed
        // as REST. The Aria.Web UI performs them in-process via NodeService from a per-circuit soul-verified
        // session, so there is no unauthenticated LAN surface that returns soul data or mutates the
        // allow-list. (An earlier `approve-enrollment` REST endpoint was a 6-digit brute-force hole.)

        // Pairing-code enrollment. A freshly-joined bridge (no enrollment yet, so it can't use the tunnel)
        // registers itself here as a PENDING device with a short join code; the human approves it from a
        // soul-verified session by typing that code (see NodeService.ApprovePendingAsync). Nothing is
        // granted by this call alone — approval still requires the code AND an approver-bridge signature.
        app.MapPost("/api/bridge/pending-enroll", async (PendingEnrollRequest req,
            IDbContextFactory<AppDbContext> dbFactory, PendingEnrollmentService pendings) =>
        {
            if (string.IsNullOrWhiteSpace(req.NodePublicKey) || string.IsNullOrWhiteSpace(req.Code))
                return Results.BadRequest("Missing fields");

            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.ServerSoulId);
            if (string.IsNullOrEmpty(user?.PublicKey)) return Results.NotFound("Unknown soul");

            var nodeId = NodeCrypto.Thumbprint(req.NodePublicKey);
            if (await db.SoulNodeKeys.AnyAsync(k => k.UserId == req.ServerSoulId && k.NodeId == nodeId && !k.Revoked))
                return Results.Ok(new { ok = true, nodeId, alreadyEnrolled = true });

            pendings.Add(req.ServerSoulId, new PendingEnrollment(
                nodeId, req.NodePublicKey, req.Label ?? "", req.Platform ?? "",
                PendingEnrollmentService.HashCode(req.Code), DateTime.UtcNow));
            return Results.Ok(new { ok = true, nodeId });
        });

        // (pending list / approve / delete are UI-only, performed in-process by NodeService — see note above.)

        return app;
    }

    private static async Task<bool> ApproverInSet(AppDbContext db, string userId, string soulPubKey, string approverPub)
    {
        if (approverPub == soulPubKey) return true;
        var thumb = NodeCrypto.Thumbprint(approverPub);
        return await db.SoulNodeKeys.AnyAsync(k => k.UserId == userId && k.NodeId == thumb && !k.Revoked);
    }
}

// Bridge remote-nodes enrollment DTOs (§9.3). Certificate/Signature are base64 ECDSA over the
// canonical payloads in NodeCrypto (EnrollPayload / RevokePayload).
public record EnrollNodeRequest(string UserId, string NewNodePublicKey, string? Label, string? Platform,
    string ApproverPublicKey, string Certificate, long ExpiryUnix, string? WrappedDek = null);
public record RevokeNodeRequest(string UserId, string TargetNodePublicKey,
    string ApproverPublicKey, string Signature, long NowUnix);

// Pairing-code DTO: a joined bridge registers itself pending; the human approves it in-process (§12).
// NumberOrStringConverter handles old bridge daemons that serialise serverSoulId as a JSON number.
public record PendingEnrollRequest(
    [property: System.Text.Json.Serialization.JsonConverter(typeof(NumberOrStringConverter))] string ServerSoulId,
    string NodePublicKey, string? Label, string? Platform, string Code);
