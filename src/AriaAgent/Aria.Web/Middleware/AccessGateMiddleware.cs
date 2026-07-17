using System.Net;
using System.Text;
using Aria.Web.Helpers;
using Aria.Web.Services.Auth;
using Microsoft.AspNetCore.DataProtection;

namespace Aria.Web.Middleware;

public class AccessGateMiddleware
{
    public static string ForbiddenPageHtml(string remoteIp)
    {
        var ipLine = string.IsNullOrEmpty(remoteIp) ? "unknown" : remoteIp;
        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>ARIA // ACCESS DENIED</title>
            <style>
                :root {
                    --bg-base: #252525;
                    --bg-panel: #2d2d2d;
                    --border-active: #b06060;
                    --border-glow: #e07070;
                    --text-dead: #9a8282;
                    --text-muted: #cc9898;
                    --text-bright: #f5a0a0;
                    --text-title: #f06060;
                    --gold-bright: #d4a020;
                    --font-mono: 'Consolas', 'Courier New', monospace;
                    --glow-md: 0 0 12px rgba(180, 80, 80, 0.45);
                }
                * { box-sizing: border-box; margin: 0; padding: 0; }
                html, body {
                    height: 100%;
                    background: var(--bg-base);
                    color: var(--text-muted);
                    font-family: var(--font-mono);
                    font-size: 13px;
                    line-height: 1.55;
                    overflow: hidden;
                }
                body {
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    min-height: 100vh;
                    text-align: center;
                }
                body::after {
                    content: '';
                    position: fixed;
                    inset: 0;
                    background: repeating-linear-gradient(
                        0deg,
                        transparent,
                        transparent 1px,
                        rgba(0, 0, 0, 0.08) 1px,
                        rgba(0, 0, 0, 0.08) 2px
                    );
                    pointer-events: none;
                    z-index: 9999;
                }
                .terminal {
                    position: relative;
                    z-index: 1;
                    padding: 48px 64px;
                    background: var(--bg-panel);
                    border: 1px solid var(--border-active);
                    box-shadow: var(--glow-md), inset 0 0 40px rgba(0, 0, 0, 0.4);
                }
                .terminal::before {
                    content: '';
                    position: absolute;
                    inset: 4px;
                    border: 1px solid rgba(176, 96, 96, 0.25);
                    pointer-events: none;
                }
                .header {
                    color: var(--text-dead);
                    font-size: 10px;
                    letter-spacing: 3px;
                    text-transform: uppercase;
                    margin-bottom: 24px;
                }
                h1 {
                    color: var(--text-title);
                    font-size: 80px;
                    letter-spacing: 16px;
                    text-shadow: 0 0 20px rgba(240, 96, 96, 0.6);
                    margin-bottom: 24px;
                }
                .seal {
                    color: var(--gold-bright);
                    font-size: 28px;
                    letter-spacing: 4px;
                    margin-bottom: 24px;
                }
                .message {
                    color: var(--text-bright);
                    font-size: 13px;
                    letter-spacing: 2px;
                    line-height: 1.8;
                    text-transform: uppercase;
                    margin-bottom: 16px;
                }
                .message span {
                    color: var(--text-muted);
                    font-size: 11px;
                    letter-spacing: 1.5px;
                }
                .ip {
                    color: var(--gold-bright);
                    font-size: 14px;
                    letter-spacing: 1.5px;
                    margin-bottom: 32px;
                }
                .hint a {
                    color: var(--text-bright);
                    text-decoration: underline;
                }
            </style>
        </head>
        <body>
            <div class="terminal">
                <div class="header">▓▓ ARIA // COGITATOR TERMINAL MK.IV ▓▓</div>
                <div class="seal">☠ ☠ ☠</div>
                <h1>403</h1>
                <div class="message">
                    // Access Denied //<br />
                    <span>Unauthorized off-world cogitator signature detected.</span><br />
                    <span>The Omnissiah recognizes only sanctioned machine spirits.</span><br />
                    <span>Return to your assigned terminus, citizen.</span>
                </div>
                <div class="ip">Detected IP: {{ipLine}}</div>
                <div class="hint message"><span>If you have an invite code, go to <a href="/access/pathoftheworthy">/access/pathoftheworthy</a>.</span></div>
            </div>
        </body>
        </html>
        """;
    }

    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly UiAccessKnockService _knockService;
    private readonly TrustedDeviceService _deviceService;
    private readonly IDataProtector _trustedProtector;
    private readonly ILogger<AccessGateMiddleware> _logger;

    public AccessGateMiddleware(
        RequestDelegate next,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        UiAccessKnockService knockService,
        TrustedDeviceService deviceService,
        IDataProtectionProvider dataProtection,
        ILogger<AccessGateMiddleware> logger)
    {
        _next = next;
        _environment = environment;
        _configuration = configuration;
        _knockService = knockService;
        _deviceService = deviceService;
        _trustedProtector = dataProtection.CreateProtector("Aria.TrustedBrowser");
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestPath = context.Request.Path;

        // Always-public surface: health checks and bridge endpoints.
        if (string.Equals(requestPath, "/health", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestPath, "/access/pathoftheworthy", StringComparison.OrdinalIgnoreCase) ||
            requestPath.StartsWithSegments("/api/bridge", StringComparison.OrdinalIgnoreCase) ||
            requestPath.StartsWithSegments("/api/modelbridge", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var remoteIp = ClientIpResolver.GetClientIp(context);
        var remoteIpString = remoteIp?.ToString();

        // Local development and loopback traffic are always allowed.
        if (_environment.IsDevelopment() || (remoteIp is not null && IPAddress.IsLoopback(remoteIp)))
        {
            await _next(context);
            return;
        }

        // Every browser reaching the gate gets a device id (minted here if absent) so it can later be
        // approved at a node. The id alone grants nothing until a node signs a grant for it.
        var deviceId = _deviceService.GetOrIssueDeviceId(context);

        // 1. An authenticated bridge has recently knocked from this IP.
        if (!string.IsNullOrEmpty(remoteIpString) && await _knockService.IsAllowedAsync(remoteIpString))
        {
            await _next(context);
            return;
        }

        // 2. Static allow-list from configuration.
        if (IsInAllowedIps(remoteIpString))
        {
            await _next(context);
            return;
        }

        // 3. Valid aria-worthy admin invite-code cookie.
        if (TryValidateWorthyCookie(context, out _))
        {
            await _next(context);
            return;
        }

        // 4. Valid aria-trusted persistent cookie set after bridge control was proven.
        if (TryValidateTrustedCookie(context))
        {
            await _next(context);
            return;
        }

        // 5. A node-approved device (Layer A): the device-id cookie carries a still-valid, node-signed
        //    trust-device grant. Survives IP changes; an unapproved/fresh device does not pass here.
        if (await _deviceService.IsDeviceTrustedAsync(deviceId))
        {
            await _next(context);
            return;
        }

        _logger.LogWarning(
            "Forbidden request from {RemoteIp} to {Path}.",
            remoteIpString ?? "(unknown)",
            requestPath);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(ForbiddenPageHtml(remoteIpString ?? "unknown"));
    }

    private bool IsInAllowedIps(string? remoteIpString)
    {
        if (string.IsNullOrEmpty(remoteIpString)) return false;

        var raw = _configuration["IpRestriction:AllowedIPs"];
        string[] configuredIPs;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            configuredIPs = raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ip => ip.Trim())
                .Where(ip => !string.IsNullOrEmpty(ip))
                .ToArray();
        }
        else
        {
            configuredIPs = _configuration.GetSection("IpRestriction:AllowedIPs").Get<string[]>() ?? Array.Empty<string>();
        }

        return configuredIPs.Contains(remoteIpString, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryValidateWorthyCookie(HttpContext context, out DateTime? expiry)
    {
        expiry = null;
        var cookie = context.Request.Cookies["aria-worthy"];
        if (string.IsNullOrWhiteSpace(cookie)) return false;

        var validCodes = ParseGuestCodes();
        if (validCodes.Count == 0) return false;

        var entries = cookie.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries)
        {
            var (code, when) = ParseGuestCodeEntry(entry);
            if (code == null) continue;
            if (!validCodes.Any(v => string.Equals(v.Code, code, StringComparison.OrdinalIgnoreCase) && v.Expiry > DateTime.UtcNow)) continue;
            if (when <= DateTime.UtcNow) continue;

            expiry = when;
            return true;
        }

        return false;
    }

    private List<(string Code, DateTime Expiry)> ParseGuestCodes()
    {
        var raw = _configuration["GuestAccess:Codes"];
        string[] entries;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            entries = raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim())
                .Where(c => !string.IsNullOrEmpty(c))
                .ToArray();
        }
        else
        {
            entries = _configuration.GetSection("GuestAccess:Codes").Get<string[]>() ?? Array.Empty<string>();
        }

        var result = new List<(string, DateTime)>();
        foreach (var entry in entries)
        {
            var (code, expiry) = ParseGuestCodeEntry(entry);
            if (code != null)
                result.Add((code, expiry));
        }
        return result;
    }

    private static (string? Code, DateTime Expiry) ParseGuestCodeEntry(string entry)
    {
        var parts = entry.Split(':', 2);
        if (parts.Length != 2) return (null, DateTime.MinValue);

        var code = parts[0].Trim();
        if (string.IsNullOrEmpty(code)) return (null, DateTime.MinValue);

        if (!DateTime.TryParse(parts[1].Trim(), null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var when))
            return (null, DateTime.MinValue);

        return (code, when);
    }

    private bool TryValidateTrustedCookie(HttpContext context)
    {
        var cookie = context.Request.Cookies["aria-trusted"];
        if (string.IsNullOrWhiteSpace(cookie)) return false;

        try
        {
            var payload = _trustedProtector.Unprotect(Convert.FromBase64String(cookie));
            var text = Encoding.UTF8.GetString(payload);
            var parts = text.Split('|', 2);
            if (parts.Length != 2 || !DateTime.TryParse(parts[1], null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var when))
                return false;

            return when > DateTime.UtcNow;
        }
        catch
        {
            return false;
        }
    }
}

public static class AccessGateMiddlewareExtensions
{
    public static IApplicationBuilder UseAccessGate(this IApplicationBuilder app)
        => app.UseMiddleware<AccessGateMiddleware>();
}
