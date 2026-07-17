using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aria.Bridge.Data;
using Aria.Bridge.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

public static class OAuthEndpoints
{
    private static readonly HttpClient Http = new();

    private const string MicrosoftAuthUrl  = "https://login.microsoftonline.com/{0}/oauth2/v2.0/authorize";
    private const string MicrosoftTokenUrl = "https://login.microsoftonline.com/{0}/oauth2/v2.0/token";
    private const string MicrosoftScope    = "openid email User.Read Mail.Read Calendars.Read offline_access";

    private const string GoogleAuthUrl  = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string GoogleTokenUrl = "https://oauth2.googleapis.com/token";
    private const string GoogleScope    = "openid email https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/calendar.readonly";

    public static WebApplication MapOAuthEndpoints(this WebApplication app)
    {
        var defaults = app.Services.GetRequiredService<BridgeOAuthConfig>();

        app.MapGet("/oauth/microsoft/connect",  async (string? returnTool, BridgeDbContext db) =>
            BuildConnectResponse("microsoft", await defaults.ResolveAsync(db), db, returnTool));
        app.MapGet("/oauth/microsoft/callback", async (string? code, string? state, string? error, BridgeDbContext db) =>
            await HandleCallbackAsync("microsoft", await defaults.ResolveAsync(db), db, code, state, error));

        app.MapGet("/oauth/google/connect",  async (string? returnTool, BridgeDbContext db) =>
            BuildConnectResponse("google", await defaults.ResolveAsync(db), db, returnTool));
        app.MapGet("/oauth/google/callback", async (string? code, string? state, string? error, BridgeDbContext db) =>
            await HandleCallbackAsync("google", await defaults.ResolveAsync(db), db, code, state, error));

        // OAuth app credentials (Microsoft tenant/client id/secret, Google OAuth client JSON) — read
        // the effective config (bridge-DB override merged over appsettings.json defaults) and let the
        // status page edit the override. Secrets are never returned, only whether one is configured.
        app.MapGet("/oauth-config", async (BridgeDbContext db) =>
        {
            var effective = await defaults.ResolveAsync(db);
            var rows = await db.OAuthAppConfigs.AsNoTracking().ToListAsync();
            var msOverridden = rows.Any(r => r.Provider == "microsoft");
            var gOverridden  = rows.Any(r => r.Provider == "google");
            return Results.Ok(new
            {
                microsoft = new
                {
                    tenantId  = effective.MsTenantId,
                    clientId  = effective.MsClientId,
                    hasSecret = !string.IsNullOrEmpty(effective.MsClientSecret),
                    overridden = msOverridden,
                },
                google = new
                {
                    clientId  = effective.GoogleClientId,
                    hasSecret = !string.IsNullOrEmpty(effective.GoogleClientSecret),
                    overridden = gOverridden,
                },
            });
        });

        app.MapPut("/oauth-config/microsoft", async (SaveMsOAuthConfigRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.ClientId))
                return Results.BadRequest("clientId required");

            var row = await db.OAuthAppConfigs.FirstOrDefaultAsync(r => r.Provider == "microsoft");
            if (row is null)
            {
                row = new BridgeOAuthAppConfig { Provider = "microsoft" };
                db.OAuthAppConfigs.Add(row);
            }
            row.TenantId = string.IsNullOrWhiteSpace(req.TenantId) ? "consumers" : req.TenantId.Trim();
            row.ClientId = req.ClientId.Trim();
            if (!string.IsNullOrWhiteSpace(req.ClientSecret))
                row.ClientSecret = req.ClientSecret.Trim();

            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        app.MapPut("/oauth-config/google", async (SaveGoogleOAuthConfigRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.CredentialsJson))
                return Results.BadRequest("credentialsJson required");
            if (BridgeOAuthConfig.ParseGoogleCredentialsJson(req.CredentialsJson) is null)
                return Results.BadRequest("Could not find client_id/client_secret under \"installed\" or \"web\" in that JSON — paste the file downloaded from Google Cloud Console as-is.");

            var row = await db.OAuthAppConfigs.FirstOrDefaultAsync(r => r.Provider == "google");
            if (row is null)
            {
                row = new BridgeOAuthAppConfig { Provider = "google" };
                db.OAuthAppConfigs.Add(row);
            }
            row.CredentialsJson = req.CredentialsJson;

            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        app.MapDelete("/oauth-config/{provider}", async (string provider, BridgeDbContext db) =>
        {
            await db.OAuthAppConfigs.Where(r => r.Provider == provider).ExecuteDeleteAsync();
            return Results.Ok(new { ok = true });
        });

        // Status / token / disconnect are available even if the provider is not configured,
        // they will just report disconnected.
        app.MapGet("/oauth/{provider}/status", async (string provider, BridgeDbContext db) =>
        {
            var soul = await GetPrimarySoulAsync(db);
            if (soul is null) return Results.Ok(new { connected = false, email = (string?)null });

            var token = await db.OAuthTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.SoulId == soul.Id && t.Provider == provider);

            return Results.Ok(new { connected = token != null, token?.Email });
        });

