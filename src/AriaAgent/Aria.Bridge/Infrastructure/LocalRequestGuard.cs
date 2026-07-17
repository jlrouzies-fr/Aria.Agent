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
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost:5741",
        "127.0.0.1:5741",
    };

    private static readonly HashSet<string> AllowedOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://localhost:5741",
        "http://127.0.0.1:5741",
    };

    /// <summary>
    /// Returns true if the request appears to originate from the bridge's own loopback UI:
    /// the Host header names localhost:5741/127.0.0.1:5741 and the Origin header is absent or
    /// names the same local origin.
    /// </summary>
    public static bool IsLocalOrigin(HttpRequest request)
    {
        var host = request.Headers.Host.ToString();
        if (string.IsNullOrWhiteSpace(host) || !AllowedHosts.Contains(host.Trim()))
            return false;

        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
            return true; // same-origin / no-origin requests (e.g. curl without -H Origin)

        return AllowedOrigins.Contains(origin.Trim());
    }
}
