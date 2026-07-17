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
        => Task.FromResult(false);

    public Task<IReadOnlyList<string>> GetBridgeNodeIdsAsync(HarnessContext context, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string> BridgePostAsync(string url, string body, HarnessContext context, CancellationToken ct = default, string? keyRef = null, bool requireKey = false, string? nodeId = null)
        => throw new NotSupportedException("Fake runtime does not support bridge calls");

    public IAsyncEnumerable<string> BridgeStreamAsync(string url, string body, HarnessContext context, CancellationToken ct = default, string? keyRef = null, bool requireKey = false, string? nodeId = null)
        => throw new NotSupportedException("Fake runtime does not support bridge calls");
}
