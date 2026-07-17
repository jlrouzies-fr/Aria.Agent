using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aria.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Aria.Web.Services.ModelBridge;

/// <summary>
/// Tracks active bridge connections (daemon direct tunnel only) and routes AI HTTP requests through them.
///
/// Connection map is connectionId → userId (inverted from the naive approach).
/// This means Unregister(connId) only ever removes that specific connection, so
/// when two connections briefly coexist (e.g. auto-reconnect race on server
/// restart), disconnecting one never orphans the other.
/// </summary>
public partial class ModelBridgeRegistry
{
    // connectionId → userId. Drives LLM/cogitation routing.
    private readonly ConcurrentDictionary<string, string> _connToUser = new();
    // userId → (nodeId → live connection). Replaces the old single userId→connId map so multiple
    // bridges (nodes) can be connected for one soul. nodeId is a stable per-connection handle until
    // Phase 3 supplies the bridge's real node id/label/platform.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, NodeConnection>> _nodes = new();
    // connectionId → one-time nonce for daemon challenge-response auth.
    private readonly ConcurrentDictionary<string, byte[]> _daemonChallenges = new();
    // requestId → channel of raw SSE text chunks
    private readonly ConcurrentDictionary<string, Channel<string>> _pending = new();
    // requestId → connectionId (so Unregister can fail in-flight requests when a connection drops)
    private readonly ConcurrentDictionary<string, string> _requestToConn = new();
    // requestId → TCS for fire-and-return local REST calls
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(int StatusCode, string? Body)>> _pendingRest = new();
    // restRequestId → connectionId it was dispatched to (so a completion can only be accepted from the
    // node that received the request — a different socket can't inject a REST response for it).
    private readonly ConcurrentDictionary<string, string> _restToConn = new();

    // Diagnostic counters
    private int _totalRequests;
    private int _totalChunks;

    private IHubContext<ModelBridgeHub>? _hub;

    public void SetHub(IHubContext<ModelBridgeHub> hub) => _hub = hub;

    /// <summary>Fired when a soul is unlinked server-side (bridge wiped its soul).</summary>
    public event Action<string>? SoulUnlinked;
    public void NotifySoulUnlinked(string userId) => SoulUnlinked?.Invoke(userId);

    /// <summary>Fired when a bridge soul registers (or re-registers) with this server.</summary>
    public event Action<string>? SoulRegistered;
    public void NotifySoulRegistered(string userId) => SoulRegistered?.Invoke(userId);

    /// <summary>Fired when a daemon (direct) bridge connects and authenticates. Argument = userId.</summary>
    public event Action<string>? DirectBridgeRegistered;
    /// <summary>Fired when a daemon bridge disconnects. Argument = userId.</summary>
    public event Action<string>? DirectBridgeDisconnected;
    /// <summary>Fired when the set of connected nodes for a user changes (connect/disconnect). Argument = userId.</summary>
    public event Action<string>? NodesChanged;

    public bool HasDirectBridge(string userId) => _nodes.TryGetValue(userId, out var m) && !m.IsEmpty;

    public bool HasBridge(string userId) => HasDirectBridge(userId);

    public bool TryGetUserId(string connectionId, out string userId) => _connToUser.TryGetValue(connectionId, out userId!);

    /// <summary>Stores a one-time nonce for daemon challenge-response. Consumed by TakeDaemonChallenge.</summary>
    public void StoreDaemonChallenge(string connectionId, byte[] nonce) =>
        _daemonChallenges[connectionId] = nonce;

    /// <summary>Removes and returns the stored nonce, or null if none/already consumed.</summary>
    public byte[]? TakeDaemonChallenge(string connectionId)
    {
        _daemonChallenges.TryRemove(connectionId, out var nonce);
        return nonce;
    }

    /// <summary>
    /// Registers an authenticated daemon connection. The daemon is always soul-verified
    /// (signature was checked before this is called) and is preferred over WASM for routing.
    /// Backward-compatible shim: each connection is its own node, keyed by connectionId until
    /// Phase 3 supplies a real node id/label/platform.
    /// </summary>
    public void RegisterDirect(string userId, string connectionId)
        => RegisterNode(userId, connectionId, label: "", platform: "", connectionId);

