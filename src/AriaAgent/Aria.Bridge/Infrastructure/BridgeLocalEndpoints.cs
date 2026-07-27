namespace Aria.Bridge.Infrastructure;

/// <summary>
/// Resolves the bridge's own local loopback base URL from <c>ASPNETCORE_URLS</c>.
/// Multi-instance debug mode needs every node to address itself (tunnel relay, approval URL,
/// local request guard) instead of the hardcoded <c>http://localhost:5741</c>.
/// </summary>
public static class BridgeLocalEndpoints
{
    private const string DefaultBaseUrl = "http://localhost:5741";

    /// <summary>
    /// The first URL listed in <c>ASPNETCORE_URLS</c>, normalized to <c>localhost</c>.
    /// Defaults to <c>http://localhost:5741</c> when the env var is absent or malformed.
    /// </summary>
    public static string BaseUrl => ResolveBaseUrl(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));

    /// <summary>
    /// The TCP port derived from <see cref="BaseUrl"/>.
    /// </summary>
    public static int Port => new Uri(BaseUrl).Port;

    /// <summary>
    /// Parses the first entry of a <c>ASPNETCORE_URLS</c>-style semicolon list.
    /// Bind-all host tokens (<c>+</c>, <c>*</c>, <c>0.0.0.0</c>) are rewritten to <c>localhost</c>.
    /// </summary>
    internal static string ResolveBaseUrl(string? urls)
    {
        var first = urls?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
            return DefaultBaseUrl;

        var raw = first!;
        if (!raw.Contains("://", StringComparison.OrdinalIgnoreCase))
            raw = "http://" + raw;

        // Bind-all host tokens are valid in ASPNETCORE_URLS but rejected by System.Uri.
        raw = raw.Replace("://+:", "://localhost:", StringComparison.OrdinalIgnoreCase)
                 .Replace("://*:", "://localhost:", StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return DefaultBaseUrl;

        var host = uri.Host;
        if (host is "+" or "*" or "0.0.0.0")
            host = "localhost";

        var scheme = uri.Scheme;
        if (!scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            scheme = "http";

        var builder = new UriBuilder(uri) { Scheme = scheme, Host = host };
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }
}
