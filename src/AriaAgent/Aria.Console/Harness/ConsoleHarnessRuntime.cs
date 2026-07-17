using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aria.Agent;
using Aria.Harness;
using Aria.Harness.Core;
using Aria.Harness.Formats;
using Aria.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aria.Console.Harness;

/// <summary>
/// Console host implementation of <see cref="IHarnessRuntime"/>.
/// Talks to the mandatory local Aria.Bridge (http://localhost:5741) for config, keys, and LLM proxying.
/// </summary>
public sealed class ConsoleHarnessRuntime : IHarnessRuntime
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HttpClient _http;
    private readonly ILogger<ConsoleHarnessRuntime> _logger;
    private readonly Dictionary<string, string> _oauthTokens = new(StringComparer.OrdinalIgnoreCase);
    private List<ModelSource>? _localSources;

    public IFormatCache FormatCache { get; }

    /// <summary>Adds an extra local source (e.g. FoundryLocal) to the console catalog.</summary>
    public void AddSource(ModelSource source)
    {
        GetLocalSources().Add(source);
    }

    public ConsoleHarnessRuntime(
        IConfiguration config,
        IFormatCache? formatCache = null,
        ILogger<ConsoleHarnessRuntime>? logger = null,
        string bridgeBaseUrl = "http://localhost:5741",
        HttpClient? httpClient = null)
    {
        _logger     = logger ?? NullLogger<ConsoleHarnessRuntime>.Instance;
        FormatCache = formatCache ?? new ConsoleFormatCache();
        _http       = httpClient ?? new HttpClient { BaseAddress = new Uri(bridgeBaseUrl.TrimEnd('/') + "/") };
    }

    /// <summary>Pre-seeds an OAuth token so the harness can enable provider-specific tools.</summary>
    public void SetOAuthToken(string providerName, string token)
        => _oauthTokens[providerName] = token;

    public ModelSource? FindSource(string? name, HarnessContext context)
    {
        var locals = GetLocalSources();
        var catalog = PublicModelSourceCatalog.Providers;

        if (string.IsNullOrEmpty(name))
            return locals.FirstOrDefault() ?? catalog.FirstOrDefault();

        return locals.FirstOrDefault(s => s.Name == name)
            ?? catalog.FirstOrDefault(s => s.Name == name);
    }

    public async Task<string?> GetApiKeyAsync(string providerName, HarnessContext context, CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<KeysResponse>("keys", Json, ct);
            return result?.Providers.Contains(providerName, StringComparer.OrdinalIgnoreCase) == true
                ? "configured"
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not query bridge keys for {Provider}", providerName);
            return null;
        }
    }

    public Task<string?> GetOAuthTokenAsync(string providerName, HarnessContext context, CancellationToken ct = default)
        => Task.FromResult(_oauthTokens.TryGetValue(providerName, out var token) ? token : null);

    public Task<bool> IsBridgeAvailableAsync(HarnessContext context, CancellationToken ct = default)
        => Task.FromResult(true);

    // Console talks to a single local bridge — no multi-node fan-out; recall falls back to that node.
    public Task<IReadOnlyList<string>> GetBridgeNodeIdsAsync(HarnessContext context, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public async Task<string> BridgePostAsync(string url, string body, HarnessContext context, CancellationToken ct = default, string? keyRef = null, bool requireKey = false, string? nodeId = null)
    {
        var req = new LlmProxyRequest(url, body, keyRef, null, requireKey);
        using var response = await _http.PostAsJsonAsync("llm/proxy", req, Json, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async IAsyncEnumerable<string> BridgeStreamAsync(string url, string body, HarnessContext context, [EnumeratorCancellation] CancellationToken ct = default, string? keyRef = null, bool requireKey = false, string? nodeId = null)
    {
        var req = new LlmProxyRequest(url, body, keyRef, null, requireKey);
        using var request = new HttpRequestMessage(HttpMethod.Post, "llm/proxy")
        {
            Content = JsonContent.Create(req, options: Json)
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested && await reader.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrEmpty(line))
                yield return line + "\n";
        }
    }

    private List<ModelSource> GetLocalSources()
    {
        if (_localSources != null) return _localSources;

        try
        {
            var dtoTask = _http.GetFromJsonAsync<List<SyncedLocalSourceDto>>("console/sources", Json);
            var dtos = dtoTask.GetAwaiter().GetResult() ?? [];
            _localSources = dtos.Select(d => new ModelSource
            {
                Name         = d.Name,
                Url          = d.Url,
                Models       = DeserializeModels(d.ModelsJson),
                IsBridged    = d.IsBridged,
                BridgeNodeId = d.BridgeNodeId,
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load local sources from bridge");
            _localSources = [];
        }

        return _localSources;
    }

    private static List<string> DeserializeModels(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json, Json) ?? []; }
        catch { return []; }
    }

    private sealed record KeysResponse(List<string> Providers);
    private sealed record LlmProxyRequest(string Url, string? Body, string? KeyRef, string? ApiKey, bool RequireKey);
}