    /// <summary>Registers (or refreshes) a node connection for a user.</summary>
    public void RegisterNode(string userId, string nodeId, string label, string platform, string connectionId)
    {
        var map = _nodes.GetOrAdd(userId, _ => new ConcurrentDictionary<string, NodeConnection>());
        map[nodeId]               = new NodeConnection(nodeId, label, platform, connectionId, DateTime.UtcNow);
        _connToUser[connectionId] = userId;
        SetSoulVerified($"direct-{userId}", true);
        DirectBridgeRegistered?.Invoke(userId);
        NodesChanged?.Invoke(userId);
    }

    /// <summary>All currently-connected nodes for a user (empty if none).</summary>
    /// <summary>Every connected node across all souls, as (userId, node). Used by the self-identifying
    /// session-code unlock (§12): the pasted code resolves to whichever soul's bridge holds it.</summary>
    public IEnumerable<(string UserId, NodeConnection Node)> AllNodes() =>
        _nodes.SelectMany(u => u.Value.Values.Select(n => (u.Key, n)));

    /// <summary>User ids that currently have two or more connected nodes — the only souls for which
    /// Layer B context-grant replication does anything. Used by the background replicator.</summary>
    public IReadOnlyList<string> UsersWithMultipleNodes() =>
        _nodes.Where(u => u.Value.Count >= 2).Select(u => u.Key).ToList();

    public IReadOnlyCollection<NodeConnection> GetNodes(string userId) =>
        _nodes.TryGetValue(userId, out var m) ? m.Values.ToList() : [];

    public bool TryGetNode(string userId, string nodeId, out NodeConnection node)
    {
        node = default!;
        return _nodes.TryGetValue(userId, out var m) && m.TryGetValue(nodeId, out node!);
    }

    /// <summary>Forcibly drops a node from routing (e.g. after revocation). Mirrors Unregister's
    /// bookkeeping. The SignalR socket may stay open until the bridge notices, but it is no longer
    /// routable and any reconnect is rejected by the allow-list.</summary>
    public void RemoveNode(string userId, string nodeId)
    {
        if (!_nodes.TryGetValue(userId, out var map) || !map.TryRemove(nodeId, out var nc)) return;
        _connToUser.TryRemove(nc.ConnectionId, out _);
        NodesChanged?.Invoke(userId);
        if (map.IsEmpty)
        {
            _nodes.TryRemove(userId, out _);
            SetSoulVerified($"direct-{userId}", false);
            DirectBridgeDisconnected?.Invoke(userId);
        }
    }

    /// <summary>Default node when a request doesn't name one: the most-recently-connected.</summary>
    public NodeConnection? GetDefaultNode(string userId) =>
        _nodes.TryGetValue(userId, out var m) && !m.IsEmpty
            ? m.Values.OrderByDescending(n => n.ConnectedAt).First()
            : null;

    // ── Preferred approval node (which bridge opens seal-approval pages) ───────────────────────
    // With several nodes online, the human is only sitting at one of them. Seal-approval ceremonies
    // must open where that human actually is, not on whichever node happened to connect last. The user
    // pins that node here; both the ceremony /request and its /poll are routed to it (they must hit the
    // same node — the pending approval is held in-memory on the node that received the request).
    // In-memory only: nodes are ephemeral, so a pin that outlived a server restart could point nowhere.
    private readonly ConcurrentDictionary<string, string> _approvalNode = new();

    /// <summary>Fired when the user changes which node hosts seal approvals (arg = userId).</summary>
    public event Action<string>? ApprovalNodeChanged;

    /// <summary>The node the user pinned for seal approvals, or null for "auto" (follow the default).</summary>
    public string? GetPreferredApprovalNode(string userId) =>
        _approvalNode.TryGetValue(userId, out var n) ? n : null;