        app.MapGet("/oauth/{provider}/token", async (string provider, BridgeDbContext db) =>
        {
            var soul = await GetPrimarySoulAsync(db);
            if (soul is null) return Results.NotFound(new { error = "No soul configured" });

            var token = await db.OAuthTokens
                .FirstOrDefaultAsync(t => t.SoulId == soul.Id && t.Provider == provider);

            if (token is null) return Results.NotFound(new { error = $"No {provider} token stored" });

            if (token.ExpiresAt.HasValue && token.ExpiresAt.Value <= DateTime.UtcNow.AddMinutes(1))
            {
                var cfg = await defaults.ResolveAsync(db);
                var refreshed = await RefreshTokenAsync(provider, cfg, token);
                if (refreshed is not null)
                {
                    token.AccessToken  = refreshed.Value.accessToken;
                    token.RefreshToken = refreshed.Value.refreshToken ?? token.RefreshToken;
                    token.ExpiresAt    = refreshed.Value.expiresAt;
                    token.UpdatedAt    = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }
            }

            return Results.Ok(new { token.AccessToken });
        });

        app.MapDelete("/oauth/{provider}", async (string provider, BridgeDbContext db) =>
        {
            var soul = await GetPrimarySoulAsync(db);
            if (soul is null) return Results.Ok(new { ok = true });

            await db.OAuthTokens
                .Where(t => t.SoulId == soul.Id && t.Provider == provider)
                .ExecuteDeleteAsync();

            return Results.Ok(new { ok = true });
        });

