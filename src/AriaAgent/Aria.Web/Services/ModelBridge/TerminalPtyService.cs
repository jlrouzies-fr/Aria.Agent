using System.Collections.Concurrent;

namespace Aria.Web.Services.ModelBridge;

/// <summary>
/// Server-side dispatcher for PTY terminal sessions. Bridges route chunks from the node to this
/// singleton, which forwards them to the Blazor circuit currently listening for that session id.
/// The reverse path (keystrokes/resize) goes through <see cref="ModelBridgeRegistry"/> to the node.
/// </summary>
public sealed class TerminalPtyService(ModelBridgeRegistry registry, ILogger<TerminalPtyService> logger)
{
    private readonly ConcurrentDictionary<string, SessionRegistration> _sessions = new();

    public void RegisterSession(
        string sessionId,
        string userId,
        string? nodeId,
        Func<byte[], Task> onChunk,
        Action<int?> onClosed)
    {
        _sessions[sessionId] = new SessionRegistration(onChunk, onClosed);
        // Bind the session to the node connection that will host it, so relayed output can be
        // authenticated (a socket that isn't this node can't inject into the stream).
        registry.BindTerminalSession(userId, nodeId, sessionId);
    }

    public void UnregisterSession(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        registry.UnbindTerminalSession(sessionId);
    }

    public async Task DispatchChunkAsync(string sessionId, byte[] data, string callerConnId)
    {
        if (!registry.OwnsTerminalSession(sessionId, callerConnId))
        {
            logger.LogWarning("PTY chunk from non-owning connection dropped for {SessionId}", sessionId);
            return;
        }
        if (_sessions.TryGetValue(sessionId, out var reg))
        {
            try { await reg.OnChunk(data); }
            catch (Exception ex) { logger.LogWarning(ex, "PTY chunk dispatch failed for {SessionId}", sessionId); }
        }
    }

    public async Task DispatchClosedAsync(string sessionId, int? exitCode, string callerConnId)
    {
        if (!registry.OwnsTerminalSession(sessionId, callerConnId))
        {
            logger.LogWarning("PTY close from non-owning connection dropped for {SessionId}", sessionId);
            return;
        }
        if (_sessions.TryRemove(sessionId, out var reg))
        {
            registry.UnbindTerminalSession(sessionId);
            try { reg.OnClosed(exitCode); }
            catch (Exception ex) { logger.LogWarning(ex, "PTY closed dispatch failed for {SessionId}", sessionId); }
        }
    }

    public async Task SendInputAsync(string userId, string nodeId, string sessionId, byte[] data)
    {
        try
        {
            await registry.SendTerminalInputAsync(userId, nodeId, sessionId, Convert.ToBase64String(data));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PTY input forwarding failed for {SessionId}", sessionId);
        }
    }

    public async Task SendResizeAsync(string userId, string nodeId, string sessionId, int cols, int rows)
    {
        try
        {
            await registry.SendTerminalResizeAsync(userId, nodeId, sessionId, cols, rows);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PTY resize forwarding failed for {SessionId}", sessionId);
        }
    }

    public async Task SendCloseAsync(string userId, string nodeId, string sessionId)
    {
        try
        {
            await registry.SendTerminalCloseAsync(userId, nodeId, sessionId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PTY close forwarding failed for {SessionId}", sessionId);
        }
    }

    private sealed record SessionRegistration(
        Func<byte[], Task> OnChunk,
        Action<int?> OnClosed);
}
