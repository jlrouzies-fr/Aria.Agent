using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Aria.Bridge.Services.Logging;
using Microsoft.AspNetCore.SignalR.Client;
using Porta.Pty;

namespace Aria.Bridge.Infrastructure;

/// <summary>
/// Manages persistent pseudo-terminal sessions for the chat shared-terminal panel.
/// One shell process per session; output is streamed over the direct tunnel back to the web panel.
/// </summary>
public sealed class PtySessionStore : IDisposable
{
    private readonly ConcurrentDictionary<string, PtySession> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(10);
    // Runs often enough that a lapsed seal grant kills the live shell within seconds, not minutes.
    private readonly TimeSpan _cleanupPeriod = TimeSpan.FromSeconds(15);

    private HubConnection? _hub;

    // The seal grant window. Live shells are killed once the grant is revoked (null) or lapses.
    // In-memory only: PTY sessions never survive a bridge restart, so this need not be persisted.
    private DateTime? _grantExpiresUtc;

    public PtySessionStore()
    {
        _cleanupTimer = new Timer(_ => _ = CleanupAsync(), null, _cleanupPeriod, _cleanupPeriod);
    }

    public void SetHub(HubConnection hub) => _hub = hub;

    /// <summary>Sets the current seal-grant expiry. Pass null to revoke — this also kills every live
    /// session immediately so a revoked terminal stops accepting input at once.</summary>
    public async Task SetGrantAsync(DateTime? expiresUtc)
    {
        _grantExpiresUtc = expiresUtc;
        if (!IsGrantValid())
            await KillAllAsync();
    }

    private bool IsGrantValid() => _grantExpiresUtc.HasValue && _grantExpiresUtc.Value > DateTime.UtcNow;

    /// <summary>
    /// Creates or reuses a PTY session, spawning the user's login shell if necessary.
    /// </summary>
    public async Task<bool> EnsureAsync(string sessionId, string cwd, int cols, int rows, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            existing.Touch();
            return true;
        }

