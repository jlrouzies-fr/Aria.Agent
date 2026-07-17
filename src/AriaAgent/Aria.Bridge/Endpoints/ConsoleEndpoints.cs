using Aria.Bridge.Data;
using Aria.Shared;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// Local-only REST surface used by Aria.Console. Reads from the server-authoritative mirror tables
/// so the console never has to re-declare agents, tools, sources, or MCP servers.
/// </summary>
public static class ConsoleEndpoints
{
    public static void MapConsoleEndpoints(this WebApplication app)
    {
        // GET /console/profile — soul identity + sync health.
        app.MapGet("/console/profile", async (BridgeDbContext db) =>
        {
            var soul = await db.Souls.AsNoTracking()
                .FirstOrDefaultAsync(s => s.ServerSoulId != null || s.Name != "")
                ?? await db.Souls.AsNoTracking().FirstOrDefaultAsync();

            if (soul is null)
                return Results.NotFound(new { error = "No soul configured on this bridge" });

            return Results.Ok(new
            {
                soul.Id,
                soul.Name,
                soul.AvatarSpriteKey,
                soul.AccentColor,
                soul.ServerSoulId,
                soul.ServerUrl,
                NodeLabel = soul.NodeLabel ?? Environment.MachineName,
                HasKeypair = soul.PublicKeyBase64 != null,
                Synced = new
                {
                    Agents      = await db.SyncedSubAgents.CountAsync(),
                    ToolStates  = await db.SyncedSubAgentToolStates.CountAsync(),
                    ToolConfigs = await db.SyncedToolConfigs.CountAsync(),
                    Sources     = await db.SyncedLocalSources.CountAsync(),
                    Mcps        = await db.SyncedMcpServers.CountAsync(),
                    Folders     = await db.SyncedCogitationFolders.CountAsync(),
                }
            });
        });

        // GET /console/agents — synced sub-agents.
        app.MapGet("/console/agents", async (BridgeDbContext db) =>
        {
            var agents = await db.SyncedSubAgents
                .AsNoTracking()
                .Include(a => a.ToolStates)
                .OrderBy(a => a.CreatedAt)
                .ToListAsync();

            return Results.Ok(agents.Select(a => new SyncedSubAgentDto(
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
            )).ToList());
        });

        // GET /console/agents/{id} — single synced sub-agent.
        app.MapGet("/console/agents/{id:int}", async (int id, BridgeDbContext db) =>
        {
            var a = await db.SyncedSubAgents
                .AsNoTracking()
                .Include(a => a.ToolStates)
                .FirstOrDefaultAsync(a => a.Id == id);

            if (a is null) return Results.NotFound();

            return Results.Ok(new SyncedSubAgentDto(
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
            ));
        });

        // GET /console/tools — synced tool configs.
        app.MapGet("/console/tools", async (BridgeDbContext db) =>
        {
            var configs = await db.SyncedToolConfigs
                .AsNoTracking()
                .OrderBy(c => c.ToolId)
                .Select(c => new SyncedToolConfigDto(c.Id, c.ToolId, c.Enabled, c.ConfigJson))
                .ToListAsync();

            return Results.Ok(configs);
        });

        // GET /console/sources — node-authoritative channels (custom BridgeChannels + public providers).
        // These are authored on this node, not synced from the server.
        app.MapGet("/console/sources", async (BridgeDbContext db) =>
        {
            var custom = await db.Channels
                .AsNoTracking()
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .Select(c => new SyncedLocalSourceDto(c.Id, c.Name, c.Url, c.ModelsJson, c.IsBridged, c.SortOrder, null))
                .ToListAsync();

            var sortBase = custom.Count;
            var publics = Aria.Shared.PublicProviderCatalog.Providers
                .Select((p, i) => new SyncedLocalSourceDto(
                    100000 + i, p.Name, p.CanonicalUrl,
                    System.Text.Json.JsonSerializer.Serialize(p.DefaultModels), true, sortBase + i, null));

            return Results.Ok(custom.Concat(publics).ToList());
        });

        // GET /console/mcps — node-authoritative MCP servers (authored on this node, not synced from the server).
        app.MapGet("/console/mcps", async (BridgeDbContext db) =>
        {
            var servers = await db.McpServers
                .AsNoTracking()
                .OrderBy(s => s.SortOrder)
                .ThenBy(s => s.Name)
                .Select(s => new SyncedMcpServerDto(s.Id, s.Name, s.Transport, s.Command, s.ArgsJson, s.EnvJson, s.Url, s.Enabled))
                .ToListAsync();

            return Results.Ok(servers);
        });

        // GET /console/folders — synced cogitation folders.
        app.MapGet("/console/folders", async (BridgeDbContext db) =>
        {
            var folders = await db.SyncedCogitationFolders
                .AsNoTracking()
                .OrderBy(f => f.SortOrder)
                .ThenBy(f => f.Name)
                .Select(f => new SyncedCogitationFolderDto(
                    f.Id, f.Name, f.Color, f.SortOrder,
                    f.DefaultSubAgentId, f.DefaultProjectPath, f.StandingDirective))
                .ToListAsync();

            return Results.Ok(folders);
        });
    }
}
