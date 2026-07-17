using System.Text.Json;
using Aria.Shared;

namespace Aria.Web.Services.ModelBridge;

/// <summary>Live metrics for one connected bridge node, tagged with a display label.</summary>
public sealed record NodeMetrics(string NodeId, string Label, BridgeMetrics Metrics);

/// <summary>
/// Fetches live performance metrics from the connected Aria.Bridge node(s).
/// </summary>
public class BridgeMetricsClient(ModelBridgeRegistry registry)
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Metrics from the default (most-recent) node, or null. Kept for single-node callers.</summary>
    public Task<BridgeMetrics?> GetMetricsAsync(string userId) => FetchNodeMetricsAsync(userId, null);

    /// <summary>
    /// Metrics from EVERY connected node for the soul, so telemetry can show each machine — not just the
    /// most-recently-connected one. Nodes that don't answer are simply omitted; order follows connect time.
    /// </summary>
    public async Task<List<NodeMetrics>> GetAllMetricsAsync(string userId)
    {
        var nodes = registry.GetNodes(userId).OrderBy(n => n.ConnectedAt).ToList();
        var results = await Task.WhenAll(nodes.Select(async n =>
        {
            var m = await FetchNodeMetricsAsync(userId, n.NodeId);
            return m == null ? null : new NodeMetrics(n.NodeId, NodeDisplayLabel(n, m), m);
        }));
        return results.Where(r => r != null).Select(r => r!).ToList();
    }

    private async Task<BridgeMetrics?> FetchNodeMetricsAsync(string userId, string? nodeId)
    {
        try
        {
            var result = await registry.SendLocalRestAsync(userId, "GET", "/metrics", nodeId: nodeId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
                return JsonSerializer.Deserialize<BridgeMetrics>(result.Value.Body, _json);
        }
        catch { }
        return null;
    }

    /// <summary>Node label for the telemetry header: device label, else the metrics' platform, else a
    /// short slice of the node id.</summary>
    private static string NodeDisplayLabel(NodeConnection n, BridgeMetrics m) =>
        !string.IsNullOrWhiteSpace(n.Label)     ? n.Label
        : !string.IsNullOrWhiteSpace(n.Platform) ? n.Platform
        : !string.IsNullOrWhiteSpace(m.Platform) ? m.Platform
        : n.NodeId.Length > 6 ? n.NodeId[..6] : n.NodeId;
}
