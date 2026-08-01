using Aria.Bridge;
using Aria.Bridge.Data;
using Aria.Bridge.Endpoints;
using Aria.Bridge.Infrastructure;
using Aria.Bridge.Services.Logging;

if (args.Contains("--version") || args.Contains("-v"))
{
    Console.WriteLine(BridgeLogger.Version);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.AddBridgeServices();

var app = builder.Build();

BuiltinTools.ConfigureMemory(app.Services);
BuiltinTools.ConfigureDiffFeedback(builder.Configuration);

app.UseBridgePipeline();
await app.InitializeBridgeDatabaseAsync();
app.MapBridgeEndpoints();
app.RegisterBridgeLifetimeEvents();

// macOS AppKit insists NSStatusItem is created on the main thread, so when the menu-bar icon
// is enabled the host runs on a worker and main pumps -[NSApplication run]. Windows / Linux
// (and macOS with Bridge:TrayIcon=false) keep the ordinary blocking Run().
if (OperatingSystem.IsMacOS() &&
    app.Configuration.GetValue("Bridge:TrayIcon", defaultValue: true))
{
    MacMenuBarIcon.RunWebHostWithMenuBar(app);
}
else
{
    app.Run();
}

namespace Aria.Bridge
{
    /// <summary>
    /// Makes the top-level Program class visible to <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>.
    /// </summary>
    public partial class Program { }
}
