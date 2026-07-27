namespace Aria.Bridge.Infrastructure;

/// <summary>
/// Defends sensitive bridge endpoints against DNS-rebinding and simple cross-origin POSTs. The bridge
/// only listens on loopback, but a malicious page the user visits can still send requests to
/// localhost. This guard verifies the request claims the loopback origin and host.
///
/// See <c>docs/security/hardening-plan.md</c> F-1 / F-3.
/// </summary>
public static class LocalRequestGuard
{
    /// <summary>
    /// Returns true if the request appears to originate from the bridge's own loopback UI.
    /// The allowed host/origin are derived from <see cref="BridgeLocalEndpoints"/> so a node
    /// running on a non-default port still recognizes its own UI.
    /// </summary>
    public static bool IsLocalOrigin(HttpRequest request)
        => IsLocalOrigin(request, BridgeLocalEndpoints.BaseUrl, BridgeLocalEndpoints.Port);

    /// <summary>
    /// Testable overload: validate against an explicit base URL and port instead of reading the
    /// environment. The base URL is used only to build the allowed origin set; the port is used
    /// for the allowed host set.
    /// </summary>
    internal static bool IsLocalOrigin(HttpRequest request, string baseUrl, int port)
    {
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"localhost:{port}",
            $"127.0.0.1:{port}",
        };

        var allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"{baseUrl.TrimEnd('/')}",
            $"{baseUrl.TrimEnd('/').Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase)}",
        };

        var host = request.Headers.Host.ToString();
        if (string.IsNullOrWhiteSpace(host) || !allowedHosts.Contains(host.Trim()))
            return false;

        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
            return true; // same-origin / no-origin requests (e.g. curl without -H Origin)

        return allowedOrigins.Contains(origin.Trim());
    }
}
