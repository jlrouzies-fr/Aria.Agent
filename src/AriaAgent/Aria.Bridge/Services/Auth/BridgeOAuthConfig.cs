using System.Text.Json;
using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Services.Auth;

/// <summary>
/// Holds the OAuth app credentials used by the bridge to authenticate users with Microsoft/Google.
/// Values can be supplied directly via configuration/environment variables, or read from local files.
/// </summary>
public sealed class BridgeOAuthConfig
{
    public string? MsTenantId     { get; private init; }
    public string? MsClientId     { get; private init; }
    public string? MsClientSecret { get; private init; }
    public bool    MsEnabled      => !string.IsNullOrEmpty(MsClientId) && !string.IsNullOrEmpty(MsClientSecret);

    public string? GoogleClientId     { get; private init; }
    public string? GoogleClientSecret { get; private init; }
    public bool    GoogleEnabled      => !string.IsNullOrEmpty(GoogleClientId) && !string.IsNullOrEmpty(GoogleClientSecret);

    public static BridgeOAuthConfig FromConfiguration(IConfiguration config)
    {
        var ms = config.GetSection("Auth:Microsoft");
        var g  = config.GetSection("Auth:Google");

        var googleCreds = ParseGoogleCredentials(g);

        return new BridgeOAuthConfig
        {
            MsTenantId     = ms["TenantId"] ?? "consumers",
            MsClientId     = ms["ClientId"],
            MsClientSecret = ReadValueOrFile(ms, "ClientSecret", "ClientSecretFile"),
            GoogleClientId     = googleCreds?.clientId,
            GoogleClientSecret = googleCreds?.clientSecret,
        };
    }

    /// <summary>
    /// Merges node-authored overrides (bridge status page, stored in <see cref="BridgeOAuthAppConfig"/>)
    /// over these appsettings.json-derived defaults. A DB row's non-empty fields win; anything left
    /// blank falls back to the corresponding default already held on this instance.
    /// </summary>
    public async Task<BridgeOAuthConfig> ResolveAsync(BridgeDbContext db)
    {
        var rows = await db.OAuthAppConfigs.AsNoTracking().ToListAsync();
        var ms = rows.FirstOrDefault(r => r.Provider == "microsoft");
        var g  = rows.FirstOrDefault(r => r.Provider == "google");

        var googleClientId     = GoogleClientId;
        var googleClientSecret = GoogleClientSecret;
        if (!string.IsNullOrWhiteSpace(g?.CredentialsJson) && ParseGoogleCredentialsJson(g!.CredentialsJson!) is { } parsed)
        {
            googleClientId     = parsed.clientId;
            googleClientSecret = parsed.clientSecret;
        }

        return new BridgeOAuthConfig
        {
            MsTenantId         = !string.IsNullOrWhiteSpace(ms?.TenantId)     ? ms!.TenantId     : MsTenantId,
            MsClientId         = !string.IsNullOrWhiteSpace(ms?.ClientId)     ? ms!.ClientId     : MsClientId,
            MsClientSecret     = !string.IsNullOrWhiteSpace(ms?.ClientSecret) ? ms!.ClientSecret : MsClientSecret,
            GoogleClientId     = googleClientId,
            GoogleClientSecret = googleClientSecret,
        };
    }

    private static string? ReadValueOrFile(IConfigurationSection section, string valueKey, string fileKey)
    {
        var direct = section[valueKey];
        if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();
        return ReadFile(section[fileKey]);
    }

    private static string? ReadFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var resolved = path.StartsWith("~/")
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;
        return File.Exists(resolved) ? File.ReadAllText(resolved).Trim() : null;
    }

    private static (string clientId, string clientSecret)? ParseGoogleCredentials(IConfigurationSection googleSection)
    {
        var directJson = googleSection["Credentials"];
        var json = !string.IsNullOrWhiteSpace(directJson)
            ? directJson
            : ReadFile(googleSection["CredentialsFile"]);

        return json is null ? null : ParseGoogleCredentialsJson(json);
    }

    /// <summary>Extracts (client_id, client_secret) from a raw Google OAuth "Desktop app" credentials
    /// JSON blob (the file downloaded from Google Cloud Console) — shared by the appsettings.json path
    /// and the bridge-UI-stored <see cref="BridgeOAuthAppConfig.CredentialsJson"/> path.</summary>
    public static (string clientId, string clientSecret)? ParseGoogleCredentialsJson(string json)
    {
        try
        {
            var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement inner;
            if (!root.TryGetProperty("installed", out inner) && !root.TryGetProperty("web", out inner))
                return null;
            var clientId     = inner.GetProperty("client_id").GetString() ?? "";
            var clientSecret = inner.GetProperty("client_secret").GetString() ?? "";
            return string.IsNullOrEmpty(clientId) ? null : (clientId, clientSecret);
        }
        catch { return null; }
    }
}
