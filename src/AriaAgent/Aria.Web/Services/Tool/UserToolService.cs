using System.Text.Json;
using Aria.Harness.Core;
using Aria.Harness.Governance;
using Aria.Web.Data;
using Aria.Web.Services.Memory;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.Tool;

public class UserToolService(IDbContextFactory<AppDbContext> dbFactory, BridgeSyncService? sync = null)
{
    /// <summary>Raised after a user's tool config (or governance mode) is saved, so every open
    /// circuit — including other devices — refreshes without a page reload.</summary>
    public static event Action<string>? ToolsChanged;

    public async Task<Dictionary<string, (bool Enabled, Dictionary<string, string> Config)>>
        GetToolStatesAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var records = await db.UserToolConfigs
            .Where(c => c.UserId == userId)
            .ToListAsync();

        var result = new Dictionary<string, (bool, Dictionary<string, string>)>();

        foreach (var def in ToolRegistry.All)
        {
            var rec = records.FirstOrDefault(r => r.ToolId == def.Id);
            bool enabled = rec?.Enabled ?? false;
            Dictionary<string, string> cfg;

            if (rec?.ConfigJson != null)
                cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(rec.ConfigJson) ?? [];
            else
                cfg = [];

            result[def.Id] = (enabled, cfg);
        }

        return result;
    }

    public async Task SaveToolStateAsync(
        string userId, string toolId, bool enabled, Dictionary<string, string> cfg)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var rec = await db.UserToolConfigs
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ToolId == toolId);

        if (rec == null)
        {
            rec = new UserToolConfig { UserId = userId, ToolId = toolId };
            db.UserToolConfigs.Add(rec);
        }

        rec.Enabled    = enabled;
        rec.ConfigJson = cfg.Count > 0
            ? JsonSerializer.Serialize(cfg)
            : null;

        await db.SaveChangesAsync();
        _ = sync?.PushSnapshotAsync(userId);
        ToolsChanged?.Invoke(userId);
    }

    // ── Governance mode ───────────────────────────────────────────────────────
    // Stored as a reserved row in UserToolConfigs (no schema change needed). ToolId is namespaced
    // so it never collides with a real ToolRegistry entry.
    private const string GovernanceToolId = "__governance__";

    public async Task<GovernanceMode> GetGovernanceModeAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rec = await db.UserToolConfigs
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ToolId == GovernanceToolId);
        if (rec?.ConfigJson != null && Enum.TryParse<GovernanceMode>(rec.ConfigJson, out var mode))
            return mode;
        return GovernanceMode.Balanced; // sensible default for users who haven't chosen
    }

    public async Task SaveGovernanceModeAsync(string userId, GovernanceMode mode)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rec = await db.UserToolConfigs
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ToolId == GovernanceToolId);
        if (rec == null)
        {
            rec = new UserToolConfig { UserId = userId, ToolId = GovernanceToolId, Enabled = true };
            db.UserToolConfigs.Add(rec);
        }
        rec.ConfigJson = mode.ToString();
        await db.SaveChangesAsync();
        _ = sync?.PushSnapshotAsync(userId);
        ToolsChanged?.Invoke(userId);
    }

    // ── Auto-memory mode ─────────────────────────────────────────────────────
    // Same reserved-row trick as governance: ConfigJson holds "{Mode}:{Interval}".
    private const string AutoMemoryToolId = "__automemory__";

    public async Task<(AutoMemoryMode Mode, int Interval)> GetAutoMemorySettingsAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rec = await db.UserToolConfigs
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ToolId == AutoMemoryToolId);

        if (rec?.ConfigJson != null)
        {
            var parts = rec.ConfigJson.Split(':', 2);
            if (Enum.TryParse<AutoMemoryMode>(parts[0], out var mode))
            {
                var interval = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 5;
                return (mode, Math.Clamp(interval, 1, 50));
            }
        }
        return (AutoMemoryMode.ModelAuto, 5); // sensible default for users who haven't chosen
    }

    public async Task SaveAutoMemorySettingsAsync(string userId, AutoMemoryMode mode, int interval)
    {
        interval = Math.Clamp(interval, 1, 50);
        await using var db = await dbFactory.CreateDbContextAsync();
        var rec = await db.UserToolConfigs
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ToolId == AutoMemoryToolId);
        if (rec == null)
        {
            rec = new UserToolConfig { UserId = userId, ToolId = AutoMemoryToolId, Enabled = true };
            db.UserToolConfigs.Add(rec);
        }
        rec.ConfigJson = $"{mode}:{interval}";
        await db.SaveChangesAsync();
        _ = sync?.PushSnapshotAsync(userId);
        ToolsChanged?.Invoke(userId);
    }

    // ── Recall scope (single-node vs cross-node memory recall) ────────────────────────────────
    private const string RecallScopeToolId = "__recallscope__";

    public async Task<RecallScope> GetRecallScopeAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rec = await db.UserToolConfigs
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ToolId == RecallScopeToolId);
        if (rec?.ConfigJson != null && Enum.TryParse<RecallScope>(rec.ConfigJson, out var scope))
            return scope;
        // Default to AllNodes: memory is node-local and never replicated, so on a multi-node soul the
        // LLM node often isn't where memories live (e.g. LLM on Windows, Noosphere on the Mac). Fanning
        // out reaches memory wherever it is; single-node souls fall back to the one node, so no downside.
        return RecallScope.AllNodes;
    }

    public async Task SaveRecallScopeAsync(string userId, RecallScope scope)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rec = await db.UserToolConfigs
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ToolId == RecallScopeToolId);
        if (rec == null)
        {
            rec = new UserToolConfig { UserId = userId, ToolId = RecallScopeToolId, Enabled = true };
            db.UserToolConfigs.Add(rec);
        }
        rec.ConfigJson = scope.ToString();
        await db.SaveChangesAsync();
        _ = sync?.PushSnapshotAsync(userId);
        ToolsChanged?.Invoke(userId);
    }
}
