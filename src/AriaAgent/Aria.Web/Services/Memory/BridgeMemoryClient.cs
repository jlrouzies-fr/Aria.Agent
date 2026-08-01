using System.Text.Json;
using Aria.Web.Services.ModelBridge;

namespace Aria.Web.Services.Memory;

// Reads and writes Noosphere memory content on the user's local Aria.Bridge node, via the
// LocalRestRequest tunnel (ModelBridgeRegistry.SendLocalRestAsync). Used by the nav flyout panel —
// the agent's own Inscribe/Probe/Contemplate tools call the bridge directly (see Harness.cs "memory" case).
public class BridgeMemoryClient(ModelBridgeRegistry registry)
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<MemoryStatsDto?> GetStatsAsync(string userId, string? nodeId = null)
    {
        var result = await TryNodesAsync(userId, "GET", "/memory/stats", nodeId: nodeId);
        return result?.Body != null ? JsonSerializer.Deserialize<MemoryStatsDto>(result.Value.Body, _json) : null;
    }

    /// <summary>
    /// Aggregates ingest health across every connected node. Inscribe returns immediately and the
    /// worker extracts async — a failure on a secondary box (LM Studio down on Windows) must still
    /// reach the nav warning even when the primary Mac would answer a bare GetStatsAsync first.
    /// </summary>
    public async Task<MemoryNavHealth> GetNavHealthAsync(string userId)
    {
        var nodes = registry.GetNodes(userId).ToList();
        if (nodes.Count == 0)
        {
            var lone = await GetStatsAsync(userId);
            return AggregateNavHealth([(lone, null)]);
        }

        var samples = new List<(MemoryStatsDto? Stats, string? NodeLabel)>(nodes.Count);
        foreach (var n in nodes)
        {
            var label = !string.IsNullOrWhiteSpace(n.Label) ? n.Label
                : !string.IsNullOrWhiteSpace(n.Platform) ? n.Platform
                : n.NodeId;
            samples.Add((await GetStatsAsync(userId, n.NodeId), label));
        }
        return AggregateNavHealth(samples);
    }

    /// <summary>Pure fold of per-node stats into the sidebar indicator. Prefers the most recent failure.</summary>
    internal static MemoryNavHealth AggregateNavHealth(
        IReadOnlyList<(MemoryStatsDto? Stats, string? NodeLabel)> samples)
    {
        var processing = false;
        string? err = null;
        DateTime? errAt = null;
        string? errNode = null;
        foreach (var (stats, label) in samples)
        {
            if (stats == null) continue;
            if (stats.PendingIngests > 0) processing = true;
            if (string.IsNullOrEmpty(stats.LastExtractionError)) continue;
            if (errAt != null && (stats.LastExtractionErrorAt == null || stats.LastExtractionErrorAt <= errAt))
                continue;
            err = stats.LastExtractionError;
            errAt = stats.LastExtractionErrorAt;
            errNode = label;
        }
        return new MemoryNavHealth(processing, err, errAt, errNode);
    }

    public async Task<List<EngramDto>> GetEngramsAsync(string userId, int offset = 0, int limit = 20, string? entityId = null, string? q = null, string? nodeId = null)
    {
        var query = $"?offset={offset}&limit={limit}"
                    + (string.IsNullOrEmpty(entityId) ? "" : $"&entityId={Uri.EscapeDataString(entityId)}")
                    + (string.IsNullOrEmpty(q) ? "" : $"&q={Uri.EscapeDataString(q)}");
        var result = await TryNodesAsync(userId, "GET", $"/memory/engrams{query}", nodeId: nodeId);
        return result?.Body != null ? JsonSerializer.Deserialize<List<EngramDto>>(result.Value.Body, _json) ?? [] : [];
    }

    public async Task<List<MemoryEntityDto>> GetEntitiesAsync(string userId, int limit = 50, string? nodeId = null)
    {
        var result = await TryNodesAsync(userId, "GET", $"/memory/entities?limit={limit}", nodeId: nodeId);
        return result?.Body != null ? JsonSerializer.Deserialize<List<MemoryEntityDto>>(result.Value.Body, _json) ?? [] : [];
    }

    public async Task<MemoryGraphDto> GetGraphAsync(string userId, string? nodeId = null)
    {
        var result = await TryNodesAsync(userId, "GET", "/memory/graph", nodeId: nodeId);
        return result?.Body != null
            ? JsonSerializer.Deserialize<MemoryGraphDto>(result.Value.Body, _json) ?? new MemoryGraphDto([], [])
            : new MemoryGraphDto([], []);
    }

    public async Task<ProbeResponseDto?> ProbeAsync(string userId, string query, string? nodeId = null)
    {
        var body = JsonSerializer.Serialize(new { query }, _json);
        var result = await TryNodesAsync(userId, "POST", "/memory/probe", body, nodeId);
        return result?.Body != null ? JsonSerializer.Deserialize<ProbeResponseDto>(result.Value.Body, _json) : null;
    }

    public async Task<bool> DeleteEngramAsync(string userId, string engramId, string? nodeId = null)
    {
        var result = await TryNodesAsync(userId, "DELETE", $"/memory/engrams/{engramId}", nodeId: nodeId);
        return result?.StatusCode is 200 or 204;
    }

    public async Task<bool> MergeEntityAsync(string userId, string sourceId, string targetId, string? nodeId = null)
    {
        var body = JsonSerializer.Serialize(new { sourceId, targetId }, _json);
        var result = await TryNodesAsync(userId, "POST", "/memory/entities/merge", body, nodeId);
        return result?.StatusCode == 200;
    }

    /// <summary>Replace-all sync of one anchor source (currently just Terminal projects) so extraction
    /// can lead grouping toward them. Fire-and-forget from the caller's perspective — a bridge hiccup
    /// here just means anchors are stale until the next sync, never a user-visible error.</summary>
    public async Task SyncAnchorsAsync(string userId, List<(string Name, string Description)> anchors, string source = "terminal-project")
    {
        var body = JsonSerializer.Serialize(new
        {
            anchors = anchors.Select(a => new { name = a.Name, description = a.Description }),
            source
        }, _json);
        await TryNodesAsync(userId, "PUT", "/memory/anchors", body);
    }

    /// <summary>Auto-memory triggering (Regular/Always) — fire-and-forget best-effort inscribe, mirroring
    /// the tool's own semantics. Never throws; a bridge hiccup just means this turn wasn't inscribed.</summary>
    public async Task<bool> InscribeAsync(string userId, string content)
    {
        var body = JsonSerializer.Serialize(new { content }, _json);
        var result = await registry.SendLocalRestAsync(userId, "POST", "/memory/inscribe", body);
        if (result?.StatusCode is 200 or 202) return true;

        foreach (var node in registry.GetNodes(userId))
        {
            result = await registry.SendLocalRestAsync(userId, "POST", "/memory/inscribe", body, nodeId: node.NodeId);
            if (result?.StatusCode is 200 or 202) return true;
        }
        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(int StatusCode, string? Body)?> TryNodesAsync(string userId, string method, string path, string? body = null, string? nodeId = null)
    {
        // Explicit node: target it deterministically (per-node browsing) — no fall-through to others,
        // so the view always reflects exactly the node the user selected.
        if (!string.IsNullOrEmpty(nodeId))
            return await registry.SendLocalRestAsync(userId, method, path, body, nodeId: nodeId);

        var result = await registry.SendLocalRestAsync(userId, method, path, body);
        if (result?.StatusCode == 200) return result.Value;

        foreach (var node in registry.GetNodes(userId))
        {
            result = await registry.SendLocalRestAsync(userId, method, path, body, nodeId: node.NodeId);
            if (result?.StatusCode == 200) return result.Value;
        }

        return null;
    }
}

