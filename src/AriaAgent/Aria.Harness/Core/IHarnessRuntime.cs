using Aria.Agent;
using Aria.Harness.Formats;
using Microsoft.Extensions.AI;

namespace Aria.Harness.Core;

/// <summary>
/// Host-provided capabilities that the harness needs but does not own.
/// Web and Console each implement this differently: Web uses DB + SignalR bridge,
/// Console uses local config + direct HTTP to the bridge (if any).
/// </summary>
public interface IHarnessRuntime
{
    /// <summary>Resolve a model source by name (user sources first, then public catalog).</summary>
    ModelSource? FindSource(string? name, HarnessContext context);

    /// <summary>Get the stored API key for a public cloud provider, if any.</summary>
    Task<string?> GetApiKeyAsync(string providerName, HarnessContext context, CancellationToken ct = default);

    /// <summary>Get a valid OAuth token for the requested provider, if available.</summary>
    Task<string?> GetOAuthTokenAsync(string providerName, HarnessContext context, CancellationToken ct = default);

    /// <summary>True if a bridge is available for this harness context.</summary>
    Task<bool> IsBridgeAvailableAsync(HarnessContext context, CancellationToken ct = default);

    /// <summary>All connected bridge node ids for this context's soul, for cross-node fan-out (memory
    /// recall). Empty when no bridge / single in-process host (Console). Order is host-defined.</summary>
    Task<IReadOnlyList<string>> GetBridgeNodeIdsAsync(HarnessContext context, CancellationToken ct = default);

    /// <summary>Post a non-streaming request to the bridge (tools, health, etc.).</summary>
    Task<string> BridgePostAsync(string url, string body, HarnessContext context, CancellationToken ct = default, string? keyRef = null, bool requireKey = false, string? nodeId = null);

    /// <summary>Stream an LLM request through the bridge.</summary>
    IAsyncEnumerable<string> BridgeStreamAsync(string url, string body, HarnessContext context, CancellationToken ct = default, string? keyRef = null, bool requireKey = false, string? nodeId = null);

    /// <summary>Format cache for the current context.</summary>
    IFormatCache FormatCache { get; }
}
