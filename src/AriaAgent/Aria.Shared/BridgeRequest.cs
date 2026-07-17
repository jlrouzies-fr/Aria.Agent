namespace Aria.Shared;

/// <summary>
/// Sent from server → WASM bridge when an AI HTTP call needs to happen from the browser.
/// <para>
/// When <see cref="KeyRef"/> is set the request routes to the local bridge's <c>/llm/proxy</c>,
/// which resolves the key and makes the outbound call. For cloud providers
/// (<see cref="RequireKey"/> = true) the bridge 401s if the key is absent; for local/LAN bridged
/// sources (<see cref="RequireKey"/> = false) it continues without auth rather than failing hard.
/// </para>
/// </summary>
public record BridgeRequest(string RequestId, string Url, string Body, string? ApiKey,
    string? KeyRef = null, bool RequireKey = true, string? SessionId = null);

/// <summary>
/// Sent from server → WASM bridge for a non-streaming REST call to the local McpBridge (localhost:5741).
/// WASM makes the HTTP call and returns a <see cref="LocalRestResponse"/> via the hub.
/// <para>
/// <see cref="SessionId"/> (optional) identifies the browser circuit a sensitive request originates
/// from, so the node's Layer B gate can scope a context grant to that session rather than the whole
/// soul. Null (background/maintenance calls, or older servers) falls back to the per-soul grant, so
/// the field is fully backward-compatible.
/// </para>
/// </summary>
public record LocalRestRequest(string RequestId, string Method, string Path, string? Body, string? SessionId = null);
