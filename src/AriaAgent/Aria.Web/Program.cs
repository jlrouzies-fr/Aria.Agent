using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Data Protection keys are persisted to the Fly.io volume in production so cookie
// protection (aria-trusted / aria-worthy) survives redeploys and machine restarts.
// The path can be overridden with the DataProtection__KeysPath config value.
if (!builder.Environment.IsDevelopment())
{
    var dpKeysPath = builder.Configuration["DataProtection:KeysPath"] ?? "/data/dp-keys";
    try
    {
        Directory.CreateDirectory(dpKeysPath);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath))
            .SetApplicationName("aria-cogitator");
    }
    catch
    {
        // If the configured path is not writable (e.g., local prod sanity check), fall back
        // to ephemeral keys. Cookies won't survive redeploys, but the app stays usable.
        builder.Services.AddDataProtection()
            .SetApplicationName("aria-cogitator");
    }
}
else
{
    builder.Services.AddDataProtection()
        .SetApplicationName("aria-cogitator");
}

// Focused-debug stream/request logs are wiped each run so they don't grow unbounded.
Aria.Agent.UniversalReasoningHandler.ClearDebugLogs();

builder.Services.AddAriaServices(builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Wire runtime service cross-references after DI is available.
app.WireAriaServices();

// Ensure DB is created on startup (no migrations needed for this project)
await app.EnsureAriaDatabaseAsync();

app.UseForwardedHeaders();
app.UseSecurityHeaders();
app.UseRouting();
app.UseRateLimiter();
app.UseAccessGate();
app.UseAriaPipeline();
app.MapAriaEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapGet("/api/debug/forbidden", () =>
        Results.Content(
            Aria.Web.Middleware.AccessGateMiddleware.ForbiddenPageHtml("127.0.0.1"),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status403Forbidden));
}

app.Run();

namespace Aria.Web
{
    /// <summary>
    /// Makes the top-level <c>Program</c> class visible to <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>.
    /// </summary>
    public partial class Program
    {
    }
}
