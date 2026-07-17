namespace Aria.Bridge.Endpoints;

public static class EndpointsMapper
{
    public static WebApplication MapBridgeEndpoints(this WebApplication app)
    {
        app.MapSoulEndpoints();
        app.MapCogitationEndpoints();
        app.MapHiveEndpoints();
        app.MapContactEndpoints();
        app.MapLlmKeyEndpoints();
        app.MapLocalWhisperEndpoints();
        app.MapNodeEndpoints();
        app.MapProjectFileEndpoints();
        app.MapGitEndpoints();
        app.MapStatusEndpoints();
        app.MapDbAdminEndpoints();
        app.MapToolEndpoints();
        app.MapSyncEndpoints();
        app.MapConsoleEndpoints();
        app.MapMcpEndpoints();
        app.MapSealEndpoints();
        app.MapContextEndpoints();
        app.MapOAuthEndpoints();
        app.MapMemoryEndpoints();
        app.MapTerminalEndpoints();
        app.MapChannelEndpoints();

        return app;
    }
}
