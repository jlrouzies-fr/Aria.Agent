using Aria.Harness.Governance;
using Aria.Web.Helpers;
using Aria.Web.Services.Auth;
using Aria.Web.Services.ModelBridge;

namespace Aria.Web.Endpoints;

/// <summary>
/// Layer A device-trust approval (defense-in-depth plan §3). A soul-verified session asks its node to
/// sign a <c>trust-device</c> grant for THIS browser; once signed and recorded, the device passes the
/// access gate from any network without a guest code. The signature is produced at the node — the
/// server cannot forge it — so only a human at the node can trust a device.
/// </summary>
public static class DeviceEndpoints
{
    private static readonly TimeSpan DeviceGrantLifetime = TimeSpan.FromDays(90);

    public static WebApplication MapDeviceEndpoints(this WebApplication app)
    {
        // Trust the CURRENT browser (its device-id cookie) for the given soul. Requires that soul to be
        // verified (a live, key-proven bridge is connected) AND drives the node Seal ceremony — the
        // human approves at the node, which signs the grant. Blocks/returns 403 otherwise.
        app.MapPost("/api/devices/trust-this", async (
            HttpContext ctx, TrustDeviceRequest req,
            ModelBridgeRegistry registry, GrantService grants, TrustedDeviceService devices,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.UserId) || !registry.IsSoulVerified(req.UserId))
                return Results.Json(new { ok = false, error = "Soul not verified" },
                    statusCode: StatusCodes.Status403Forbidden);

            var deviceId = devices.ReadDeviceId(ctx);
            if (string.IsNullOrWhiteSpace(deviceId))
                return Results.BadRequest(new { ok = false, error = "No device id on this browser yet" });

            var ip    = ClientIpResolver.GetClientIp(ctx)?.ToString();
            var label = string.IsNullOrWhiteSpace(req.Label) ? "This browser" : req.Label!.Trim();
            var desc  = new ActionDescriptor(
                "trust-device",
                $"device={deviceId[..8]}…  ip={ip ?? "unknown"}  label={label}",
                "Authorise this browser to reach the terminal from any network",
                null, ToolSeverity.NeedsSeal);

            // SubjectId = deviceId, ContextId = soul — must match TrustedDeviceService verification.
            var grant = await grants.RequestGrantAsync(
                req.UserId, GrantService.DeviceGrant, deviceId, req.UserId, DeviceGrantLifetime, desc, ct);
            if (grant == null)
                return Results.Json(new { ok = false, error = "Not approved at the node (refused, timed out, or unreachable)" },
                    statusCode: StatusCodes.Status403Forbidden);

            var stored = await devices.RecordTrustAsync(req.UserId, grant, label, ip, approvedByNodeId: null, ct);
            return stored
                ? Results.Ok(new { ok = true, expiresUnix = grant.ExpiryUnix })
                : Results.Json(new { ok = false, error = "Grant failed verification" },
                    statusCode: StatusCodes.Status403Forbidden);
        });

        // Revoke a trusted device for a soul (soul-verified). The next request from that device falls
        // back to the other gate tiers (guest code / knock) — i.e. it is no longer auto-trusted.
        app.MapPost("/api/devices/revoke", async (
            RevokeDeviceRequest req, ModelBridgeRegistry registry, TrustedDeviceService devices,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.UserId) || !registry.IsSoulVerified(req.UserId))
                return Results.Json(new { ok = false, error = "Soul not verified" },
                    statusCode: StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(req.DeviceId))
                return Results.BadRequest(new { ok = false, error = "deviceId required" });

            await devices.RevokeAsync(req.UserId, req.DeviceId, ct);
            return Results.Ok(new { ok = true });
        });

        return app;
    }
}

public record TrustDeviceRequest(string UserId, string? Label);
public record RevokeDeviceRequest(string UserId, string DeviceId);
