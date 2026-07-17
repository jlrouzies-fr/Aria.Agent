using System.Collections.Concurrent;
using System.Text.Json;
using Aria.Tools;
using Aria.Web.Services.ModelBridge;

namespace Aria.Web.Services.Tool;

/// <summary>
/// Read-only, bridge-backed view of the user's MCP servers. Servers are authored ONLY on the node;
/// the server receives only names/transports and never sees commands, args, or env secrets.
/// </summary>
public sealed class BridgeMcpClient(ModelBridgeRegistry registry)
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ConcurrentDictionary<string, List<BridgeMcpInfo>> _cache = new();

    /// <summary>Fetches the node's MCP servers (refreshing the cache) and returns them.</summary>
    public async Task<List<BridgeMcpInfo>> GetMcpInfosAsync(string userId)
    {
        var actualNodeId = registry.GetDefaultNode(userId)?.NodeId;
        if (actualNodeId == null) return [];

        try
        {
            var result = await registry.SendLocalRestAsync(userId, "GET", "/mcps", nodeId: actualNodeId, timeoutSeconds: 10);
            if (result?.StatusCode != 200 || string.IsNullOrEmpty(result.Value.Body))
                return GetInfosCached(userId);

            using var doc = JsonDocument.Parse(result.Value.Body);
            if (!doc.RootElement.TryGetProperty("servers", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<BridgeMcpInfo>();
            foreach (var e in arr.EnumerateArray())
            {
                var name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;

                list.Add(new BridgeMcpInfo(
                    Name:         name,
                    Transport:    (McpTransport)(e.TryGetProperty("transport", out var t) ? t.GetInt32() : 0),
                    Command:      e.TryGetProperty("command", out var c) ? c.GetString() : null,
                    Url:          e.TryGetProperty("url", out var u) ? u.GetString() : null,
                    Enabled:      !e.TryGetProperty("enabled", out var en) || en.GetBoolean(),
                    BridgeNodeId: actualNodeId));
            }

            _cache[userId] = list;
            return list;
        }
        catch
        {
            return GetInfosCached(userId);
        }
    }

    /// <summary>Non-blocking cache read of the MCP list.</summary>
    public List<BridgeMcpInfo> GetInfosCached(string userId) =>
        _cache.TryGetValue(userId, out var c) ? c : [];

    /// <summary>Returns configs for the named MCPs (used by background services).</summary>
    public async Task<List<McpServerConfig>> GetConfigsForNamesAsync(string userId, IReadOnlyCollection<string> names)
    {
        var infos = await GetMcpInfosAsync(userId);
        return infos
            .Where(i => i.Enabled && names.Contains(i.Name))
            .Select(ToConfig)
            .ToList();
    }

    public static McpServerConfig ToConfig(BridgeMcpInfo info) => new(
        Name:        info.Name,
        Command:     "",
        Arguments:   [],
        Enabled:     info.Enabled,
        Environment: null,
        Transport:   info.Transport,
        Url:         info.Url);
}

public sealed record BridgeMcpInfo(
    string Name,
    McpTransport Transport,
    string? Command,
    string? Url,
    bool Enabled,
    string? BridgeNodeId);
