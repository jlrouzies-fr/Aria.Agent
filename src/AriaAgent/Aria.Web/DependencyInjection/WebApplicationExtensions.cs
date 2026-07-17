using Aria.Web.Services;
using Aria.Web.Services.ModelBridge;
using Microsoft.AspNetCore.SignalR;

namespace Aria.Web.DependencyInjection;

public static class WebApplicationExtensions
{
    public static WebApplication WireAriaServices(this WebApplication app)
    {
        // Wire WargameTools delegate after DI resolves both singletons
        var wargameSvc = app.Services.GetRequiredService<WargameService>();
        Aria.Tools.WargameTools.Configure(() => wargameSvc.BuildSituationReport());

        // Wire bridge: give the registry its hub context and give AgentService the registry
        var bridgeRegistry = app.Services.GetRequiredService<ModelBridgeRegistry>();
        var hubCtx = app.Services.GetRequiredService<IHubContext<ModelBridgeHub>>();
        bridgeRegistry.SetHub(hubCtx);
        app.Services.GetRequiredService<AgentService>().SetBridge(bridgeRegistry);

        // Push server config snapshots to the bridge when it connects.
        var bridgeSync = app.Services.GetRequiredService<BridgeSyncService>();
        bridgeRegistry.DirectBridgeRegistered += userId => _ = bridgeSync.PushSnapshotAsync(userId);

        return app;
    }
}
