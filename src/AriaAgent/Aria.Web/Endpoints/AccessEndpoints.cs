using System.Text;
using Aria.Web.DependencyInjection;
using Aria.Web.Middleware;
using Microsoft.AspNetCore.DataProtection;

namespace Aria.Web.Endpoints;

public static class AccessEndpoints
{
    private const string WorthyCookieName = "aria-worthy";
    private const string TrustedCookieName = "aria-trusted";
    private const string TrustedProtectorPurpose = "Aria.TrustedBrowser";

    public static WebApplication MapAccessEndpoints(this WebApplication app)
    {
        app.MapGet("/access/pathoftheworthy", (HttpContext context, IConfiguration configuration,
            IDataProtectionProvider dataProtection) =>
        {
            if (HasValidWorthyCookie(context, configuration) || HasValidTrustedCookie(context, dataProtection))
                return Results.Redirect("/");

            return Results.Content(WorthyPageHtml(null), "text/html; charset=utf-8");
        });

        app.MapPost("/access/pathoftheworthy", async (HttpContext context, IConfiguration configuration) =>
        {
            var code = context.Request.Form["code"].FirstOrDefault()?.Trim() ?? "";
            var matched = FindGuestCode(configuration, code);

            if (matched == null || matched.Value.Expiry <= DateTime.UtcNow)
            {
                return Results.Content(WorthyPageHtml("Invalid or expired code."), "text/html; charset=utf-8");
            }

            var cookieValue = $"{matched.Value.Code}:{matched.Value.Expiry:O}";
            var options = new CookieOptions
            {
                HttpOnly = true,
                Secure = !app.Environment.IsDevelopment(),
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = matched.Value.Expiry,
            };
            context.Response.Cookies.Append(WorthyCookieName, cookieValue, options);
            return Results.Redirect("/");
        }).RequireRateLimiting(ServiceCollectionExtensions.GuestCodePolicy);

        return app;
    }

    private static (string Code, DateTime Expiry)? FindGuestCode(IConfiguration configuration, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var raw = configuration["GuestAccess:Codes"];
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
            entries = configuration.GetSection("GuestAccess:Codes").Get<string[]>() ?? Array.Empty<string>();
        }

        foreach (var entry in entries)
        {
            var parts = entry.Split(':', 2);
            if (parts.Length != 2) continue;
            var entryCode = parts[0].Trim();
            if (!string.Equals(entryCode, code, StringComparison.OrdinalIgnoreCase)) continue;
            if (!DateTime.TryParse(parts[1].Trim(), null,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var expiry))
                continue;
            return (entryCode, expiry);
        }

        return null;
    }

    private static bool HasValidWorthyCookie(HttpContext context, IConfiguration configuration)
    {
        var cookie = context.Request.Cookies[WorthyCookieName];
        if (string.IsNullOrWhiteSpace(cookie)) return false;

        var parts = cookie.Split(':', 2);
        if (parts.Length != 2) return false;

        var valid = FindGuestCode(configuration, parts[0].Trim());
        if (valid == null) return false;

        if (!DateTime.TryParse(parts[1].Trim(), null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var cookieExpiry))
            return false;

        return cookieExpiry > DateTime.UtcNow && valid.Value.Expiry > DateTime.UtcNow;
    }

    private static bool HasValidTrustedCookie(HttpContext context, IDataProtectionProvider dataProtection)
    {
        var cookie = context.Request.Cookies[TrustedCookieName];
        if (string.IsNullOrWhiteSpace(cookie)) return false;

        try
        {
            var protector = dataProtection.CreateProtector(TrustedProtectorPurpose);
            var payload = protector.Unprotect(Convert.FromBase64String(cookie));
            var text = Encoding.UTF8.GetString(payload);
            var parts = text.Split('|', 2);
            if (parts.Length != 2 || !DateTime.TryParse(parts[1], null,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var when))
                return false;
            return when > DateTime.UtcNow;
        }
        catch
        {
            return false;
        }
    }

    private static string WorthyPageHtml(string? error)
    {
        var errorBlock = string.IsNullOrEmpty(error)
            ? ""
            : $"""<div class=\"error\">{System.Net.WebUtility.HtmlEncode(error)}</div>""";

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <link rel="icon" type="image/png" href="favicon.png" />
            <title>ARIA // PATH OF THE WORTHY</title>
            <style>
                :root {
                    --bg-base: #252525;
                    --bg-panel: #2d2d2d;
                    --border-active: #6080b0;
                    --border-glow: #7090e0;
                    --text-dead: #829a9a;
                    --text-muted: #98cc98;
                    --text-bright: #a0f5a0;
                    --text-title: #60f060;
                    --gold-bright: #d4a020;
                    --font-mono: 'Consolas', 'Courier New', monospace;
                    --glow-md: 0 0 12px rgba(80, 120, 180, 0.45);
                }
                * { box-sizing: border-box; margin: 0; padding: 0; }
                html, body {
                    height: 100%;
                    background: var(--bg-base);
                    color: var(--text-muted);
                    font-family: var(--font-mono);
                    font-size: 13px;
                    line-height: 1.55;
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
                    border: 1px solid rgba(96, 128, 176, 0.25);
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
                    font-size: 28px;
                    letter-spacing: 4px;
                    text-shadow: 0 0 20px rgba(96, 240, 96, 0.4);
                    margin-bottom: 24px;
                }
                .message {
                    color: var(--text-bright);
                    font-size: 13px;
                    letter-spacing: 1.5px;
                    line-height: 1.8;
                    margin-bottom: 24px;
                }
                input[type=text] {
                    background: #1a1a1a;
                    border: 1px solid var(--border-active);
                    color: var(--text-bright);
                    padding: 12px 16px;
                    font-family: var(--font-mono);
                    font-size: 14px;
                    letter-spacing: 2px;
                    width: 280px;
                    text-align: center;
                    outline: none;
                }
                input[type=text]:focus {
                    border-color: var(--border-glow);
                    box-shadow: 0 0 8px rgba(112, 144, 224, 0.35);
                }
                button {
                    background: #1a1a1a;
                    border: 1px solid var(--border-active);
                    color: var(--text-bright);
                    padding: 12px 24px;
                    font-family: var(--font-mono);
                    font-size: 12px;
                    letter-spacing: 2px;
                    text-transform: uppercase;
                    cursor: pointer;
                    margin-top: 16px;
                }
                button:hover {
                    border-color: var(--border-glow);
                    color: var(--text-title);
                }
                .error {
                    color: #f06060;
                    margin-bottom: 16px;
                    letter-spacing: 1px;
                }
            </style>
        </head>
        <body>
            <div class="terminal">
                <div class="header">▓▓ ARIA // COGITATOR TERMINAL MK.IV ▓▓</div>
                <h1>PATH OF THE WORTHY</h1>
                <div class="message">
                    Present your admin invite-code, aspirant.<br />
                    The gate opens only to those who bear the seal.
                </div>
                {{errorBlock}}
                <form method="post" action="/access/pathoftheworthy">
                    <input type="text" name="code" placeholder="INVITE-CODE" autocomplete="off" autofocus />
                    <br />
                    <button type="submit">Enter</button>
                </form>
            </div>
        </body>
        </html>
        """;
    }
}
