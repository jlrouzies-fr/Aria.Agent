using Microsoft.Graph;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.Kiota.Abstractions.Authentication;

namespace Aria.Tools;

public static partial class GraphTools
{
    private static string? _tenantId;
    private static string? _clientId;

    private static readonly string[] Scopes = ["Mail.Read", "Calendars.Read"];
    private static readonly Lazy<Task<GraphServiceClient>> _clientLazy = new(InitializeAsync);

    public static void Configure(string tenantId, string clientId)
    {
        _tenantId = tenantId;
        _clientId = clientId;
    }

    /// <summary>
    /// Override MSAL token acquisition with a custom provider (used by Aria.Web to inject DB-stored tokens).
    /// Pass null to revert to MSAL.
    /// </summary>
    public static void SetTokenOverride(Func<Task<string>>? provider) =>
        MsalTokenProvider.TokenOverride = provider;

    private static async Task<GraphServiceClient> InitializeAsync()
    {
        BaseBearerTokenAuthenticationProvider authProvider;

        // When Aria.Web injects a bridge-held token, we don't need the MSAL app configuration
        // (tenant/client id) that lives on the cogitator node, not the server.
        if (MsalTokenProvider.TokenOverride is not null)
        {
            authProvider = new BaseBearerTokenAuthenticationProvider(
                new StaticTokenProvider(MsalTokenProvider.TokenOverride));
        }
        else
        {
            if (string.IsNullOrEmpty(_tenantId) || string.IsNullOrEmpty(_clientId))
                throw new InvalidOperationException(
                    "Call GraphTools.Configure(tenantId, clientId) before using Graph tools. " +
                    "Register an app in Entra ID with Mail.Read and Calendars.Read delegated permissions " +
                    "and a public client redirect URI (http://localhost). Use 'consumers' as tenantId for personal accounts.");

            var app = PublicClientApplicationBuilder
                .Create(_clientId)
                .WithAuthority($"https://login.microsoftonline.com/{_tenantId}")
                .WithRedirectUri("http://localhost")
                .Build();

            // Persist tokens to OS keychain (macOS) / DPAPI (Windows) via MSAL.Extensions
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aria-agent");
            Directory.CreateDirectory(cacheDir);

            var cacheProps = new StorageCreationPropertiesBuilder("msal-ms-token.bin", cacheDir)
                .WithMacKeyChain("Aria Agent", "Microsoft Auth")
                .Build();
            var cacheHelper = await MsalCacheHelper.CreateAsync(cacheProps);
            cacheHelper.RegisterCache(app.UserTokenCache);

            authProvider = new BaseBearerTokenAuthenticationProvider(
                new MsalTokenProvider(app, Scopes));
        }

        return new GraphServiceClient(authProvider);
    }

    private static Task<GraphServiceClient> GetClientAsync() => _clientLazy.Value;

    public static async Task<Microsoft.Graph.Models.User?> EnsureAuthenticatedAsync()
    {
        var client = await GetClientAsync();
        return await client.Me.GetAsync();
    }
}

// Wraps MSAL silent→interactive token acquisition for the Microsoft.Graph SDK (Kiota auth).
// TokenOverride, when set, bypasses MSAL entirely — used by Aria.Web to inject DB-stored tokens.
file sealed class MsalTokenProvider(IPublicClientApplication app, string[] scopes) : IAccessTokenProvider
{
    internal static Func<Task<string>>? TokenOverride;

    public AllowedHostsValidator AllowedHostsValidator { get; } = new();

    public async Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        if (TokenOverride is not null)
            return await TokenOverride();

        var accounts = await app.GetAccountsAsync();
        try
        {
            var result = await app.AcquireTokenSilent(scopes, accounts.FirstOrDefault())
                .ExecuteAsync(cancellationToken);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            var result = await app.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync(cancellationToken);
            return result.AccessToken;
        }
    }
}

// Simple token-only provider used when Aria.Web injects a bridge-held OAuth token.
// Avoids requiring MSAL app configuration (tenant/client id) on the server.
file sealed class StaticTokenProvider(Func<Task<string>> tokenFactory) : IAccessTokenProvider
{
    public AllowedHostsValidator AllowedHostsValidator { get; } = new();

    public Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
        => tokenFactory();
}
