using System.Text.Json;
using Aria.Web.Data;
using Aria.Web.Services;

namespace Aria.Web.Services.Cogitations;

/// <summary>
/// Reads and writes cogitations and messages on the local Aria.Bridge node.
/// Bridge cogitation IDs use the deterministic format "sv-{serverCogitationId}" so the server
/// can reference bridge records without storing a separate mapping.
///
/// This client is the source of truth for bridge-owned conversations:
/// reads return the bridge content, and writes surface failures so the UI can warn.
/// </summary>
public class BridgeCogitationClient(ModelBridgeRegistry registry)
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public bool HasBridge(string userId) => registry.HasBridge(userId);

    public static string BridgeId(int serverCogitationId) => $"sv-{serverCogitationId}";

    // ── Read path ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches a cogitation's messages from the bridge that owns it.
    /// If the origin node is offline, tries the user's other connected nodes (sync fallback).
    /// Returns an empty list when no node can serve the content.
    /// </summary>
    public async Task<List<BridgeMessageDto>> GetMessagesAsync(
        string userId, int serverCogitationId, string? originNodeId = null)
    {
        var path = $"/cogitations/{BridgeId(serverCogitationId)}/messages";

        if (!string.IsNullOrEmpty(originNodeId))
        {
            var result = await registry.SendLocalRestAsync(userId, "GET", path, nodeId: originNodeId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
                return DeserializeMessages(result.Value.Body);
        }

        // Origin offline or unspecified: try any other connected node for this user.
        foreach (var node in registry.GetNodes(userId))
        {
            if (node.NodeId == originNodeId) continue;
            var result = await registry.SendLocalRestAsync(userId, "GET", path, nodeId: node.NodeId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
                return DeserializeMessages(result.Value.Body);
        }

        return [];
    }

    /// <summary>Fetches a single cogitation metadata from the bridge.</summary>
    public async Task<BridgeCogitationDto?> GetCogitationAsync(
        string userId, int serverCogitationId, string? originNodeId = null)
    {
        var path = $"/cogitations/{BridgeId(serverCogitationId)}";

        if (!string.IsNullOrEmpty(originNodeId))
        {
            var result = await registry.SendLocalRestAsync(userId, "GET", path, nodeId: originNodeId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
                return DeserializeCogitation(result.Value.Body);
        }

        foreach (var node in registry.GetNodes(userId))
        {
            if (node.NodeId == originNodeId) continue;
            var result = await registry.SendLocalRestAsync(userId, "GET", path, nodeId: node.NodeId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
                return DeserializeCogitation(result.Value.Body);
        }

        return null;
    }

    // ── Write path ────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures the cogitation exists in the bridge. Creates a placeholder soul on first use.
    /// Returns false if no bridge is connected or the call failed.
    /// </summary>
    public async Task<bool> EnsureCogitationAsync(
        string userId, string serverUserId, int serverCogitationId,
        string? ariaAvatarKey = null, string? subAgentId = null,
        string? originNodeId = null, int? folderId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                id           = BridgeId(serverCogitationId),
                serverUserId,
                ariaAvatarKey,
                subAgentId,
                folderId,
            }, _json);

            var result = await registry.SendLocalRestAsync(
                userId, "POST", "/cogitations/init", body, nodeId: originNodeId);
            return result?.StatusCode is 200 or 201;
        }
        catch { return false; }
    }

    /// <summary>Updates the title of a bridge cogitation. Returns false on failure.</summary>
    public async Task<bool> UpdateTitleAsync(string userId, int serverCogitationId, string title, string? originNodeId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { title }, _json);
            var result = await registry.SendLocalRestAsync(userId, "PUT", $"/cogitations/{BridgeId(serverCogitationId)}", body, nodeId: originNodeId);
            return result?.StatusCode is 200 or 204;
        }
        catch { return false; }
    }

    /// <summary>Updates the folder assignment of a bridge cogitation. Returns false on failure.</summary>
    public async Task<bool> UpdateFolderAsync(string userId, int serverCogitationId, int? folderId, string? originNodeId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { folderId }, _json);
            var result = await registry.SendLocalRestAsync(
                userId, "PUT", $"/cogitations/{BridgeId(serverCogitationId)}", body, nodeId: originNodeId);
            return result?.StatusCode is 200 or 204;
        }
        catch { return false; }
    }

    /// <summary>Updates the sections JSON of an existing bridge-owned message. Returns false on failure.</summary>
    public async Task<bool> UpdateMessageAsync(
        string userId, int serverCogitationId, string bridgeMessageId,
        string sectionsJson, string? originNodeId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { sectionsJson }, _json);
            var result = await registry.SendLocalRestAsync(
                userId, "PUT", $"/cogitations/{BridgeId(serverCogitationId)}/messages/{bridgeMessageId}", body,
                nodeId: originNodeId);
            return result?.StatusCode is 200 or 204;
        }
        catch { return false; }
    }

    /// <summary>Appends a message to a bridge cogitation. Returns false on failure.</summary>
    public async Task<bool> AddMessageAsync(
        string userId, int serverCogitationId,
        string role, string content, string? thinkingContent = null,
        string? sectionsJson = null,
        string? originNodeId = null,
        string? imageBase64 = null, string? imageMediaType = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { role, content, thinkingContent, sectionsJson, imageBase64, imageMediaType }, _json);
            var result = await registry.SendLocalRestAsync(
                userId, "POST", $"/cogitations/{BridgeId(serverCogitationId)}/messages", body,
                nodeId: originNodeId);
            return result?.StatusCode is 200 or 201;
        }
        catch { return false; }
    }

    /// <summary>Replaces the bridge cogitation's full transcript with <paramref name="messages"/>
    /// in order. Shared by Compact and edit-and-replay. Returns false on failure.</summary>
    public async Task<bool> ReplaceMessagesAsync(
        string userId, int serverCogitationId,
        IReadOnlyList<TranscriptMessageWrite> messages, string? originNodeId = null)
    {
        try
        {
            var payload = new
            {
                messages = messages.Select(m => new
                {
                    role            = m.Role,
                    content         = m.Content,
                    thinkingContent = m.ThinkingContent,
                    sectionsJson    = m.SectionsJson,
                    imageBase64     = m.ImageBase64,
                    imageMediaType  = m.ImageMediaType,
                }).ToList()
            };
            var body = JsonSerializer.Serialize(payload, _json);
            var result = await registry.SendLocalRestAsync(
                userId, "POST", $"/cogitations/{BridgeId(serverCogitationId)}/messages/replace", body,
                nodeId: originNodeId);
            return result?.StatusCode is 200 or 201;
        }
        catch { return false; }
    }

    /// <summary>Replaces all of a bridge cogitation's messages with a single summary message
    /// (used by "/compact"). Returns false on failure.</summary>
    public Task<bool> CompactAsync(
        string userId, int serverCogitationId, string summary, string? originNodeId = null) =>
        ReplaceMessagesAsync(userId, serverCogitationId,
            [new TranscriptMessageWrite("assistant", summary)], originNodeId);

    /// <summary>
    /// Copies a cogitation's content from its origin node to another node so the conversation can be
    /// continued there ("open on another channel/bridge"). Reads the messages from the origin,
    /// (re)creates the cogitation on the target, replays every message in order, and repoints its title.
    /// Returns true only if the target was reachable and every message copied — the caller then updates
    /// the server OriginNodeId. Both nodes must be online.
    /// </summary>
    public async Task<bool> MigrateToNodeAsync(
        string userId, string serverUserId, int serverCogitationId, string title,
        string? ariaAvatarKey, string? subAgentId, int? folderId,
        string fromNodeId, string toNodeId)
    {
        if (string.IsNullOrEmpty(toNodeId) || fromNodeId == toNodeId) return false;

        var messages = await GetMessagesAsync(userId, serverCogitationId, fromNodeId);

        if (!await EnsureCogitationAsync(userId, serverUserId, serverCogitationId,
                ariaAvatarKey, subAgentId, originNodeId: toNodeId, folderId: folderId))
            return false;

        await UpdateTitleAsync(userId, serverCogitationId, title, toNodeId);

        foreach (var m in messages.OrderBy(m => m.CreatedAt))
        {
            var ok = await AddMessageAsync(
                userId, serverCogitationId, m.Role, m.Content,
                m.ThinkingContent, m.SectionsJson, originNodeId: toNodeId,
                imageBase64: m.ImageBase64, imageMediaType: m.ImageMediaType);
            if (!ok) return false;
        }
        return true;
    }

    // ── Contact management (stored in local bridge DB) ─────────────────────────

    public async Task<List<ContactDto>> GetContactsAsync(string userId)
    {
        try
        {
            var result = await registry.SendLocalRestAsync(userId, "GET", "/contacts");
            if (result?.StatusCode == 200 && result.Value.Body != null)
                return JsonSerializer.Deserialize<List<ContactDto>>(result.Value.Body, _json) ?? [];
        }
        catch { }
        return [];
    }

    public async Task<bool> AddContactAsync(string userId, string name, string publicKey, string? avatarSpriteKey = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { name, publicKey, avatarSpriteKey }, _json);
            var result = await registry.SendLocalRestAsync(userId, "POST", "/contacts", body);
            return result?.StatusCode is 200 or 201;
        }
        catch { return false; }
    }

    public async Task<bool> DeleteContactAsync(string userId, string contactId)
    {
        try
        {
            var result = await registry.SendLocalRestAsync(userId, "DELETE", $"/contacts/{contactId}");
            return result?.StatusCode == 200;
        }
        catch { return false; }
    }

    // ── Exchange transcript — push to local bridge after completion ────────────

    public async Task PushExchangeTranscriptAsync(
        string userId, string serverUserId,
        string exchangeId, string topic,
        IReadOnlyList<(string AgentLabel, string Content, bool IsOurs)> turns)
    {
        try
        {
            var cogId    = $"ex-{exchangeId}";
            var initBody = JsonSerializer.Serialize(new
            {
                id           = cogId,
                serverUserId,
                ariaAvatarKey = (string?)null,
                subAgentId    = (string?)null,
            }, _json);
            await registry.SendLocalRestAsync(userId, "POST", "/cogitations/init", initBody);

            var titleBody = JsonSerializer.Serialize(new { title = $"EXCHANGE: {topic}" }, _json);
            await registry.SendLocalRestAsync(userId, "PUT", $"/cogitations/{cogId}", titleBody);

            foreach (var (label, content, isOurs) in turns)
            {
                var msgBody = JsonSerializer.Serialize(new
                {
                    role            = isOurs ? "assistant" : "user",
                    content         = $"[{label}]\n{content}",
                    thinkingContent = (string?)null,
                }, _json);
                await registry.SendLocalRestAsync(userId, "POST", $"/cogitations/{cogId}/messages", msgBody);
            }
        }
        catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<BridgeMessageDto> DeserializeMessages(string body) =>
        JsonSerializer.Deserialize<List<BridgeMessageDto>>(body, _json) ?? [];

    private static BridgeCogitationDto? DeserializeCogitation(string body) =>
        JsonSerializer.Deserialize<BridgeCogitationDto>(body, _json);
}

public record ContactDto(string Id, string Name, string PublicKey, string? AvatarSpriteKey, DateTime AddedAt);

public record BridgeMessageDto(
    string Id,
    string CogitationId,
    string Role,
    string Content,
    string? ThinkingContent,
    string? SectionsJson,
    DateTime CreatedAt,
    string? ImageBase64 = null,
    string? ImageMediaType = null);

public record BridgeCogitationDto(
    string Id,
    string SoulId,
    string Title,
    string? AriaAvatarKey,
    string? SubAgentId,
    int? FolderId,
    DateTime CreatedAt,
    DateTime UpdatedAt);
