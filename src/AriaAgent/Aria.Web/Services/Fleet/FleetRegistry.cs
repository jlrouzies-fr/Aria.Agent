using System.Collections.Concurrent;
using System.Text.Json;
using Aria.Shared;
using Aria.Web.Services.Llm;
using Aria.Web.Services.ModelBridge;

namespace Aria.Web.Services.Fleet;

/// <summary>Mirror of the bridge's HardwareInventory snapshot (deserialized by property name).</summary>
public sealed record FleetHardware(
    string Hostname, string Os, string Arch, string? CpuModel, int CpuCores,
    double? TotalRamMb, string? GpuName, double? GpuVramTotalMb, string FormFactor);

public sealed record FleetModelChannel(string Name, IReadOnlyList<string> Models);

/// <summary>One bridge node in the fleet: identity, static hardware, live load, models.</summary>
public sealed class FleetNode
{
    public required string NodeId  { get; init; }
    public required string Label   { get; init; }
    public required string Platform { get; init; }
    public FleetHardware?   Hardware { get; set; }
    public BridgeMetrics?   Metrics  { get; set; }
    public List<FleetModelChannel> Channels { get; set; } = [];
}

/// <summary>
/// Server-side aggregation of the machine fleet: merges each connected bridge's live /metrics,
/// static /hardware (cached for the connection's lifetime), and the channel/model cache into one
/// per-node view. Consumers: the agent's fleet_status tool (compact JSON) and the /fleet
/// dashboard. Refreshes lazily — metrics at most every 15 s — so there's no background tunnel
/// traffic when nobody is looking.
/// </summary>
public class FleetRegistry(ModelBridgeRegistry registry, UserLocalSourceService localSources)
{
    private static readonly TimeSpan MetricsTtl = TimeSpan.FromSeconds(15);
    private const int MaxModelsPerChannel = 30; // cap: this payload can land in a small model's context

    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class CacheEntry
    {
        public FleetHardware?  Hardware;
        public BridgeMetrics?  Metrics;
        public DateTimeOffset  MetricsAt;
    }

    // Keyed by node id. Entries outlive a disconnect harmlessly — GetFleetAsync only reads
    // entries for currently connected nodes, and a reconnect reuses the cached hardware.
    private readonly ConcurrentDictionary<string, CacheEntry> _byNode = new();

    public async Task<IReadOnlyList<FleetNode>> GetFleetAsync(string userId, CancellationToken ct = default)
    {
        var nodes = registry.GetNodes(userId).OrderBy(n => n.ConnectedAt).ToList();
        await Task.WhenAll(nodes.Select(n => RefreshNodeAsync(userId, n.NodeId)));

        var channels = localSources.GetInfosCached(userId);
        if (channels.Count == 0 && nodes.Count > 0)
        {
            // Cold cache (fresh server / first session): fetch once so the fleet isn't model-less.
            await localSources.GetForUserAsync(userId);
            channels = localSources.GetInfosCached(userId);
        }

        return nodes.Select(n =>
        {
            _byNode.TryGetValue(n.NodeId, out var entry);
            return new FleetNode
            {
                NodeId   = n.NodeId,
                Label    = string.IsNullOrWhiteSpace(n.Label) ? n.Platform : n.Label,
                Platform = n.Platform,
                Hardware = entry?.Hardware,
                Metrics  = entry?.Metrics,
                Channels = channels
                    .Where(c => c.BridgeNodeId == n.NodeId && !c.IsPublic)
                    .Select(c => new FleetModelChannel(c.Name, c.Models.Take(MaxModelsPerChannel).ToList()))
                    .ToList(),
            };
        }).ToList();
    }

    /// <summary>The fleet_status tool payload: compact JSON sized for a model's context.</summary>
    public async Task<string> GetStatusJsonAsync(string userId, CancellationToken ct = default)
    {
        var fleet = await GetFleetAsync(userId, ct);
        if (fleet.Count == 0)
            return "No bridge nodes are currently connected — the fleet is empty.";

        var payload = fleet.Select(n => new
        {
            label      = n.Label,
            platform   = n.Platform,
            formFactor = n.Hardware?.FormFactor,
            hardware   = n.Hardware == null ? null : new
            {
                cpu             = n.Hardware.CpuModel,
                cores           = n.Hardware.CpuCores,
                totalRamMb      = n.Hardware.TotalRamMb,
                gpu             = n.Hardware.GpuName,
                gpuVramTotalMb  = n.Hardware.GpuVramTotalMb,
            },
            load = n.Metrics == null ? null : new
            {
                sysCpuPercent   = Round(n.Metrics.SystemCpuPercent),
                ramFreeMb       = Free(n.Metrics.SystemMemoryTotalMb, n.Metrics.SystemMemoryUsedMb),
                gpuUtilPercent  = Round(n.Metrics.GpuUtilizationPercent),
                gpuVramFreeMb   = Round(n.Metrics.GpuMemoryFreeMb),
            },
            channels = n.Channels.Select(c => new { name = c.Name, models = c.Models }),
        });
        return JsonSerializer.Serialize(new { nodes = payload }, Json);
    }

    private async Task RefreshNodeAsync(string userId, string nodeId)
    {
        var entry = _byNode.GetOrAdd(nodeId, _ => new CacheEntry());
        var tasks = new List<Task>();

        // Static inventory: fetched once per process lifetime of this cache entry.
        if (entry.Hardware == null)
            tasks.Add(Task.Run(async () =>
            {
                var hw = await GetJsonAsync<FleetHardware>(userId, "/hardware", nodeId);
                if (hw != null) entry.Hardware = hw;
            }));

        if (DateTimeOffset.UtcNow - entry.MetricsAt > MetricsTtl)
            tasks.Add(Task.Run(async () =>
            {
                var m = await GetJsonAsync<BridgeMetrics>(userId, "/metrics", nodeId);
                if (m != null)
                {
                    entry.Metrics   = m;
                    entry.MetricsAt = DateTimeOffset.UtcNow;
                }
            }));

        await Task.WhenAll(tasks);
    }

    private async Task<T?> GetJsonAsync<T>(string userId, string path, string nodeId) where T : class
    {
        try
        {
            var result = await registry.SendLocalRestAsync(userId, "GET", path, nodeId: nodeId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
                return JsonSerializer.Deserialize<T>(result.Value.Body, Json);
        }
        catch { /* node raced a disconnect — leave the cache as-is */ }
        return null;
    }

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;
    private static double? Free(double? total, double? used) =>
        total is { } t && used is { } u ? Math.Round(t - u, 0) : null;
}