public record MemoryStatsDto(
    int Engrams, int Entities, int Links, int PendingIngests, int EmbeddedCount,
    bool EmbeddingsConfigured, bool ExtractionConfigured,
    string? LastExtractionError = null, DateTime? LastExtractionErrorAt = null);

/// <summary>Sidebar Noosphere indicator — pending queue and/or a live extraction-channel failure.</summary>
public record MemoryNavHealth(
    bool Processing, string? ExtractionError, DateTime? ExtractionErrorAt, string? ErrorNodeLabel);

public record EngramDto(
    string Id, string Content, string? TimeAnchor, DateTime CreatedAt,
    List<string> Entities, bool HasEmbedding);

public record MemoryEntityDto(string Id, string Name, string? Kind, int EngramCount);

public record MemoryGraphNodeDto(string Id, string Name, string? Kind, int EngramCount, int Group);
public record MemoryGraphEdgeDto(string From, string To, string Relation);
public record MemoryGraphDto(List<MemoryGraphNodeDto> Nodes, List<MemoryGraphEdgeDto> Edges);

public record ProbeResultDto(string Id, string Text, double Score, List<string> Entities, DateTime CreatedAt);
public record ProbeLegsDto(bool Vector, bool Fts, bool Graph);
public record ProbeResponseDto(List<ProbeResultDto> Results, ProbeLegsDto Legs);
