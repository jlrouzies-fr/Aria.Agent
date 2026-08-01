using Aria.Agent;
using Aria.Harness.Core;
using Aria.Harness.Formats;

namespace Aria.Tests.Fakes;

/// <summary>
/// Stub runtime for unit-testing the harness without a web host or bridge.
/// </summary>
public sealed class FakeHarnessRuntime : IHarnessRuntime
{
    public IFormatCache FormatCache { get; } = new FakeFormatCache();
    public List<ModelSource> Sources { get; } = new();
    public Dictionary<string, string> ApiKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> OAuthTokens { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true, <see cref="IsBridgeAvailableAsync"/> reports the bridge as up.</summary>
    public bool BridgeAvailable { get; set; }

    /// <summary>
    /// Optional handler for <see cref="BridgePostAsync"/>. When null, bridge posts throw
    /// <see cref="NotSupportedException"/> (legacy stub behaviour).
    /// </summary>
    public Func<string, string, string?, CancellationToken, Task<string>>? BridgePostHandler { get; set; }

    public void AddSource(ModelSource source) => Sources.Add(source);

    public ModelSource? FindSource(string? name, HarnessContext context)
    {
        if (Sources.Count == 0) return null;
        return string.IsNullOrEmpty(name) ? Sources[0] : Sources.FirstOrDefault(s => s.Name == name);
    }

    public Task<string?> GetApiKeyAsync(string providerName, HarnessContext context, CancellationToken ct = default)
        => Task.FromResult(ApiKeys.TryGetValue(providerName, out var key) ? key : null);

    public Task<string?> GetOAuthTokenAsync(string providerName, HarnessContext context, CancellationToken ct = default)
        => Task.FromResult(OAuthTokens.TryGetValue(providerName, out var token) ? token : null);

    public Task<bool> IsBridgeAvailableAsync(HarnessContext context, CancellationToken ct = default)
        => Task.FromResult(BridgeAvailable);

    public Task<IReadOnlyList<string>> GetBridgeNodeIdsAsync(HarnessContext context, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string> BridgePostAsync(string url, string body, HarnessContext context, CancellationToken ct = default, string? keyRef = null, bool requireKey = false, string? nodeId = null)
        => BridgePostHandler is { } handler
            ? handler(url, body, nodeId, ct)
            : throw new NotSupportedException("Fake runtime does not support bridge calls");

    public IAsyncEnumerable<string> BridgeStreamAsync(string url, string body, HarnessContext context, CancellationToken ct = default, string? keyRef = null, bool requireKey = false, string? nodeId = null)
        => throw new NotSupportedException("Fake runtime does not support bridge calls");
}
