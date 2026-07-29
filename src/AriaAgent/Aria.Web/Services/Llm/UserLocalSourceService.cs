using System.Collections.Concurrent;
using System.Text.Json;
using Aria.Agent;
using Aria.Web.Data;
using Aria.Web.Services.ModelBridge;

namespace Aria.Web.Services.Llm;

/// <summary>
/// Read-only, bridge-backed view of the user's LLM channels. Channels are authored ONLY on the node
/// (bridge status page) and fetched here via <see cref="BridgeChannelClient"/>; the server persists
/// nothing about them. A per-user in-memory cache lets synchronous callers (e.g. the harness'
/// <c>FindSource</c>) read the last-fetched list without blocking on the tunnel.
/// </summary>
public class UserLocalSourceService(BridgeChannelClient channels)
{
    private readonly ConcurrentDictionary<string, List<BridgeChannelInfo>> _cache = new();

    /// <summary>Fetches channels from EVERY connected node (refreshing the cache) and returns them in the
    /// legacy shape. Same-named channels living on different nodes are disambiguated for display.</summary>
    public async Task<List<UserLocalSource>> GetForUserAsync(string userId)
    {
        var infos = await channels.GetAllChannelsAsync(userId);
        if (infos != null) _cache[userId] = infos;
        return ToLocalSources(GetInfosCached(userId));
    }

    /// <summary>Non-blocking cache read of the rich channel info (name, models, key presence).</summary>
    public List<BridgeChannelInfo> GetInfosCached(string userId) =>
        _cache.TryGetValue(userId, out var c) ? c : [];

    /// <summary>Non-blocking cache read for synchronous source resolution. Empty until first fetch.</summary>
    public List<UserLocalSource> GetCached(string userId) => ToLocalSources(GetInfosCached(userId));

    /// <summary>Custom (non-public) channels only — for the "LOCAL LLM" panel section. Public providers
    /// are surfaced separately from the catalog, so including them here would double them in the UI.</summary>
    public List<UserLocalSource> GetCustomCached(string userId) =>
        ToLocalSources(GetInfosCached(userId).Where(c => !c.IsPublic).ToList());

    private static List<UserLocalSource> ToLocalSources(List<BridgeChannelInfo> infos)
    {
        // A channel name that appears on more than one node needs a node-label suffix to stay unique —
        // the Name doubles as the selection key (persisted LastModelSource) and the FindSource lookup.
        // The real channel name is preserved in ChannelName so the bridge keyRef still resolves.
        var collide = infos
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.BridgeNodeId).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var i = 0;
        return infos.Select(c =>
        {
            var ambiguous = collide.Contains(c.Name);
            var display   = ambiguous && !string.IsNullOrWhiteSpace(c.NodeLabel)
                ? $"{c.Name} · {c.NodeLabel}"
                : c.Name;
            return new UserLocalSource
            {
                Id            = ++i,
                Name          = display,
                ChannelName   = ambiguous ? c.Name : null,
                Url           = c.Url,
                ModelsJson    = JsonSerializer.Serialize(c.Models),
                IsBridged     = c.IsBridged,
                SortOrder     = i,
                BridgeNodeId  = c.BridgeNodeId,
                ContextWindow = c.ContextWindow,
            };
        }).ToList();
    }

    /// <summary>Re-queries a channel's own endpoint for its model list and updates the cache in place —
    /// backs the left-nav "⟳" rediscover action. Returns false if the node didn't answer or found
    /// nothing; the bridge's own stored list (and next full fetch) is unaffected either way.</summary>
    public async Task<bool> RediscoverModelsAsync(string userId, string nodeId, string channelName)
    {
        var infos = GetInfosCached(userId);
        var info = infos.FirstOrDefault(c =>
            c.BridgeNodeId == nodeId && string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));
        if (info == null) return false;

        var models = await channels.DiscoverModelsAsync(userId, nodeId, info.Url, info.Name);
        if (models == null) return false;

        _cache[userId] = infos.Select(c =>
            c.BridgeNodeId == nodeId && string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase)
                ? c with { Models = models }
                : c).ToList();
        return true;
    }

    public static ModelSource ToModelSource(UserLocalSource src)
    {
        var models = new List<string>();
        try { models = JsonSerializer.Deserialize<List<string>>(src.ModelsJson) ?? []; }
        catch { /* invalid JSON — use empty list */ }

        return new ModelSource
        {
            Name          = src.Name,
            Url           = src.Url,
            Models        = models,
            IsBridged     = src.IsBridged,
            BridgeNodeId  = src.BridgeNodeId,
            ChannelName   = src.ChannelName,
            ContextWindow = src.ContextWindow,
        };
    }
}
