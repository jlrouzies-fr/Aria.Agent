using Aria.Bridge.Data;
using Aria.Bridge.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge;

/// <summary>
/// Resolves the node-authoritative Terminal policy shared by the exec, project-file, git, and
/// built-in tools (<c>/tools/call</c>) endpoints. The node's declared Allowed Paths (Terminal ›
/// Allowed Projects) are the maximum scope; a server-supplied request may only <em>narrow</em>
/// them, never widen. An empty node list blocks every path (fail closed) so a compromised server
/// cannot read or write outside the directories the node explicitly declared — even by sending
/// its own <c>AllowedPaths</c>.
///
/// The one sanctioned widening is the node's OWN doing: a session path grant (Wave 5), minted and
/// signed by this node (or a trusted sibling) after a human approved it, unions into the base set
/// for requests carrying that session id. The request-may-only-narrow rule is untouched.
///
/// See <c>docs/security/hardening-plan.md</c> F-4 / F-10.
/// </summary>
public static class NodeTerminalPolicy
{
    public static async Task<SecurityPolicy> ResolveAsync(
        BridgeDbContext db, string[]? requestAllowedPaths, string? sessionId = null)
    {
        var soul = await db.Souls.AsNoTracking().FirstOrDefaultAsync(x => x.Name != "")
                   ?? await db.Souls.AsNoTracking().FirstOrDefaultAsync();
        // When the Projects capability is off, the agent's file/git tools get an empty path set,
        // which fails closed (blocks every path) regardless of any server-supplied AllowedPaths.
        var nodePaths = soul is { ProjectsEnabled: true } ? soul.GetTerminalAllowedPaths() : [];

        // Wave 5: union this session's live, node-signed path grants into the node's base set. They
        // widen scope ONLY because the node itself issued them (signature re-verified on every use);
        // a capability-off node unions nothing. The request narrowing below still applies on top.
        if (soul is { ProjectsEnabled: true } && !string.IsNullOrEmpty(sessionId))
        {
            var granted = await ContextGrantStore.GetLiveSessionPathGrantsAsync(db, soul, sessionId);
            if (granted.Count > 0)
                nodePaths = nodePaths.Concat(granted.Select(g => g.Path)).ToArray();
        }
        return SecurityPolicy.FromNodeAndRequest(nodePaths, requestAllowedPaths);
    }

    /// <summary>
    /// The enforcement seam for the built-in tools path (<c>/tools/call</c>): resolves the SAME
    /// node-authoritative policy as <see cref="ResolveAsync"/> — node declared paths (empty when
    /// the Projects capability is off, which fails closed) ∪ this session's node-signed path
    /// grants, then narrowed by the server-supplied request paths (e.g. active-project focus).
    /// The server's BlockedCommands pass through so <c>bash_exec</c> keeps enforcing them; its
    /// AllowedPaths only ever narrow the node set, never widen it.
    /// </summary>
    public static async Task<SecurityPolicy> ResolveBuiltinPolicyAsync(
        BridgeDbContext db, SecurityPolicy? requestPolicy, string? sessionId = null)
    {
        var policy = await ResolveAsync(db, requestPolicy?.AllowedPaths, sessionId);
        return requestPolicy?.BlockedCommands is { Length: > 0 } blocked
            ? policy with { BlockedCommands = blocked }
            : policy;
    }
}
