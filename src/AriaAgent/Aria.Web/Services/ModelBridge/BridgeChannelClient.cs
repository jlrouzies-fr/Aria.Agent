using System.Text.Json;

namespace Aria.Web.Services.ModelBridge;

/// <summary>Read-only view of a node-authoritative channel, as returned by the bridge's GET /channels.</summary>
public sealed record BridgeChannelInfo(
    string Name, string Url, List<string> Models, bool IsBridged, bool IsPublic, bool HasKey,
    string? BridgeNodeId, string? NodeLabel = null, int? ContextWindow = null);

/// <summary>
/// Fetches the node-authoritative channel list from the bridge. Channels (name → URL, models, key) are
/// owned and authored on the bridge; the server only mirrors them for the picker and never writes them.
/// </summary>
public sealed class BridgeChannelClient(ModelBridgeRegistry registry)
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Returns the channels declared on the user's node, or null if no node is reachable.</summary>
    public async Task<List<BridgeChannelInfo>?> GetChannelsAsync(string userId, string? nodeId = null)
    {
        // Resolve the node we will actually talk to so every returned channel can be pinned to it.
        var actualNodeId = nodeId ?? registry.GetDefaultNode(userId)?.NodeId;
        if (actualNodeId == null) return null;
        var label = registry.TryGetNode(userId, actualNodeId, out var nc) ? NodeDisplayLabel(nc) : null;
        return await FetchNodeChannelsAsync(userId, actualNodeId, label);
    }

    /// <summary>
    /// Aggregates channels from EVERY connected node for the soul. Custom (node-authored) channels are
    /// kept per node — each pinned to its own <see cref="BridgeChannelInfo.BridgeNodeId"/> — so a Mac and
    /// a Windows node both expose their own local models. Public providers share one catalog across nodes,
    /// so they are collapsed to a single entry pinned to a node that actually holds the key (if any).
    /// Returns null only when no node answered at all (so callers keep their last cache).
    /// </summary>
    public async Task<List<BridgeChannelInfo>?> GetAllChannelsAsync(string userId)
    {
        var nodes = registry.GetNodes(userId);
        if (nodes.Count == 0) return null;

        var results = await Task.WhenAll(
            nodes.Select(n => FetchNodeChannelsAsync(userId, n.NodeId, NodeDisplayLabel(n))));

        // Every node failing to answer is "unavailable" (null), not "no channels" (empty) — blanking the
        // picker on a transient tunnel hiccup would drop the user's current selection.
        if (results.All(r => r == null)) return null;
        var all = results.Where(r => r != null).SelectMany(r => r!).ToList();

        var custom = all.Where(c => !c.IsPublic);
        var publicMerged = all.Where(c => c.IsPublic)
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            // Pin the single public entry to a node that has the key, so a keyed call routes somewhere
            // that can actually answer; fall back to any node if none has stored one.
            .Select(g => g.FirstOrDefault(x => x.HasKey) ?? g.First());

        return [..publicMerged, ..custom];
    }

    private async Task<List<BridgeChannelInfo>?> FetchNodeChannelsAsync(string userId, string nodeId, string? nodeLabel)
    {
        try
        {
            var result = await registry.SendLocalRestAsync(userId, "GET", "/channels", nodeId: nodeId, timeoutSeconds: 10);
            if (result?.StatusCode != 200 || string.IsNullOrEmpty(result.Value.Body))
                return null;

            using var doc = JsonDocument.Parse(result.Value.Body);
            if (!doc.RootElement.TryGetProperty("channels", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<BridgeChannelInfo>();
            foreach (var e in arr.EnumerateArray())
            {
                var models = new List<string>();
                if (e.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array)
                    models.AddRange(m.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));

                int? contextWindow = null;
                if (e.TryGetProperty("contextWindow", out var cw) && cw.ValueKind == JsonValueKind.Number)
                    contextWindow = cw.GetInt32();

                list.Add(new BridgeChannelInfo(
                    Name:         e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Url:          e.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    Models:       models,
                    IsBridged:    !e.TryGetProperty("isBridged", out var b) || b.GetBoolean(),
                    IsPublic:     e.TryGetProperty("isPublic", out var p) && p.GetBoolean(),
                    HasKey:       e.TryGetProperty("hasKey", out var h) && h.GetBoolean(),
                    BridgeNodeId: nodeId,
                    NodeLabel:    nodeLabel,
                    ContextWindow: contextWindow));
            }
            return list.Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
        }
        catch
        {
            return null; // this node's tunnel errored → treat it as unavailable, keep the others
        }
    }

    /// <summary>Asks the node to re-query a channel's own endpoint for its model list (used by the
    /// left-nav "rediscover models" action so the picker doesn't go stale between channel saves).
    /// Returns null if the node is unreachable or discovery found nothing.</summary>
    public async Task<List<string>?> DiscoverModelsAsync(string userId, string nodeId, string url, string keyRef)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { url, keyRef });
            var result = await registry.SendLocalRestAsync(userId, "POST", "/llm/discover-models", body, nodeId: nodeId, timeoutSeconds: 20);
            if (result?.StatusCode != 200 || string.IsNullOrEmpty(result.Value.Body)) return null;

            using var doc = JsonDocument.Parse(result.Value.Body);
            if (!doc.RootElement.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean()) return null;
            if (!doc.RootElement.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;

            return arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
        }
        catch { return null; }
    }

    /// <summary>Human-friendly node name for disambiguating same-named channels: label, else platform,
    /// else a short slice of the node id (always distinct, if ugly).</summary>
    private static string NodeDisplayLabel(NodeConnection n) =>
        !string.IsNullOrWhiteSpace(n.Label)    ? n.Label
        : !string.IsNullOrWhiteSpace(n.Platform) ? n.Platform
        : n.NodeId.Length > 6 ? n.NodeId[..6] : n.NodeId;
}
