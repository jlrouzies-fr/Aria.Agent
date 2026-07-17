using System.Text.Json;
using Aria.Shared;
using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.ModelBridge;

/// <summary>
/// Pushes server-side config snapshots (agents, tools, sources, MCP servers) to the local bridge
/// so Aria.Console can read them without re-declaring anything.
/// </summary>
public sealed class BridgeSyncService(
    ModelBridgeRegistry registry,
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<BridgeSyncService> log)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Fires a full snapshot to every connected bridge for the soul.</summary>
    public async Task PushSnapshotAsync(string userId)
    {
        if (!registry.HasBridge(userId))
        {
            log.LogDebug("No bridge connected for {UserId}; skipping sync push", userId);
            return;
        }

        var snapshot = await BuildSnapshotAsync(userId);
        var body = JsonSerializer.Serialize(snapshot, Json);

        // Push to EVERY connected node — sending only to the default node left the soul's other
        // machines with stale local copies until their next reconnect.
        foreach (var node in registry.GetNodes(userId))
        {
            try
            {
                var resp = await registry.SendLocalRestAsync(userId, "POST", "/sync/apply", body,
                    node.NodeId, timeoutSeconds: 30);
                if (resp?.StatusCode != 200)
                    log.LogWarning("Sync push to node {Node} returned {Status} for {UserId}",
                        node.NodeId, resp?.StatusCode, userId);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Sync push to node {Node} failed for {UserId}", node.NodeId, userId);
            }
        }
    }

    private async Task<SyncSnapshot> BuildSnapshotAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var agents = await db.SubAgents
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .Include(a => a.ToolStates)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();

        var agentDtos = agents.Select(a => new SyncedSubAgentDto(
            a.Id,
            a.GeneratedName,
            a.ArchetypeName,
            a.GeneratedPersonality,
            a.UserDirectives,
            a.AccentColor,
            a.ModelSourceName,
            a.ModelId,
            a.EnabledMcpNamesJson,
            a.AvatarSpriteKey,
            a.Nickname,
            a.CreatedAt,
            a.ToolStates.Select(ts => new SyncedSubAgentToolStateDto(ts.Id, ts.SubAgentId, ts.ToolId, ts.Enabled)).ToList()
        )).ToList();

        var toolConfigs = await db.UserToolConfigs
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.ToolId)
            .Select(c => new SyncedToolConfigDto(c.Id, c.ToolId, c.Enabled, c.ConfigJson))
            .ToListAsync();

        // Channels and MCP servers are node-authoritative and authored ONLY on the bridge. The server
        // no longer pushes them down, so the snapshot carries empty lists.
        var localSources = new List<SyncedLocalSourceDto>();
        var mcpServers   = new List<SyncedMcpServerDto>();

        var folders = await db.CogitationFolders
            .AsNoTracking()
            .Where(f => f.UserId == userId)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Name)
            .Select(f => new SyncedCogitationFolderDto(
                f.Id, f.Name, f.Color, f.SortOrder,
                f.DefaultSubAgentId, f.DefaultProjectPath, f.StandingDirective))
            .ToListAsync();

        return new SyncSnapshot(userId, agentDtos, toolConfigs, localSources, mcpServers, folders);
    }
}
