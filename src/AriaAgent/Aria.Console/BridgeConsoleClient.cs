using System.Net.Http.Json;
using System.Text.Json;
using Aria.Shared;

namespace Aria.Console;

/// <summary>
/// Typed client for the local Aria.Bridge console endpoints.
/// </summary>
public sealed class BridgeConsoleClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly HttpClient _http;

    public BridgeConsoleClient(string baseUrl = "http://localhost:5741")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<ConsoleProfile?> GetProfileAsync(CancellationToken ct = default)
    {
        try { return await _http.GetFromJsonAsync<ConsoleProfile>("console/profile", Json, ct); }
        catch { return null; }
    }

    public async Task<List<SyncedSubAgentDto>> GetAgentsAsync(CancellationToken ct = default)
    {
        try { return await _http.GetFromJsonAsync<List<SyncedSubAgentDto>>("console/agents", Json, ct) ?? []; }
        catch { return []; }
    }

    public async Task<List<SyncedToolConfigDto>> GetToolConfigsAsync(CancellationToken ct = default)
    {
        try { return await _http.GetFromJsonAsync<List<SyncedToolConfigDto>>("console/tools", Json, ct) ?? []; }
        catch { return []; }
    }

    public async Task<List<SyncedLocalSourceDto>> GetLocalSourcesAsync(CancellationToken ct = default)
    {
        try { return await _http.GetFromJsonAsync<List<SyncedLocalSourceDto>>("console/sources", Json, ct) ?? []; }
        catch { return []; }
    }

    public async Task<List<SyncedMcpServerDto>> GetMcpServersAsync(CancellationToken ct = default)
    {
        try { return await _http.GetFromJsonAsync<List<SyncedMcpServerDto>>("console/mcps", Json, ct) ?? []; }
        catch { return []; }
    }

    public async Task<List<SyncedCogitationFolderDto>> GetFoldersAsync(CancellationToken ct = default)
    {
        try { return await _http.GetFromJsonAsync<List<SyncedCogitationFolderDto>>("console/folders", Json, ct) ?? []; }
        catch { return []; }
    }

    public async Task CreateSoulAsync(string name, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("soul", new { name, avatarSpriteKey = (string?)null, accentColor = (string?)null }, Json, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task LinkServerAsync(string serverUrl, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("soul/link-server", new { serverUrl }, Json, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<bool> IsMemoryAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("memory/status", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task InscribeMemoryAsync(string content, CancellationToken ct = default)
    {
        try { await _http.PostAsJsonAsync("memory/inscribe", new { content }, Json, ct); }
        catch { /* fire-and-forget — inscribe is best-effort */ }
    }
}

public sealed record ConsoleProfile(
    string Id,
    string Name,
    string? AvatarSpriteKey,
    string? AccentColor,
    string? ServerSoulId,
    string? ServerUrl,
    string NodeLabel,
    bool HasKeypair,
    ConsoleSyncCounts Synced);

public sealed record ConsoleSyncCounts(int Agents, int ToolStates, int ToolConfigs, int Sources, int Mcps, int Folders);
