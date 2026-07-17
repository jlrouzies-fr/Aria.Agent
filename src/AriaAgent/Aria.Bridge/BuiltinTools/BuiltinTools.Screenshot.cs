using System.Text.Json;
using Microsoft.Playwright;

namespace Aria.Bridge;

// Screenshots a page running on the user's own machine (localhost only) via a headless Chromium
// instance launched on the bridge. The browser is auto-installed on first use so nothing extra is
// required on macOS/Windows/Linux beyond the bridge itself.
public static partial class BuiltinTools
{
    private static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "::1"];
    private static readonly SemaphoreSlim _chromiumInstallLock = new(1, 1);
    private static bool _chromiumInstalled;

    private static IEnumerable<BridgeToolInfo> ScreenshotToolInfos()
    {
        yield return new("TakeScreenshot",
            "Takes a screenshot of a page running on localhost (e.g. a dev server) using a headless browser on this machine. Only localhost/127.0.0.1 URLs are allowed.",
            Js("""
               {"type":"object",
                "properties":{"url":{"type":"string","description":"The localhost URL to capture, e.g. http://localhost:5129/chat. Must be localhost or 127.0.0.1."}},
                "required":["url"]}
               """));
    }

    private static async Task<ToolCallResponse> TakeScreenshotAsync(Dictionary<string, JsonElement> args)
    {
        var urlStr = args.Str("url") ?? throw new ArgumentException("'url' is required");

        if (!Uri.TryCreate(urlStr, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return Err("Invalid URL. Provide an absolute http:// or https:// URL.");

        if (!LocalHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            return Err($"Refused: '{uri.Host}' is not localhost. This tool only captures pages running on the user's own machine (localhost/127.0.0.1) — use a web-fetch tool for remote pages.");

        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await LaunchChromiumAsync(playwright);
            var page = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
            });

            await page.GotoAsync(uri.ToString(), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout   = 15_000
            });

            // JPEG, not PNG: this is shipped inline (base64) over the tunnel and, when the active
            // model has vision, straight into the chat request — keep the payload small.
            var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type    = ScreenshotType.Jpeg,
                Quality = 80
            });

            var text = $"Captured screenshot of {uri} (1280x800 viewport, {bytes.Length / 1024} KB).";
            return new ToolCallResponse(text, false, Convert.ToBase64String(bytes), "image/jpeg");
        }
        catch (Exception ex)
        {
            return Err($"Screenshot failed: {ex.Message}");
        }
    }

    private static async Task<IBrowser> LaunchChromiumAsync(IPlaywright playwright)
    {
        try
        {
            return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (PlaywrightException) when (!_chromiumInstalled)
        {
            await EnsureChromiumInstalledAsync();
            return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
    }

    private static async Task EnsureChromiumInstalledAsync()
    {
        await _chromiumInstallLock.WaitAsync();
        try
        {
            if (_chromiumInstalled) return;
            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode != 0)
                throw new InvalidOperationException($"'playwright install chromium' exited with code {exitCode}.");
            _chromiumInstalled = true;
        }
        finally { _chromiumInstallLock.Release(); }
    }
}
