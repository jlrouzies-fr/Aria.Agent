#if DEBUG
using System.Net;
using Aria.Bridge.Data;
using Aria.Shared;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// DEBUG-only dev-fleet helper: lets a developer bootstrap sibling trust between local bridge
/// instances without the production enrollment-certificate ceremony (which needs an
/// already-trusted device to sign, unavailable when every node is a throwaway debug instance).
/// Compiled out of Release builds, registered only in the Development environment, and restricted
/// to loopback callers. The production trust model is untouched — this writes the same
/// <c>TrustedSiblingKeys</c> rows the cert path would, so <see cref="Services.Trust.SiblingRoster"/>
/// and <c>ContextGrantStore.AcceptableKeysAsync</c> work exactly as in production afterwards.
/// </summary>
public static class DebugTrustEndpoints
{
    public static void MapDebugTrustEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            return;

        app.MapPost("/debug/trust-sibling", async (DebugTrustSiblingRequest req, HttpContext http, BridgeDbContext db) =>
        {
            // Loopback only: a debug convenience must never become a remote trust-injection vector.
            if (!IPAddress.IsLoopback(http.Connection.RemoteIpAddress ?? IPAddress.IPv6None))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.NodePublicKey))
                return Results.BadRequest("userId and nodePublicKey required");

            var nodeId = NodeCrypto.Thumbprint(req.NodePublicKey);
            var existing = await db.TrustedSiblingKeys
                .FirstOrDefaultAsync(k => k.UserId == req.UserId && k.NodeId == nodeId);
            if (existing == null)
            {
                db.TrustedSiblingKeys.Add(new TrustedSiblingKey
                {
                    UserId = req.UserId,
                    NodeId = nodeId,
                    NodePublicKeyBase64 = req.NodePublicKey,
                    // No cert vouches for this key — the debug endpoint did. Keeping the field a
                    // valid key (the sibling's own) avoids surprising any future consumer of it.
                    CertifiedByPublicKeyBase64 = req.NodePublicKey,
                });
            }
            else
            {
                existing.NodePublicKeyBase64 = req.NodePublicKey;
                existing.CertifiedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, nodeId });
        });
    }

    public record DebugTrustSiblingRequest(string UserId, string NodePublicKey);
}
#endif
