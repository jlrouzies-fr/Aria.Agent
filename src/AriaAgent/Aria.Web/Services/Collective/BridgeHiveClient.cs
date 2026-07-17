using System.Text.Json;
using Aria.Web.Data;

namespace Aria.Web.Services.Collective;

/// <summary>
/// Reads and writes Hive collective content on the local Aria.Bridge node.
/// Bridge IDs use deterministic prefixes ("hv-{id}", "ht-{id}", "he-{id}") so the server
/// can reference bridge records without a separate mapping table.
/// </summary>
public class BridgeHiveClient(ModelBridgeRegistry registry)
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string CollectiveId(int serverCollectiveId) => $"hv-{serverCollectiveId}";
    public static string TaskId(int serverTaskId)             => $"ht-{serverTaskId}";
    public static string EventId(int serverEventId)           => $"he-{serverEventId}";

    // ── Read path ─────────────────────────────────────────────────────────────

    public async Task<BridgeHiveCollectiveDto?> GetCollectiveAsync(
        string userId, int serverCollectiveId, string? originNodeId = null)
    {
        var path = $"/hive/collectives/{CollectiveId(serverCollectiveId)}";
        var result = await TryNodesAsync(userId, "GET", path, originNodeId: originNodeId);
        if (result?.StatusCode == 200 && result.Value.Body != null)
            return JsonSerializer.Deserialize<BridgeHiveCollectiveDto>(result.Value.Body, _json);
        return null;
    }

    public async Task<List<BridgeHiveTaskDto>> GetTasksAsync(
        string userId, int serverCollectiveId, string? originNodeId = null)
    {
        var path = $"/hive/collectives/{CollectiveId(serverCollectiveId)}/tasks";
        var result = await TryNodesAsync(userId, "GET", path, originNodeId: originNodeId);
        if (result?.StatusCode == 200 && result.Value.Body != null)
            return JsonSerializer.Deserialize<List<BridgeHiveTaskDto>>(result.Value.Body, _json) ?? [];
        return [];
    }

    public async Task<List<BridgeHiveEventDto>> GetEventsAsync(
        string userId, int serverCollectiveId, string? originNodeId = null)
    {
        var path = $"/hive/collectives/{CollectiveId(serverCollectiveId)}/events";
        var result = await TryNodesAsync(userId, "GET", path, originNodeId: originNodeId);
        if (result?.StatusCode == 200 && result.Value.Body != null)
            return JsonSerializer.Deserialize<List<BridgeHiveEventDto>>(result.Value.Body, _json) ?? [];
        return [];
    }

    /// <summary>Loads collective + tasks + events in one helper call.</summary>
    public async Task<BridgeHiveContent?> LoadContentAsync(
        string userId, int serverCollectiveId, string? originNodeId = null)
    {
        var collective = await GetCollectiveAsync(userId, serverCollectiveId, originNodeId);
        if (collective == null) return null;

        var tasks  = await GetTasksAsync(userId, serverCollectiveId, originNodeId);
        var events = await GetEventsAsync(userId, serverCollectiveId, originNodeId);
        return new BridgeHiveContent(collective, tasks, events);
    }

    // ── Write path ────────────────────────────────────────────────────────────

    public async Task<bool> EnsureCollectiveAsync(
        string userId, int serverCollectiveId, string originNodeId,
        string? objective = null, string? resultSummary = null,
        string? lastFeedback = null, string? synapseMemory = null)
    {
        var body = JsonSerializer.Serialize(new
        {
            id            = CollectiveId(serverCollectiveId),
            serverUserId  = userId,
            objective,
            resultSummary,
            lastFeedback,
            synapseMemory,
        }, _json);

        var result = await registry.SendLocalRestAsync(
            userId, "POST", "/hive/collectives/init", body, nodeId: originNodeId);
        return result?.StatusCode is 200 or 201;
    }

    public async Task<bool> UpdateCollectiveContentAsync(
        string userId, int serverCollectiveId, string originNodeId,
        string? objective = null, string? resultSummary = null,
        string? lastFeedback = null, string? synapseMemory = null)
    {
        var body = JsonSerializer.Serialize(new
        {
            objective,
            resultSummary,
            lastFeedback,
            synapseMemory,
        }, _json);

        var result = await registry.SendLocalRestAsync(
            userId, "PUT", $"/hive/collectives/{CollectiveId(serverCollectiveId)}/content", body,
            nodeId: originNodeId);
        return result?.StatusCode is 200 or 204;
    }

    public async Task<bool> UpsertTaskContentAsync(
        string userId, int serverCollectiveId, int serverTaskId, string originNodeId,
        string? title = null, string? instruction = null,
        string? effectiveInstruction = null, string? result = null)
    {
        var body = JsonSerializer.Serialize(new
        {
            title,
            instruction,
            effectiveInstruction,
            result,
        }, _json);

        var resp = await registry.SendLocalRestAsync(
            userId, "POST",
            $"/hive/collectives/{CollectiveId(serverCollectiveId)}/tasks/{TaskId(serverTaskId)}/content",
            body, nodeId: originNodeId);
        return resp?.StatusCode is 200 or 201;
    }

    public async Task<bool> AppendEventAsync(
        string userId, int serverCollectiveId, string originNodeId,
        DateTime timestamp, string type, int? actorMemberId, int? taskId, string message)
    {
        var body = JsonSerializer.Serialize(new
        {
            id            = (string?)null,
            timestamp,
            type,
            actorMemberId,
            taskId,
            message,
        }, _json);

        var result = await registry.SendLocalRestAsync(
            userId, "POST", $"/hive/collectives/{CollectiveId(serverCollectiveId)}/events", body,
            nodeId: originNodeId);
        return result?.StatusCode is 200 or 201;
    }

    public async Task<bool> DeleteCollectiveAsync(
        string userId, int serverCollectiveId, string originNodeId)
    {
        var result = await registry.SendLocalRestAsync(
            userId, "DELETE", $"/hive/collectives/{CollectiveId(serverCollectiveId)}",
            nodeId: originNodeId);
        return result?.StatusCode is 200 or 204;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(int StatusCode, string? Body)?> TryNodesAsync(
        string userId, string method, string path, string? body = null, string? originNodeId = null)
    {
        if (!string.IsNullOrEmpty(originNodeId))
        {
            var result = await registry.SendLocalRestAsync(userId, method, path, body, nodeId: originNodeId);
            if (result?.StatusCode == 200)
                return result.Value;
        }

        foreach (var node in registry.GetNodes(userId))
        {
            if (node.NodeId == originNodeId) continue;
            var result = await registry.SendLocalRestAsync(userId, method, path, body, nodeId: node.NodeId);
            if (result?.StatusCode == 200)
                return result.Value;
        }

        return null;
    }
}

public record BridgeHiveContent(
    BridgeHiveCollectiveDto Collective,
    List<BridgeHiveTaskDto> Tasks,
    List<BridgeHiveEventDto> Events);

public record BridgeHiveCollectiveDto(
    string Id,
    string SoulId,
    string Objective,
    string? ResultSummary,
    string? LastFeedback,
    string? SynapseMemory,
    DateTime UpdatedAt);

public record BridgeHiveTaskDto(
    string Id,
    string CollectiveId,
    string Title,
    string Instruction,
    string? EffectiveInstruction,
    string? Result,
    DateTime UpdatedAt);

public record BridgeHiveEventDto(
    string Id,
    string CollectiveId,
    DateTime Timestamp,
    string Type,
    int? ActorMemberId,
    int? TaskId,
    string Message);
