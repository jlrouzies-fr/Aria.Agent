using Aria.Web.Components;
using Aria.Web.Services.ModelBridge;
using Microsoft.AspNetCore.SignalR;

namespace Aria.Web.Middleware;

public static class PipelineExtensions
{
    public static WebApplication UseAriaPipeline(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.MapHub<ModelBridgeHub>("/api/modelbridge");

        return app;
    }
}
