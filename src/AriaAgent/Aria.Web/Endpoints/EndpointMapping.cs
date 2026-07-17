namespace Aria.Web.Endpoints;

public static class EndpointMapping
{
    // Process start time — the simplest way to confirm a deploy actually took effect: compare this
    // against when you pushed. A fresh timestamp means the running process is the new one.
    private static readonly DateTimeOffset StartedAtUtc = DateTimeOffset.UtcNow;

    public static WebApplication MapAriaEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new
        {
            status        = "ok",
            startedAtUtc  = StartedAtUtc,
            uptimeSeconds = (int)(DateTimeOffset.UtcNow - StartedAtUtc).TotalSeconds,
        }));

        app.MapBridgeNodeEndpoints();
        app.MapSoulEndpoints();
        app.MapVoxEndpoints();
        app.MapDeviceEndpoints();
        app.MapAccessEndpoints();
        app.MapMaintenanceEndpoints();
        app.MapDebugEndpoints();

        return app;
    }
}
