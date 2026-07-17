namespace Aria.Shared;

/// <summary>
/// Server-authoritative config snapshot pushed from Aria.Web to Aria.Bridge.
/// Kept in Aria.Shared so both sides share the contract without a project reference cycle.
/// </summary>
public record SyncSnapshot(
    string ServerSoulId,
    IReadOnlyList<SyncedSubAgentDto> Agents,
    IReadOnlyList<SyncedToolConfigDto> ToolConfigs,
    IReadOnlyList<SyncedLocalSourceDto> LocalSources,
    IReadOnlyList<SyncedMcpServerDto> McpServers,
    IReadOnlyList<SyncedCogitationFolderDto> Folders);

public record SyncedCogitationFolderDto(
    int Id,
    string Name,
    string? Color,
    int SortOrder,
    int? DefaultSubAgentId,
    string? DefaultProjectPath,
    string? StandingDirective);

public record SyncedSubAgentDto(
    int Id,
    string GeneratedName,
    string ArchetypeName,
    string GeneratedPersonality,
    string? UserDirectives,
    string AccentColor,
    string? ModelSourceName,
    string? ModelId,
    string? EnabledMcpNamesJson,
    string? AvatarSpriteKey,
    string? Nickname,
    DateTime CreatedAt,
    IReadOnlyList<SyncedSubAgentToolStateDto> ToolStates)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? GeneratedName : Nickname;
}

public record SyncedSubAgentToolStateDto(int Id, int SubAgentId, string ToolId, bool Enabled);

public record SyncedToolConfigDto(int Id, string ToolId, bool Enabled, string? ConfigJson);

public record SyncedLocalSourceDto(
    int Id, string Name, string Url, string ModelsJson,
    bool IsBridged, int SortOrder, string? BridgeNodeId);

public record SyncedMcpServerDto(
    int Id, string Name, int Transport, string Command,
    string ArgsJson, string? EnvJson, string? Url, bool Enabled);