        return app;
    }

    private static IResult BuildConnectResponse(string provider, BridgeOAuthConfig cfg, BridgeDbContext db, string? returnTool)
    {
        var enabled = provider == "microsoft" ? cfg.MsEnabled : cfg.GoogleEnabled;
        if (!enabled)
            return Results.Content(ErrorPage(
                $"{provider} OAuth is not configured on this bridge. " +
                $"Set it up on the bridge status page (OAuth tab) or add Auth:{(provider == "microsoft" ? "Microsoft" : "Google")} to the bridge appsettings."),
                "text/html; charset=utf-8", statusCode: 400);

        var soul = GetPrimarySoulAsync(db).GetAwaiter().GetResult();
        if (soul is null)
            return Results.Content(ErrorPage("No soul configured on this bridge. Create a soul first."), "text/html; charset=utf-8", statusCode: 400);

        var state = EncodeState(new OAuthState(provider, soul.Id, returnTool));
        var redirectUri = RedirectUri(provider);
        string url;

        if (provider == "microsoft")
        {
            url = string.Format(MicrosoftAuthUrl, Uri.EscapeDataString(cfg.MsTenantId!)) +
                $"?client_id={Uri.EscapeDataString(cfg.MsClientId!)}" +
                "&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&response_mode=query" +
                $"&scope={Uri.EscapeDataString(MicrosoftScope)}" +
                $"&state={Uri.EscapeDataString(state)}";
        }
        else
        {
            url = GoogleAuthUrl +
                $"?client_id={Uri.EscapeDataString(cfg.GoogleClientId!)}" +
                "&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&access_type=offline" +
                "&prompt=consent" +
                $"&scope={Uri.EscapeDataString(GoogleScope)}" +
                $"&state={Uri.EscapeDataString(state)}";
        }

        return Results.Redirect(url);
    }

    private static async Task<IResult> HandleCallbackAsync(string provider, BridgeOAuthConfig cfg, BridgeDbContext db, string? code, string? state, string? error)
    {
        var enabled = provider == "microsoft" ? cfg.MsEnabled : cfg.GoogleEnabled;
        if (!enabled)
            return Results.Content(ErrorPage(
                $"{provider} OAuth is not configured on this bridge. " +
                $"Set it up on the bridge status page (OAuth tab) or add Auth:{(provider == "microsoft" ? "Microsoft" : "Google")} to the bridge appsettings."),
                "text/html; charset=utf-8", statusCode: 400);

        var returnTool = "graph_email";
        if (error is not null || string.IsNullOrEmpty(code))
            return Results.Content(ErrorPage($"Authentication failed ({provider}): {error ?? "no code"}"), "text/html; charset=utf-8", statusCode: 400);

        var decoded = DecodeState(state);
        returnTool = decoded?.ReturnTool ?? (provider == "microsoft" ? "graph_email" : "google_email");

        if (decoded?.SoulId is null)
            return Results.Content(ErrorPage("Invalid state parameter."), "text/html; charset=utf-8", statusCode: 400);

        var soul = await db.Souls.FirstOrDefaultAsync(s => s.Id == decoded.SoulId);
        if (soul is null)
            return Results.Content(ErrorPage("Soul not found."), "text/html; charset=utf-8", statusCode: 400);

        var redirectUri = RedirectUri(provider);
        Dictionary<string, string> tokenParams;
        string tokenUrl;

        if (provider == "microsoft")
        {
            tokenUrl = string.Format(MicrosoftTokenUrl, Uri.EscapeDataString(cfg.MsTenantId!));
            tokenParams = new()
            {
                ["grant_type"]    = "authorization_code",
                ["client_id"]     = cfg.MsClientId!,
                ["client_secret"] = cfg.MsClientSecret!,
                ["code"]          = code,
                ["redirect_uri"]  = redirectUri,
                ["scope"]         = MicrosoftScope,
            };
        }
        else
        {
            tokenUrl = GoogleTokenUrl;
            tokenParams = new()
            {
                ["grant_type"]    = "authorization_code",
                ["client_id"]     = cfg.GoogleClientId!,
                ["client_secret"] = cfg.GoogleClientSecret!,
                ["code"]          = code,
                ["redirect_uri"]  = redirectUri,
            };
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(tokenParams)
        };
        using var resp = await Http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return Results.Content(ErrorPage($"Token exchange failed ({provider}): {body}"), "text/html; charset=utf-8", statusCode: 400);

        var doc = JsonDocument.Parse(body);
        var accessToken  = doc.RootElement.GetProperty("access_token").GetString()!;
        var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresIn    = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : (int?)null;

        string? email = provider == "microsoft"
            ? await FetchMicrosoftEmailAsync(accessToken)
            : await FetchGoogleEmailAsync(accessToken);

        var existing = await db.OAuthTokens
            .FirstOrDefaultAsync(t => t.SoulId == soul.Id && t.Provider == provider);

        if (existing is null)
        {
            db.OAuthTokens.Add(new BridgeOAuthToken
            {
                SoulId       = soul.Id,
                Provider     = provider,
                AccessToken  = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt    = expiresIn.HasValue ? DateTime.UtcNow.AddSeconds(expiresIn.Value) : null,
                Email        = email,
            });
        }
        else
        {
            existing.AccessToken  = accessToken;
            existing.RefreshToken = refreshToken ?? existing.RefreshToken;
            existing.ExpiresAt    = expiresIn.HasValue ? DateTime.UtcNow.AddSeconds(expiresIn.Value) : existing.ExpiresAt;
            existing.Email        = email ?? existing.Email;
            existing.UpdatedAt    = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();

        return Results.Content(SuccessPage(provider, returnTool), "text/html; charset=utf-8");
    }

    private static async Task<(string accessToken, string? refreshToken, DateTime? expiresAt)?> RefreshTokenAsync(
        string provider, BridgeOAuthConfig cfg, BridgeOAuthToken token)
    {
        if (string.IsNullOrEmpty(token.RefreshToken)) return null;

        Dictionary<string, string> tokenParams;
        string tokenUrl;

        if (provider == "microsoft")
        {
            tokenUrl = string.Format(MicrosoftTokenUrl, Uri.EscapeDataString(cfg.MsTenantId!));
            tokenParams = new()
            {
                ["grant_type"]    = "refresh_token",
                ["client_id"]     = cfg.MsClientId!,
                ["client_secret"] = cfg.MsClientSecret!,
                ["refresh_token"] = token.RefreshToken,
                ["scope"]         = MicrosoftScope,
            };
        }
        else
        {
            tokenUrl = GoogleTokenUrl;
            tokenParams = new()
            {
                ["grant_type"]    = "refresh_token",
                ["client_id"]     = cfg.GoogleClientId!,
                ["client_secret"] = cfg.GoogleClientSecret!,
                ["refresh_token"] = token.RefreshToken,
            };
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(tokenParams)
        };
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;

        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        var accessToken  = doc.RootElement.GetProperty("access_token").GetString()!;
        var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : token.RefreshToken;
        var expiresIn    = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : (int?)null;

        return (accessToken, refreshToken, expiresIn.HasValue ? DateTime.UtcNow.AddSeconds(expiresIn.Value) : null);
    }

    private static async Task<string?> FetchMicrosoftEmailAsync(string accessToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("mail", out var m) ? m.GetString() : null;
        }
        catch { return null; }
    }

    private static async Task<string?> FetchGoogleEmailAsync(string accessToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v1/userinfo?alt=json");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }

    private static async Task<BridgeSoul?> GetPrimarySoulAsync(BridgeDbContext db)
    {
        return await db.Souls
            .OrderByDescending(s => s.ServerSoulId != null)
            .ThenBy(s => s.CreatedAt)
            .FirstOrDefaultAsync();
    }

    private static string RedirectUri(string provider) =>
        $"http://localhost:5741/oauth/{provider}/callback";

    private static string EncodeState(OAuthState state)
    {
        var json = JsonSerializer.Serialize(state);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static OAuthState? DecodeState(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return JsonSerializer.Deserialize<OAuthState>(json);
        }
        catch { return null; }
    }

    private static string SuccessPage(string provider, string returnTool) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>Aria — Auth</title>
        </head>
        <body style="margin:0;display:flex;align-items:center;justify-content:center;min-height:100vh;
                     font-family:monospace;background:#252525;color:#e08888">
          <div style="text-align:center;padding:2rem">
            <div style="font-size:2.5rem;color:#4caf50">●</div>
            <p style="margin-top:1rem;font-size:0.9rem">Connected to {{provider}}. Closing…</p>
          </div>
          <script>
            localStorage.setItem('aria_oauth_result', JSON.stringify({tool:'{{returnTool}}',ts:Date.now()}));
            setTimeout(()=>window.close(),400);
          </script>
        </body>
        </html>
        """;

    private static string ErrorPage(string message) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>Aria — Auth Error</title>
        </head>
        <body style="margin:0;display:flex;align-items:center;justify-content:center;min-height:100vh;
                     font-family:monospace;background:#252525;color:#e08888">
          <div style="text-align:center;padding:2rem">
            <div style="font-size:2.5rem;color:#e07070">✕</div>
            <p style="margin-top:1rem;font-size:0.9rem">{{message}}</p>
            <p style="font-size:0.75rem;color:#9a8282">You may close this window.</p>
          </div>
        </body>
        </html>
        """;

    private record OAuthState(string Provider, string SoulId, string? ReturnTool);
}

public record SaveMsOAuthConfigRequest(string? TenantId, string ClientId, string? ClientSecret);
public record SaveGoogleOAuthConfigRequest(string CredentialsJson);
