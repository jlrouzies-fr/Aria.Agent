#if DEBUG
using Aria.Shared;
using Aria.Web.Data;
using Aria.Web.Data.Bridge;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Debug;

/// <summary>
/// DEBUG-only helper: pre-enroll a bridge node without the normal certificate/approval ceremony.
/// This exists solely to let a developer run multiple local bridge instances from
/// <c>scripts/dev-fleet.sh</c>; it is compiled out of Release builds and refuses to run outside
/// the Development environment.
/// </summary>
public static class BridgeEnrollmentDebugApiEndpoints
{
    public static void Register(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            return;

        var grp = app.MapGroup("/api/debug");

        grp.MapPost("/enroll-node", async (EnrollDebugRequest req, IDbContextFactory<AppDbContext> dbFactory) =>
        {
            if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.NodePublicKey))
                return Results.BadRequest("userId and nodePublicKey required");

            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.UserId);
            if (string.IsNullOrEmpty(user?.PublicKey))
                return Results.NotFound("Unknown soul");

            var thumb = NodeCrypto.Thumbprint(req.NodePublicKey);
            var existing = await db.SoulNodeKeys.FirstOrDefaultAsync(k => k.UserId == req.UserId && k.NodeId == thumb);
            if (existing == null)
            {
                db.SoulNodeKeys.Add(new SoulNodeKey
                {
                    UserId = req.UserId,
                    NodeId = thumb,
                    NodePublicKeyBase64 = req.NodePublicKey,
                    Label = req.Label,
                    Platform = req.Platform,
                    IsPrimary = false,
                    Revoked = false,
                });
            }
            else
            {
                existing.Revoked = false;
                existing.RevokedAt = null;
                existing.Label = req.Label;
                existing.Platform = req.Platform;
                existing.LastSeenAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, nodeId = thumb });
        });
    }

    public record EnrollDebugRequest(string UserId, string NodePublicKey, string? Label, string? Platform);
}
#endif
