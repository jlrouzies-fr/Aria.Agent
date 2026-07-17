namespace Aria.Bridge.Infrastructure;

/// <summary>
/// Global defense against cross-origin web content and DNS-rebinding mutating the bridge.
/// Every state-changing request (POST/PUT/DELETE/PATCH) must appear to come from the bridge's own
/// loopback UI, unless the path is on the small allowlist of endpoints that legitimately serve
/// cross-origin browser traffic (e.g. browser attestation).
///
/// Tunnel-relayed requests are unaffected: <see cref="DirectTunnel.HandleLocalRestAsync"/> forwards
/// via HttpClient to <c>http://localhost:5741</c>, producing a local Host and no Origin header.
///
/// See <c>docs/security/hardening-plan.md</c> F-3.
/// </summary>
public class LocalOriginMiddleware
{
    private readonly RequestDelegate _next;

    // Endpoints that legitimately accept non-local origins. Keep this list tiny and review each entry.
    private static readonly HashSet<string> AllowedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/node/attest", // browser proves bridge control from Aria.Web (may be on a LAN IP / cross-origin)
        "/health",      // benign health probe; load balancers may POST it
        // Voice audio is POSTed browser→bridge DIRECT (never via the server) so it stays on the user's
        // machine. These accept the audio, transcribe locally/via the node's own key, and return text.
        "/transcribe/local", // on-device whisper.cpp
        "/transcribe",       // cloud whisper via the node's own key
    };

    public LocalOriginMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var method = ctx.Request.Method;
        var path   = ctx.Request.Path.Value ?? "";

        // Preflight and reads are not state-changing.
        if (HttpMethods.IsOptions(method) || HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsTrace(method))
        {
            await _next(ctx);
            return;
        }

        if (AllowedPaths.Contains(path))
        {
            await _next(ctx);
            return;
        }

        if (!LocalRequestGuard.IsLocalOrigin(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"error":"Request must originate from the local bridge UI.","localOriginRequired":true}""");
            return;
        }

        await _next(ctx);
    }
}
