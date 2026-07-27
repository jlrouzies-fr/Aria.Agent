namespace Aria.Shared;

/// <summary>
/// Explicit allowlist for paths the server may relay to the bridge through the direct tunnel's
/// local-REST proxy (<see cref="Aria.Bridge.DirectTunnel.HandleLocalRestAsync"/>).
///
/// The bridge sits on the user's machine with local authority (keys, files, shell). A compromised
/// hosted server must not be able to drive arbitrary loopback endpoints. This list enumerates the
/// paths the web app legitimately relays; everything else is refused at the tunnel boundary.
///
/// See <c>docs/security/hardening-plan.md</c> F-2.
/// </summary>
public static class TunnelAllowlist
{
    // Fixed paths the tunnel is allowed to reach.
    private static readonly HashSet<string> ExactPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "/metrics",
        // Read-only static hardware inventory (CPU/RAM/GPU/form factor) for the server's fleet view.
        "/hardware",
        "/llm/proxy",
        "/llm/probe",
        // Read-only model enumeration against the channel's own endpoint (same key-custody pinning as
        // /llm/probe) — lets the server refresh a channel's model list for display without the node
        // having to re-save the channel first.
        "/llm/discover-models",
        "/sync/apply",
        "/keys",
        // Read-only channel mirror for the web picker. Channel *writes* (PUT/DELETE /channels/{name})
        // are intentionally NOT allowlisted, so the server can never author or edit a channel.
        "/channels",
        // Read-only MCP server mirror. Config writes stay local-origin only.
        "/mcps",
        "/context/grants/export",
        "/context/grants/import",
        // Read-only Layer B status (enforcement flag + effective grant expiry) — drives the header seal
        // countdown. No secret and no state change; the human still approves grants locally.
        "/context/status",
        // Wave 5 session path expansions: read-only status/list, plus a narrowing revoke. A grant
        // itself is only ever minted by the node's local approval ceremony — never through the tunnel.
        "/scope/status",
        "/scope/list",
        "/scope/revoke",
        "/seal/request",
        "/seal/poll",
        "/node/session-code",
        "/node/sign-enrollment",
        "/node/sign-revocation",
        "/contacts",
        "/cogitations/init",
        "/memory/inscribe",
        "/debug/llm-log",
    };

    // Path prefixes that cover parameterised endpoints. Matching is case-insensitive and ignores
    // query strings. Keep this list conservative: if a new endpoint is added it must be reviewed
    // before being reachable over the tunnel.
    private static readonly string[] Prefixes =
    [
        "/oauth/",
        "/cogitations/",
        "/contacts/",
        "/memory/",
        "/hive/",
        "/project-files/",
        "/project-git/",
        // NOTE: "/keys/" (PUT/DELETE a provider key) is deliberately NOT tunnel-reachable — keys are
        // authored only on the bridge (local origin). Only the exact "/keys" name-list read is allowed.
        "/terminal/",
        "/context/approve/",
        // Local-Whisper control plane: status + model download/delete. Covers "/transcribe/local/status",
        // "/transcribe/local/download", "/transcribe/local/model". Deliberately does NOT match the bare
        // "/transcribe/local" audio endpoint — voice audio stays browser→bridge direct, off the server.
        "/transcribe/local/",
    ];

    /// <summary>
    /// Returns true if <paramref name="path"/> may be forwarded through the direct tunnel.
    /// Normalises the path by stripping the query string, trimming a trailing slash, and lowercasing.
    /// </summary>
    public static bool IsAllowed(string? path)
    {
        var clean = Normalize(path);
        if (clean is null) return false;

        if (ExactPaths.Contains(clean)) return true;

        foreach (var prefix in Prefixes)
            if (clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var withoutQuery = path;
        var q = path.IndexOf('?');
        if (q >= 0) withoutQuery = path[..q];

        var trimmed = withoutQuery.Trim().TrimEnd('/');
        if (trimmed.Length == 0) trimmed = "/";

        return trimmed;
    }
}