        try
        {
            var shell = ResolveShell();
            var workDir = ResolveWorkingDirectory(cwd);

            var options = new PtyOptions
            {
                Name = $"aria-terminal-{sessionId[..Math.Min(8, sessionId.Length)]}",
                Cols = Math.Clamp(cols, 10, 512),
                Rows = Math.Clamp(rows, 5, 128),
                Cwd  = workDir,
                App  = shell,
                Environment = new Dictionary<string, string>
                {
                    ["TERM"] = "xterm-256color",
                }
            };

            var conn = await PtyProvider.SpawnAsync(options, ct);

            var session = new PtySession(sessionId, conn, workDir, SendChunkAsync, SendClosedAsync);
            _sessions[sessionId] = session;
            _ = session.RunReadLoopAsync();
            return true;
        }
        catch (Exception ex)
        {
            BridgeLogger.Log("ERROR", $"[PTY] Failed to spawn session {sessionId[..Math.Min(8, sessionId.Length)]}: {ex.Message}");
            return false;
        }
    }

    public async Task WriteAsync(string sessionId, byte[] data, CancellationToken ct = default)
    {
        // A revoked/expired grant must stop accepting keystrokes even before the reaper ticks.
        if (!IsGrantValid())
        {
            await KillAsync(sessionId);
            return;
        }
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        await session.WriteAsync(data, ct);
    }

    public async Task ResizeAsync(string sessionId, int cols, int rows)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        session.Resize(cols, rows);
    }

    public async Task KillAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
            await session.DisposeAsync();
    }

    public async Task KillAllAsync()
    {
        foreach (var key in _sessions.Keys)
            await KillAsync(key);
    }

    public async ValueTask DisposeAsync()
    {
        await KillAllAsync();
        _cleanupTimer.Dispose();
    }

    public void Dispose()
    {
        _ = DisposeAsync();
    }

    private async Task SendChunkAsync(string sessionId, byte[] data)
    {
        var hub = _hub;
        if (hub?.State == HubConnectionState.Connected)
        {
            try { await hub.SendAsync("TerminalChunk", sessionId, Convert.ToBase64String(data)); }
            catch (Exception ex) { BridgeLogger.Log("WARN", $"[PTY] Send chunk failed: {ex.Message}"); }
        }
    }

    private async Task SendClosedAsync(string sessionId, int? exitCode)
    {
        var hub = _hub;
        if (hub?.State == HubConnectionState.Connected)
        {
            try { await hub.SendAsync("TerminalClosed", sessionId, exitCode); }
            catch (Exception ex) { BridgeLogger.Log("WARN", $"[PTY] Send closed failed: {ex.Message}"); }
        }
    }

    private async Task CleanupAsync()
    {
        // Seal grant lapsed since the last tick — tear down every live shell.
        if (_sessions.Count > 0 && !IsGrantValid())
        {
            BridgeLogger.Log("INFO", "[PTY] Seal grant lapsed — killing all sessions");
            await KillAllAsync();
            return;
        }

        var cutoff = DateTime.UtcNow - _idleTimeout;
        var stale = _sessions.Where(kv => kv.Value.LastUsed < cutoff).Select(kv => kv.Key).ToList();
        foreach (var id in stale)
        {
            BridgeLogger.Log("INFO", $"[PTY] Evicting idle session {id[..Math.Min(8, id.Length)]}");
            await KillAsync(id);
        }
    }

    private static string ResolveShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var pwsh = FindProgram("pwsh.exe") ?? Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(pwsh)) return pwsh;
            return Path.Combine(Environment.SystemDirectory, "cmd.exe");
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(shell) && File.Exists(shell)) return shell;
        return "/bin/bash";
    }

    private static string? FindProgram(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static string ResolveWorkingDirectory(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || cwd == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (cwd.StartsWith("~/") || cwd.StartsWith("~\\"))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cwd[2..]);
        if (Directory.Exists(cwd)) return cwd;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>
    /// One PTY session with output ring buffer and background reader.
    /// </summary>
    private sealed class PtySession : IAsyncDisposable
    {
        private readonly IPtyConnection _conn;
        private readonly Func<string, byte[], Task> _onChunk;
        private readonly Func<string, int?, Task> _onClosed;
        private readonly CancellationTokenSource _cts = new();

        public string SessionId { get; }
        public string WorkingDirectory { get; }
        public DateTime LastUsed { get; private set; } = DateTime.UtcNow;

        // Tiny ring buffer for reconnect replay (not implemented in phase 1; reserved for phase 2 polish).
        public byte[] ReplayBuffer => [];

        public PtySession(string sessionId, IPtyConnection conn, string workingDirectory,
            Func<string, byte[], Task> onChunk, Func<string, int?, Task> onClosed)
        {
            SessionId       = sessionId;
            _conn           = conn;
            WorkingDirectory = workingDirectory;
            _onChunk        = onChunk;
            _onClosed       = onClosed;
        }

        public void Touch() => LastUsed = DateTime.UtcNow;

        public void Resize(int cols, int rows)
        {
            try { _conn.Resize(Math.Clamp(cols, 10, 512), Math.Clamp(rows, 5, 128)); }
            catch (Exception ex) { BridgeLogger.Log("WARN", $"[PTY] Resize failed: {ex.Message}"); }
        }

        public async Task WriteAsync(byte[] data, CancellationToken ct)
        {
            try
            {
                await _conn.WriterStream.WriteAsync(data.AsMemory(0, data.Length), ct);
                await _conn.WriterStream.FlushAsync(ct);
                Touch();
            }
            catch (Exception ex)
            {
                BridgeLogger.Log("WARN", $"[PTY] Write failed: {ex.Message}");
            }
        }

        public async Task RunReadLoopAsync()
        {
            try
            {
                var buffer = new byte[8192];
                while (!_cts.Token.IsCancellationRequested)
                {
                    var read = await _conn.ReaderStream.ReadAsync(buffer.AsMemory(0, buffer.Length), _cts.Token);
                    if (read <= 0) break;
                    Touch();
                    var chunk = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                    await _onChunk(SessionId, chunk);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                BridgeLogger.Log("WARN", $"[PTY] Read loop ended: {ex.Message}");
            }
            finally
            {
                await _onClosed(SessionId, _conn.ExitCode);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { _cts.Cancel(); } catch { }
            try { _conn.Kill(); } catch { }
            try { _conn.Dispose(); } catch { }
            _cts.Dispose();
        }
    }
}
