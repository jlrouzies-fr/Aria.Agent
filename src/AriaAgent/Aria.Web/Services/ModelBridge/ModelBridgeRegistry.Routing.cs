using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aria.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Aria.Web.Services.ModelBridge;

public partial class ModelBridgeRegistry
{
    public object GetStatus() => new
    {
        connectedUsers  = _connToUser.Select(kv => new
        {
            connectionId = kv.Key,
            userId       = kv.Value,
            isDirect     = true
        }).ToList(),
        nodesByUser     = _nodes.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Values
                .Select(n => new { n.NodeId, n.Label, n.Platform, n.ConnectedAt })
                .ToList()),
        soulVerifiedBySession = _soulVerified.ToDictionary(kv => kv.Key, kv => kv.Value),
        pendingRequests = _pending.Count,
        totalRequests   = _totalRequests,
        totalChunks     = _totalChunks
    };

    /// <summary>
    /// Sends an AI HTTP request to the user's bridge daemon and streams raw SSE lines back.
    /// </summary>
    public async IAsyncEnumerable<string> SendRequestAsync(
        string userId, BridgeRequest request,
        [EnumeratorCancellation] CancellationToken ct = default,
        string? nodeId = null)
    {
        if (_hub == null) throw new InvalidOperationException("Hub not ready");

        // No named node → wait for any bridge then use the default. A named node must already be
        // connected — don't silently fall back to a different machine.
        if (nodeId == null && !HasBridge(userId))
            await WaitForBridgeAsync(userId, TimeSpan.FromSeconds(30), ct);

        var connId = ResolveConnId(userId, nodeId)
            ?? throw new InvalidOperationException(
                nodeId == null
                    ? $"No bridge connected for user '{userId}'"
                    : $"Node '{nodeId}' is not connected for user '{userId}'");

        Interlocked.Increment(ref _totalRequests);

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            { SingleReader = true, SingleWriter = false });
        _pending[request.RequestId]      = channel;
        _requestToConn[request.RequestId] = connId;

        await _hub.Clients.Client(connId).SendAsync("HandleRequest", request, ct);

        // If the caller cancels (user pressed STOP), tell the bridge to abort the upstream LLM request.
        var requestId = request.RequestId;
        using var abortReg = ct.Register(() =>
        {
            var abortConnId = _requestToConn.TryGetValue(requestId, out var c) ? c : null;
            if (abortConnId != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await _hub.Clients.Client(abortConnId).SendAsync("AbortRequest", requestId); }
                    catch { }
                });
            }
        });

        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
                yield return chunk;
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
            _requestToConn.TryRemove(request.RequestId, out _);
        }
    }

    // Ownership guard: a chunk/completion is only honoured from the same connection the request was
    // dispatched to. Request IDs are GUIDs (already hard to guess), but this removes the trust
    // entirely — another connected socket can't inject into or terminate a stream it doesn't own.
    private bool OwnsRequest(string requestId, string callerConnId) =>
        _requestToConn.TryGetValue(requestId, out var owner) && owner == callerConnId;

    public void WriteChunk(string requestId, string data, string callerConnId)
    {
        if (!OwnsRequest(requestId, callerConnId)) return;
        if (_pending.TryGetValue(requestId, out var ch))
        {
            ch.Writer.TryWrite(data);
            Interlocked.Increment(ref _totalChunks);
        }
    }

    public void Complete(string requestId, bool success, string? error, string callerConnId)
    {
        if (!OwnsRequest(requestId, callerConnId)) return;
        if (_pending.TryGetValue(requestId, out var ch))
            ch.Writer.TryComplete(success || error == null ? null : new Exception(error));
    }

    /// <summary>
    /// Sends a REST request through the daemon bridge for this user and returns the response.
    /// Returns null if no bridge is connected. Times out after 15 s.
    /// </summary>
    public async Task<(int StatusCode, string? Body)?> SendLocalRestAsync(
        string userId, string method, string path, string? body = null, string? nodeId = null,
        int timeoutSeconds = 15, string? sessionId = null)
    {
        var connId = ResolveConnId(userId, nodeId);
        if (_hub == null || connId == null) return null;

        var requestId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<(int, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRest[requestId] = tcs;
        _restToConn[requestId]  = connId;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        cts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            var req = new Aria.Shared.LocalRestRequest(requestId, method, path, body, sessionId);
            await _hub.Clients.Client(connId).SendAsync("HandleLocalRest", req);
            return await tcs.Task;
        }
        catch
        {
            return null;
        }
        finally
        {
            _pendingRest.TryRemove(requestId, out _);
            _restToConn.TryRemove(requestId, out _);
        }
    }

    public void CompleteLocalRest(string requestId, int statusCode, string? body, string callerConnId)
    {
        // Only the node the request was dispatched to may complete it.
        if (!_restToConn.TryGetValue(requestId, out var owner) || owner != callerConnId) return;
        if (_pendingRest.TryGetValue(requestId, out var tcs))
            tcs.TrySetResult((statusCode, body));
    }

    // ── Terminal PTY streaming ───────────────────────────────────────────────────────────────

    // sessionId → the bridge connection that opened the PTY. Bound when the browser registers a
    // session; consulted before a TerminalChunk/TerminalClosed is accepted so another connected
    // socket can't inject into (or close) a PTY stream whose GUID it happened to learn — the same
    // connection-ownership guard the LLM (_requestToConn) and local-REST (_restToConn) paths use.
    private readonly ConcurrentDictionary<string, string> _terminalSessionToConn = new();

    /// <summary>Record which node connection owns a PTY session, so its output can be authenticated.</summary>
    public void BindTerminalSession(string userId, string? nodeId, string sessionId)
    {
        var connId = ResolveConnId(userId, nodeId);
        if (connId != null) _terminalSessionToConn[sessionId] = connId;
        else _terminalSessionToConn.TryRemove(sessionId, out _);
    }

    public void UnbindTerminalSession(string sessionId) => _terminalSessionToConn.TryRemove(sessionId, out _);

    /// <summary>
    /// True if the caller may stream for this PTY session. A *known* session must match the
    /// connection it was opened on; an *unbound* session is allowed through so an open-time race
    /// never silently kills the terminal (the attack this closes needs a known live GUID anyway).
    /// </summary>
    public bool OwnsTerminalSession(string sessionId, string callerConnId) =>
        !_terminalSessionToConn.TryGetValue(sessionId, out var owner) || owner == callerConnId;

    public async Task SendTerminalInputAsync(string userId, string nodeId, string sessionId, string dataBase64)
    {
        var connId = ResolveConnId(userId, nodeId);
        if (_hub == null || connId == null) return;
        await _hub.Clients.Client(connId).SendAsync("TerminalInput", sessionId, dataBase64);
    }

    public async Task SendTerminalResizeAsync(string userId, string nodeId, string sessionId, int cols, int rows)
    {
        var connId = ResolveConnId(userId, nodeId);
        if (_hub == null || connId == null) return;
        await _hub.Clients.Client(connId).SendAsync("TerminalResize", sessionId, cols, rows);
    }

    public async Task SendTerminalCloseAsync(string userId, string nodeId, string sessionId)
    {
        var connId = ResolveConnId(userId, nodeId);
        if (_hub == null || connId == null) return;
        await _hub.Clients.Client(connId).SendAsync("TerminalClose", sessionId);
    }
}
