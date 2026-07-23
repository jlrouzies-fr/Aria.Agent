using Aria.Harness.Tools;
using Aria.Tools;
using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.Agent;

public class SubAgentService(IDbContextFactory<AppDbContext> dbFactory, BridgeSyncService? sync = null)
{
    /// <summary>
    /// Load a single sub-agent with tool states (no skills needed).
    /// </summary>
    public async Task<SubAgent?> GetByIdAsync(int agentId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SubAgents
            .Include(a => a.ToolStates)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == agentId);
    }

    /// <summary>
    /// DB-based equivalent of UserSessionState.GetEnabledToolsForSubAgent.
    /// Joins SubAgentToolStates (enabled filter) with UserToolConfigs (credentials),
    /// injects _userId, and excludes bridge-only tools unless the run was explicitly
    /// authorised for project tools (<paramref name="allowBridgeTools"/>).
    /// </summary>
    public async Task<List<ActiveToolConfig>> GetEnabledToolConfigsAsync(
        int subAgentId, string userId, bool allowBridgeTools = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        // Load enabled tool states for this sub-agent
        var enabledToolIds = await db.SubAgentToolStates
            .Where(s => s.SubAgentId == subAgentId && s.Enabled)
            .Select(s => s.ToolId)
            .ToListAsync();

        // Load user tool credentials
        var userToolConfigs = await db.UserToolConfigs
            .Where(c => c.UserId == userId)
            .ToDictionaryAsync(c => c.ToolId, c => c.ConfigJson);

        var userIdStr = userId.ToString();
        var result = new List<ActiveToolConfig>();
        foreach (var toolId in enabledToolIds)
        {
            if (!allowBridgeTools && AgentBackgroundExecutor.NoBridgeTools.Contains(toolId))
                continue;

            var cfg = new Dictionary<string, string>();
            if (userToolConfigs.TryGetValue(toolId, out var cfgJson) && cfgJson != null)
            {
                try
                {
                    cfg = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(cfgJson) ?? [];
                }
                catch { /* ignore malformed config */ }
            }
            cfg["_userId"] = userIdStr;
            result.Add(new ActiveToolConfig(toolId, cfg));
        }
        return result;
    }

    /// <summary>Resolves a user's sub-agent by persona name — matches the generated name or the
    /// nickname (what the user actually calls it), case-insensitively. Null when no persona matches.</summary>
    public async Task<SubAgent?> FindByNameAsync(string userId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var agents = await db.SubAgents
            .Where(a => a.UserId == userId)
            .ToListAsync();
        return agents.FirstOrDefault(a =>
            string.Equals(a.GeneratedName, name, StringComparison.OrdinalIgnoreCase) ||
            (a.Nickname != null && string.Equals(a.Nickname, name, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<List<SubAgent>> GetForUserAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SubAgents
            .Include(a => a.ToolStates)
            .Include(a => a.SubAgentSkills)
            .AsSplitQuery()
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();
    }

    public async Task<SubAgent> CreateAsync(
        string userId,
        string name,
        string personality,
        string archetypeName,
        string? nickname,
        string? userDirectives,
        string accentColor,
        string? modelSourceName,
        string? modelId,
        string? enabledMcpNamesJson,
        List<string> enabledToolIds,
        string? avatarSpriteKey = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var agent = new SubAgent
        {
            UserId               = userId,
            GeneratedName        = name,
            ArchetypeName        = archetypeName,
            GeneratedPersonality = personality,
            Nickname             = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim(),
            UserDirectives       = string.IsNullOrWhiteSpace(userDirectives) ? null : userDirectives,
            AccentColor          = accentColor,
            ModelSourceName      = string.IsNullOrEmpty(modelSourceName) ? null : modelSourceName,
            ModelId              = string.IsNullOrEmpty(modelId) ? null : modelId,
            EnabledMcpNamesJson  = enabledMcpNamesJson,
            AvatarSpriteKey      = avatarSpriteKey,
        };
        db.SubAgents.Add(agent);
        await db.SaveChangesAsync();

        foreach (var toolId in enabledToolIds)
        {
            db.SubAgentToolStates.Add(new SubAgentToolState
            {
                SubAgentId = agent.Id,
                ToolId     = toolId,
                Enabled    = true,
            });
        }
        await db.SaveChangesAsync();

        var result = await db.SubAgents
            .Include(a => a.ToolStates)
            .Include(a => a.SubAgentSkills)
            .AsSplitQuery()
            .FirstAsync(a => a.Id == agent.Id);

        _ = sync?.PushSnapshotAsync(userId);
        return result;
    }

    public async Task<SubAgent> UpdateAsync(
        int agentId,
        string? nickname,
        string? userDirectives,
        string accentColor,
        string? modelSourceName,
        string? modelId,
        string? enabledMcpNamesJson,
        List<string> enabledToolIds)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var agent = await db.SubAgents.FirstAsync(a => a.Id == agentId);

        agent.Nickname            = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
        agent.UserDirectives      = string.IsNullOrWhiteSpace(userDirectives) ? null : userDirectives;
        agent.AccentColor         = accentColor;
        agent.ModelSourceName     = string.IsNullOrEmpty(modelSourceName) ? null : modelSourceName;
        agent.ModelId             = string.IsNullOrEmpty(modelId) ? null : modelId;
        agent.EnabledMcpNamesJson = enabledMcpNamesJson;

        await db.SubAgentToolStates
            .Where(s => s.SubAgentId == agentId)
            .ExecuteDeleteAsync();

        foreach (var toolId in enabledToolIds)
        {
            db.SubAgentToolStates.Add(new SubAgentToolState
            {
                SubAgentId = agentId,
                ToolId     = toolId,
                Enabled    = true,
            });
        }
        await db.SaveChangesAsync();

        var result = await db.SubAgents
            .Include(a => a.ToolStates)
            .FirstAsync(a => a.Id == agentId);

        _ = sync?.PushSnapshotAsync(result.UserId);
        return result;
    }

    public async Task DeleteAsync(int agentId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var userId = await db.SubAgents
            .Where(a => a.Id == agentId)
            .Select(a => a.UserId)
            .FirstOrDefaultAsync();

        await db.SubAgentToolStates.Where(s => s.SubAgentId == agentId).ExecuteDeleteAsync();
        await db.SubAgents.Where(a => a.Id == agentId).ExecuteDeleteAsync();

        if (userId != null)
            _ = sync?.PushSnapshotAsync(userId);
    }
}
