using Aria.Tools;
using ModelContextProtocol.Client;

namespace Aria.Bridge;

// Thread-safe pool of live MCP client sessions keyed by server config.
// Each entry owns a spawned child process (stdio) or HTTP connection (SSE); processes are reused
// across requests so the handshake only happens once per unique server config.
// A background timer evicts sessions that have been idle for more than 10 minutes.
public sealed class SessionStore : IAsyncDisposable
{
    private readonly Dictionary<string, McpSession> _sessions = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Timer _cleanupTimer;

    public SessionStore()
    {
        // Run idle-session cleanup every 5 minutes.
        _cleanupTimer = new Timer(_ => _ = CleanupAsync(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    // Deterministic cache key from the full config so two requests for the same server reuse one session.
    public static string MakeKey(McpServerConfig config)
    {
        var envPart = config.Environment is { Count: > 0 }
            ? string.Join("|", config.Environment.OrderBy(k => k.Key).Select(kv => $"{kv.Key}={kv.Value}"))
            : "";
        var urlPart = config.Transport == McpTransport.Sse ? $"|SSE|{config.Url}" : "";
        return $"{config.Transport}|{config.Name}|{config.Command}|{string.Join("|", config.Arguments)}|{envPart}{urlPart}";
    }

    // Returns an existing live session or creates a new one.
    // The global lock serialises creation so only one process/connection is spawned per key
    // even under concurrent requests.
    public async Task<McpSession> GetOrCreateAsync(
        McpServerConfig config,
        CancellationToken ct = default)
    {
        var key = MakeKey(config);

        await _lock.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(key, out var existing))
            {
                existing.Touch();
                return existing;
            }

            var session = new McpSession(config);
            await session.InitializeAsync(ct);
            _sessions[key] = session;
            return session;
        }
        finally { _lock.Release(); }
    }

    // Removes a session and terminates its child process / connection.
    // Called by endpoints when a tool call fails — stale/crashed sessions are not reused.
    public async Task RemoveAsync(McpServerConfig config)
    {
        var key = MakeKey(config);
        await _lock.WaitAsync();
        try
        {
            if (_sessions.Remove(key, out var session))
                await session.DisposeAsync();
        }
        finally { _lock.Release(); }
    }

    // Returns a snapshot of active session display names (falling back to the key if unnamed).
    public (string Label, DateTime LastUsed)[] GetAll()
    {
        _lock.Wait();
        try
        {
            return _sessions
                .Select(kv => (kv.Value.Name ?? kv.Key, kv.Value.LastUsed))
                .ToArray();
        }
        finally { _lock.Release(); }
    }

    private async Task CleanupAsync()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        await _lock.WaitAsync();
        try
        {
            var stale = _sessions.Where(kv => kv.Value.LastUsed < cutoff).Select(kv => kv.Key).ToList();
            foreach (var key in stale)
            {
                if (_sessions.Remove(key, out var session))
                    await session.DisposeAsync();
            }
        }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cleanupTimer.Dispose();
        await _lock.WaitAsync();
        try
        {
            foreach (var session in _sessions.Values)
                await session.DisposeAsync();
            _sessions.Clear();
        }
        finally { _lock.Release(); }
    }
}

// Wraps a single McpClient (and its underlying transport) with last-used tracking.
// InitializeAsync must be called once before Client is accessed.
public sealed class McpSession(McpServerConfig config) : IAsyncDisposable
{
    private McpClient? _client;

    public string    Name     { get; set; } = config.Name;
    public DateTime  LastUsed { get; private set; } = DateTime.UtcNow;
    public McpClient Client   => _client ?? throw new InvalidOperationException("Session not initialized");

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (config.Transport == McpTransport.Sse && !string.IsNullOrEmpty(config.Url))
        {
            _client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(config.Url),
                    Name     = config.Name,
                }),
                cancellationToken: ct);
        }
        else
        {
            _client = await McpClient.CreateAsync(
                new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name      = config.Name,
                    Command   = config.Command,
                    Arguments = [.. config.Arguments],
                    // Pass null rather than an empty dict — some SDK versions behave differently.
                    EnvironmentVariables = config.Environment?.Count > 0
                        ? new Dictionary<string, string?>(config.Environment!)
                        : null,
                }),
                cancellationToken: ct);
        }
    }

    public void Touch() => LastUsed = DateTime.UtcNow;

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }
}
