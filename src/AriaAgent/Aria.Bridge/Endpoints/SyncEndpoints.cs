using Aria.Bridge.Data;
using Aria.Shared;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// Receives server-authoritative config snapshots from Aria.Web and mirrors them in the local bridge DB.
/// The console reads these mirrored tables so the user never has to re-declare agents/tools/sources.
/// </summary>
public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this WebApplication app)
    {
        // POST /sync/apply — replace the synced config for one soul.
        app.MapPost("/sync/apply", async (SyncSnapshot req, BridgeDbContext db) =>
        {
            await ApplySnapshotAsync(db, req);
            return Results.Ok(new { ok = true, appliedAt = DateTime.UtcNow });
        });

        // GET /sync/status — diagnostic: counts of mirrored rows.
        app.MapGet("/sync/status", async (BridgeDbContext db) =>
        {
            return Results.Ok(new
            {
                agents      = await db.SyncedSubAgents.CountAsync(),
                toolStates  = await db.SyncedSubAgentToolStates.CountAsync(),
                toolConfigs = await db.SyncedToolConfigs.CountAsync(),
                sources     = await db.SyncedLocalSources.CountAsync(),
                mcps        = await db.SyncedMcpServers.CountAsync(),
            });
        });
    }

    private static async Task ApplySnapshotAsync(BridgeDbContext db, SyncSnapshot snapshot)
    {
        // Wipe and rewrite — server is authoritative.
        await db.Database.ExecuteSqlRawAsync("""
            DELETE FROM SyncedCogitationFolders;
            DELETE FROM SyncedSubAgentToolStates;
            DELETE FROM SyncedSubAgents;
            DELETE FROM SyncedToolConfigs;
            DELETE FROM SyncedLocalSources;
            DELETE FROM SyncedMcpServers;
        """);

        foreach (var folder in snapshot.Folders)
        {
            db.SyncedCogitationFolders.Add(new SyncedCogitationFolder
            {
                Id                 = folder.Id,
                Name               = folder.Name,
                Color              = folder.Color,
                SortOrder          = folder.SortOrder,
                DefaultSubAgentId  = folder.DefaultSubAgentId,
                DefaultProjectPath = folder.DefaultProjectPath,
                StandingDirective  = folder.StandingDirective,
            });
        }

        foreach (var agent in snapshot.Agents)
        {
            db.SyncedSubAgents.Add(new SyncedSubAgent
            {
                Id                   = agent.Id,
                GeneratedName        = agent.GeneratedName,
                ArchetypeName        = agent.ArchetypeName,
                GeneratedPersonality = agent.GeneratedPersonality,
                UserDirectives       = agent.UserDirectives,
                AccentColor          = agent.AccentColor,
                ModelSourceName      = agent.ModelSourceName,
                ModelId              = agent.ModelId,
                EnabledMcpNamesJson  = agent.EnabledMcpNamesJson,
                AvatarSpriteKey      = agent.AvatarSpriteKey,
                Nickname             = agent.Nickname,
                CreatedAt            = agent.CreatedAt,
                ToolStates           = agent.ToolStates.Select(ts => new SyncedSubAgentToolState
                {
                    ToolId  = ts.ToolId,
                    Enabled = ts.Enabled,
                }).ToList()
            });
        }

        foreach (var cfg in snapshot.ToolConfigs)
        {
            db.SyncedToolConfigs.Add(new SyncedToolConfig
            {
                ToolId     = cfg.ToolId,
                Enabled    = cfg.Enabled,
                ConfigJson = cfg.ConfigJson,
            });
        }

        foreach (var src in snapshot.LocalSources)
        {
            db.SyncedLocalSources.Add(new SyncedLocalSource
            {
                Id           = src.Id,
                Name         = src.Name,
                Url          = src.Url,
                ModelsJson   = src.ModelsJson,
                IsBridged    = src.IsBridged,
                SortOrder    = src.SortOrder,
                BridgeNodeId = src.BridgeNodeId,
            });
        }

        foreach (var srv in snapshot.McpServers)
        {
            db.SyncedMcpServers.Add(new SyncedMcpServer
            {
                Id        = srv.Id,
                Name      = srv.Name,
                Transport = (int)srv.Transport,
                Command   = srv.Command,
                ArgsJson  = srv.ArgsJson,
                EnvJson   = srv.EnvJson,
                Url       = srv.Url,
                Enabled   = srv.Enabled,
            });
        }

        await db.SaveChangesAsync();

        // API keys are authoritative on the BRIDGE, never the server (see LlmKeyEndpoints).
        // They are intentionally excluded from the sync snapshot so a server compromise cannot
        // overwrite or exfiltrate local keys. Cross-node key distribution is handled separately
        // by the encrypted KeyReplicationService mesh (/keys/sync-export + /keys/sync-import).
    }
}


