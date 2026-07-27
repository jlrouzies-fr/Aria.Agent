namespace Aria.Web.Endpoints;

public static class DebugEndpoints
{
    public static WebApplication MapDebugEndpoints(this WebApplication app)
    {
#if DEBUG
        Aria.Web.Debug.WargameApiEndpoints.Register(app);
        Aria.Web.Debug.ChatDebugApiEndpoints.Register(app);
        Aria.Web.Debug.BridgeDebugApiEndpoints.Register(app);
        Aria.Web.Debug.BridgeEnrollmentDebugApiEndpoints.Register(app);
        Aria.Web.Debug.McpBridgeDebugApiEndpoints.Register(app);
        Aria.Web.Debug.ProjectFilesDebugApiEndpoints.Register(app);
        Aria.Web.Debug.CronDebugApiEndpoints.MapCronDebugEndpoints(app);
        Aria.Web.Debug.HiveDebugApiEndpoints.MapHiveDebugEndpoints(app);
#endif
        return app;
    }
}
