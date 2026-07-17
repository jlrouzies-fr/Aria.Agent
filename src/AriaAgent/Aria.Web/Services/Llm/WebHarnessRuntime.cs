using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aria.Agent;
using Aria.Harness.Core;
using Aria.Harness.Formats;
using Aria.Web.Services.ModelBridge;

namespace Aria.Web.Services.Llm;

/// <summary>
/// Web host implementation of <see cref="IHarnessRuntime"/>.
/// Bridges the Aria.Harness orchestration layer to Aria.Web's user DB and SignalR bridge registry.
/// OAuth tokens live on the bridge, not the server.
/// </summary>
public sealed class WebHarnessRuntime : IHarnessRuntime
{
    private readonly UserLocalSourceService _localSourceSvc;
    private readonly ILogger<WebHarnessRuntime> _logger;
    private ModelBridgeRegistry? _bridge;

    public IFormatCache FormatCache { get; }

    public WebHarnessRuntime(
        UserLocalSourceService localSourceSvc,
        IFormatCache formatCache,
        ILogger<WebHarnessRuntime> logger)
    {
        _localSourceSvc = localSourceSvc;
        FormatCache     = formatCache;
        _logger         = logger;
    }

    public void SetBridge(ModelBridgeRegistry bridge) => _bridge = bridge;

    public ModelSource? FindSource(string? name, HarnessContext context)
    {
        var userId = context.UserId;
        if (string.IsNullOrEmpty(userId))
            return string.IsNullOrEmpty(name) ? null : PublicProviderCatalog.FirstOrDefault(s => s.Name == name);

        // Non-blocking read of the last-fetched node channels (already includes public providers).
        // The cache is warmed by async callers (NavMenu load, agent start) before a turn runs.
        var local = _localSourceSvc.GetCached(userId)
            .Select(UserLocalSourceService.ToModelSource)
            .ToList();

        if (string.IsNullOrEmpty(name))
            return local.FirstOrDefault() ?? PublicProviderCatalog.FirstOrDefault();

        return local.FirstOrDefault(s => s.Name == name)
            ?? PublicProviderCatalog.FirstOrDefault(s => s.Name == name);
    }

    public Task<string?> GetApiKeyAsync(string providerName, HarnessContext context, CancellationToken ct = default)
    {
        // The server never holds cloud LLM keys. Bridged and public-provider calls are routed
        // through the node's /llm/proxy (see BridgeHttpHandler), which injects the locally-stored
        // key on the node; the server never receives it. Direct local sources need no key here.
        return Task.FromResult<string?>(null);
    }

    public async Task<string?> GetOAuthTokenAsync(string providerName, HarnessContext context, CancellationToken ct = default)
    {
        var userId = context.UserId;
        if (string.IsNullOrEmpty(userId) || _bridge == null) return null;

        try
        {
            var result = await _bridge.SendLocalRestAsync(userId, "GET", $"/oauth/{providerName}/token");
            if (result is null || result.Value.StatusCode != 200 || string.IsNullOrEmpty(result.Value.Body))
                return null;

            using var doc = JsonDocument.Parse(result.Value.Body);
            return doc.RootElement.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch OAuth token from bridge for user {UserId} provider {Provider}", userId, providerName);
            return null;
        }
    }

    public Task<bool> IsBridgeAvailableAsync(HarnessContext context, CancellationToken ct = default)
    {
        var bridgeUserId = context.BridgeUserId ?? context.UserId;
        return Task.FromResult(_bridge != null && !string.IsNullOrEmpty(bridgeUserId) && _bridge.HasBridge(bridgeUserId));
    }

    public Task<IReadOnlyList<string>> GetBridgeNodeIdsAsync(HarnessContext context, CancellationToken ct = default)
    {
        var bridgeUserId = context.BridgeUserId ?? context.UserId;
        if (_bridge == null || string.IsNullOrEmpty(bridgeUserId))
            return Task.FromResult<IReadOnlyList<string>>([]);
        return Task.FromResult<IReadOnlyList<string>>(_bridge.GetNodes(bridgeUserId).Select(n => n.NodeId).ToList());
    }

    public async Task<string> BridgePostAsync(string url, string body, HarnessContext context, CancellationToken ct = default, string? keyRef = null, bool requireKey = false, string? nodeId = null)
    {
        if (_bridge == null) throw new InvalidOperationException("Bridge not connected");
        var bridgeUserId = context.BridgeUserId ?? context.UserId ?? throw new InvalidOperationException("No bridge user id in context");
        var request = new Aria.Shared.BridgeRequest(Guid.NewGuid().ToString("N"), url, body, null, keyRef, requireKey, context.SessionId);
        var sb = new StringBuilder();
        await foreach (var chunk in _bridge.SendRequestAsync(bridgeUserId, request, ct, nodeId))
            sb.Append(chunk);
        return sb.ToString().Trim();
    }

    public IAsyncEnumerable<string> BridgeStreamAsync(string url, string body, HarnessContext context, CancellationToken ct = default, string? keyRef = null, bool requireKey = false, string? nodeId = null)
    {
        if (_bridge == null) throw new InvalidOperationException("Bridge not connected");
        var bridgeUserId = context.BridgeUserId ?? context.UserId ?? throw new InvalidOperationException("No bridge user id in context");
        var request = new Aria.Shared.BridgeRequest(Guid.NewGuid().ToString("N"), url, body, null, keyRef, requireKey, context.SessionId);
        return _bridge.SendRequestAsync(bridgeUserId, request, ct, nodeId);
    }

    /// <summary>
    /// Asks the node to open a cheap connection to the channel's endpoint (POST /llm/probe) so we can
    /// tell a genuinely-unreachable server (LM Studio not running, wrong URL, bad key) apart from a
    /// server that answered but whose format we couldn't classify. Returns <c>Reachable=true</c> when
    /// we can't determine otherwise, so a missing bridge never masquerades as "server down".
    /// </summary>
    public async Task<(bool Reachable, string? Detail)> ProbeReachabilityAsync(
        string userId, string url, string? keyRef, string? nodeId, CancellationToken ct = default)
    {
        if (_bridge == null || string.IsNullOrEmpty(userId)) return (true, null);
        var body = System.Text.Json.JsonSerializer.Serialize(new { url, keyRef });
        var resp = await _bridge.SendLocalRestAsync(userId, "POST", "/llm/probe", body, nodeId);
        if (resp is not { StatusCode: 200, Body: { } b }) return (true, null);   // inconclusive → don't accuse
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(b);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == System.Text.Json.JsonValueKind.True;
            if (ok)
                return (true, root.TryGetProperty("warning", out var w) ? w.GetString() : null);
            return (false, root.TryGetProperty("error", out var e) ? e.GetString() : "the endpoint did not respond");
        }
        catch { return (true, null); }
    }

    public static readonly IReadOnlyList<ModelSource> PublicProviderCatalog = PublicModelSourceCatalog.Providers;
}
