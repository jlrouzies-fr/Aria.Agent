namespace Aria.Bridge.Infrastructure;

public static class BridgePipeline
{
    public static WebApplication UseBridgePipeline(this WebApplication app)
    {
        // Private Network Access (Chrome): a page served from a LAN origin (e.g. http://192.168.x.x:5129)
        // fetching this loopback bridge triggers a PNA preflight. Answer it so per-circuit attestation
        // (§12) and model relay work from a remote-driven Aria.Web, not just localhost.
        app.Use(async (ctx, next) =>
        {
            if (HttpMethods.IsOptions(ctx.Request.Method) &&
                ctx.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
                ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            await next();
        });

        app.UseCors();
        app.UseRouting();

        // F-3: state-changing requests must originate from the local bridge UI. Defeats cross-origin
        // POSTs and DNS-rebinding. Placed after routing so the path is resolved; tunnel-relayed requests
        // pass because DirectTunnel forwards to localhost with no Origin header.
        app.UseMiddleware<LocalOriginMiddleware>();

        return app;
    }
}
