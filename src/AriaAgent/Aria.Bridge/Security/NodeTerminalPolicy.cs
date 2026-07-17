using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge;

/// <summary>
/// Resolves the node-authoritative Terminal policy shared by the exec, project-file, and git
/// endpoints. The node's declared Allowed Paths (Terminal › Allowed Projects) are the maximum
/// scope; a server-supplied request may only <em>narrow</em> them, never widen. An empty node
/// list blocks every path (fail closed) so a compromised server cannot read or write outside the
/// directories the node explicitly declared — even by sending its own <c>AllowedPaths</c>.
///
/// See <c>docs/security/hardening-plan.md</c> F-4 / F-10.
/// </summary>
public static class NodeTerminalPolicy
{
    public static async Task<SecurityPolicy> ResolveAsync(BridgeDbContext db, string[]? requestAllowedPaths)
    {
        var soul = await db.Souls.AsNoTracking().FirstOrDefaultAsync(x => x.Name != "")
                   ?? await db.Souls.AsNoTracking().FirstOrDefaultAsync();
        // When the Projects capability is off, the agent's file/git tools get an empty path set,
        // which fails closed (blocks every path) regardless of any server-supplied AllowedPaths.
        var nodePaths = soul is { ProjectsEnabled: true } ? soul.GetTerminalAllowedPaths() : [];
        return SecurityPolicy.FromNodeAndRequest(nodePaths, requestAllowedPaths);
    }
}
