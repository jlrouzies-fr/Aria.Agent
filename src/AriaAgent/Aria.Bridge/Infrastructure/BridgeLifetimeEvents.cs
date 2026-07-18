using Aria.Bridge.Services.Logging;

namespace Aria.Bridge.Infrastructure;

public static class BridgeLifetimeEvents
{
    public static WebApplication RegisterBridgeLifetimeEvents(this WebApplication app)
    {
        // Open the status page in the default browser once the app is ready.
        var openBrowser = app.Configuration.GetValue("Bridge:OpenBrowserOnStart", defaultValue: true);
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            const string url = "http://localhost:5741/";
            BridgeLogger.Log("INFO", $"Bridge v{BridgeLogger.Version} started — log file: {BridgeLogger.LogFilePath}");
            Console.WriteLine($"\n  ARIA // BRIDGE v{BridgeLogger.Version} — OPERATIONAL");
            Console.WriteLine($"  Status page: {url}\n");
            if (openBrowser)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
                    System.Diagnostics.Process.Start(psi);
                }
                catch { /* no browser available — URL already printed above */ }
            }

            // The installer runs the daemon with -WindowStyle Hidden on Windows — give it a
            // notification-area icon so the user can find, open, and quit it.
            if (OperatingSystem.IsWindows() &&
                app.Configuration.GetValue("Bridge:TrayIcon", defaultValue: true))
            {
                WindowsTrayIcon.Start(onQuit: app.Lifetime.StopApplication);
            }
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            if (OperatingSystem.IsWindows()) WindowsTrayIcon.Stop();
        });

        return app;
    }
}