    /// <summary>Pin (or clear, when null) the node that opens seal-approval pages for this user.</summary>
    public void SetPreferredApprovalNode(string userId, string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) _approvalNode.TryRemove(userId, out _);
        else                              _approvalNode[userId] = nodeId;
        ApprovalNodeChanged?.Invoke(userId);
    }

    /// <summary>The node an approval ceremony should open on: the pinned node if it is still connected,
    /// otherwise the default (most-recent). BOTH the /request and its /poll must target this same node.</summary>
    public string? ResolveApprovalNode(string userId)
    {
        var pinned = GetPreferredApprovalNode(userId);
        if (pinned != null && TryGetNode(userId, pinned, out _)) return pinned;
        return GetDefaultNode(userId)?.NodeId;
    }

    public void Unregister(string connectionId)
    {
        _connToUser.TryRemove(connectionId, out _);

        // Remove the node whose live connection is this connectionId. Soul stays verified while
        // any other node for the user remains connected.
        foreach (var (userId, map) in _nodes)
        {
            var nodeEntry = map.FirstOrDefault(kv => kv.Value.ConnectionId == connectionId);
            if (nodeEntry.Key == null) continue;

            map.TryRemove(nodeEntry.Key, out _);
            NodesChanged?.Invoke(userId);
            if (map.IsEmpty)
            {
                _nodes.TryRemove(userId, out _);
                SetSoulVerified($"direct-{userId}", false);
                DirectBridgeDisconnected?.Invoke(userId);
            }
            break;
        }

        // Fail any in-flight requests that were dispatched to this specific connection.
        foreach (var kv in _requestToConn.Where(kv => kv.Value == connectionId).ToList())
        {
            if (_pending.TryGetValue(kv.Key, out var ch))
                ch.Writer.TryComplete(new Exception($"Bridge connection lost mid-request (connId={connectionId})"));
            _requestToConn.TryRemove(kv.Key, out _);
        }

        // Drop terminal-session ownership bindings for the departed connection so its session ids
        // can't be re-owned by a later socket claiming the same GUID.
        foreach (var kv in _terminalSessionToConn.Where(kv => kv.Value == connectionId).ToList())
            _terminalSessionToConn.TryRemove(kv.Key, out _);
    }

    // ── Soul verification ────────────────────────────────────────────────────────────────────
    // Keyed by "direct-{userId}" for the daemon tunnel.
    // Default = FALSE (no entry → unverified → locked).
    private readonly ConcurrentDictionary<string, bool> _soulVerified = new();

    /// <summary>Fired whenever a session's verification state changes. Argument = the key (e.g. "direct-9").</summary>
    public event Action<string>? SoulStatusChanged;

    public bool SoulVerified(string sessionKey) => _soulVerified.GetValueOrDefault(sessionKey, false);

    /// <summary>True when the daemon bridge for this userId has authenticated.</summary>
    public bool IsSoulVerified(string userId) => SoulVerified($"direct-{userId}");

    public void SetSoulVerified(string sessionKey, bool verified)
    {
        if (_soulVerified.TryGetValue(sessionKey, out var existing) && existing == verified) return;
        _soulVerified[sessionKey] = verified;
        SoulStatusChanged?.Invoke(sessionKey);
    }

    /// <summary>Clears every per-circuit verification entry for a session token (circuit disposed).
    /// Keys are "circuit-{token}-{userId}" (see CircuitAuthService).</summary>
    public void ClearCircuit(string sessionToken)
    {
        var prefix = $"circuit-{sessionToken}-";
        foreach (var key in _soulVerified.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            if (_soulVerified.TryRemove(key, out _))
                SoulStatusChanged?.Invoke(key);
    }

    // Resolves the connection id to route to: the named node, or the default (most-recent) node.
    private string? ResolveConnId(string userId, string? nodeId = null) =>
        nodeId != null
            ? (TryGetNode(userId, nodeId, out var n) ? n.ConnectionId : null)
            : GetDefaultNode(userId)?.ConnectionId;

    /// <summary>
    /// Waits until a daemon bridge for this user is registered, or throws on timeout.
    /// </summary>
    public async Task WaitForBridgeAsync(string userId, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!HasBridge(userId))
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"No bridge connected for user '{userId}' within {timeout.TotalSeconds:0}s. " +
                    "Start the Aria.Bridge daemon to connect.");
            await Task.Delay(200, ct);
        }
    }

}

/// <summary>One connected bridge (node) for a soul. Label/Platform are blank until Phase 3 reports them.</summary>
public sealed record NodeConnection(
    string NodeId, string Label, string Platform, string ConnectionId, DateTime ConnectedAt);
