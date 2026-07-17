using System.Net;

namespace Aria.Web.Helpers;

/// <summary>
/// Resolves the original client IP from Fly.io / proxy headers, falling back to the connection's
/// remote address. Shared between the access-gate middleware and the bridge knock hub.
/// </summary>
public static class ClientIpResolver
{
    public static IPAddress? GetClientIp(HttpContext? context)
    {
        if (context == null) return null;

        // Fly-Client-IP FIRST. Fly.io sets this from the real edge connection and strips any
        // client-supplied copy, so it cannot be forged. X-Forwarded-For, by contrast, is
        // client-controllable: its leftmost entry is whatever the caller prepended, so trusting it
        // for access decisions (IP allow-list, bridge knock) lets an attacker impersonate any IP by
        // sending `X-Forwarded-For: <allowed-ip>`. On Fly this header is always present, so we
        // resolve here and never reach the spoofable XFF path in production.
        if (context.Request.Headers.TryGetValue("Fly-Client-IP", out var flyClientIp) &&
            !string.IsNullOrWhiteSpace(flyClientIp) &&
            IPAddress.TryParse(flyClientIp.ToString().Trim(), out var flyIp))
        {
            return flyIp;
        }

        // Fallback for non-Fly hosting where the real client only appears in X-Forwarded-For.
        // NOTE: this branch is only reached when Fly-Client-IP is absent. It is spoofable and MUST
        // NOT be relied on as a security boundary unless a trusted reverse proxy overwrites XFF.
        // X-Forwarded-For: <client>, <proxy1>, <proxy2>, ... — leftmost is the originating client.
        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor) &&
            !string.IsNullOrWhiteSpace(forwardedFor))
        {
            foreach (var segment in forwardedFor.ToString().Split(','))
            {
                var raw = segment.Trim();
                if (IPAddress.TryParse(raw, out var ip))
                {
                    return ip;
                }
            }
        }

        return context.Connection.RemoteIpAddress;
    }
}
