using System.Collections.Concurrent;
using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Infrastructure;
using Aria.Bridge.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// User-driven terminal panel endpoint. Runs shell commands on the cogitator node outside the
/// agent tool pipeline — no governance, but the same <see cref="SecurityPolicy"/> blocklist and
/// allowed-path enforcement as bash_exec because the input still arrives over the server tunnel.
/// </summary>
public static class TerminalEndpoints
{
    // Per-panel-session working directory persistence. A session survives for the lifetime of the
    // bridge process (no idle eviction in Phase 1 — that belongs to the persistent PTY work).
    private static readonly ConcurrentDictionary<string, string> _sessionCwd = new();

    private const int DefaultTimeoutSeconds = 120;
    private const int MaxTimeoutSeconds     = 600;
    private const int MaxOutputBytes        = 200 * 1024;

    public static void MapTerminalEndpoints(this WebApplication app)
    {
        // Aggregate capability state for this node. Each of the three is independently toggleable.
        app.MapGet("/terminal/capabilities", async (BridgeDbContext db) =>
        {
            var soul = await GetFirstSoulAsync(db);
            var until = soul?.PtyEnabledUntil;
            var ptyOn = until.HasValue && until.Value > DateTime.UtcNow;
            return Results.Ok(new
            {
                projects  = soul?.ProjectsEnabled  ?? false,
                quickExec = soul?.QuickExecEnabled ?? false,
                pty       = ptyOn,
                ptyMinutes = soul?.PtySealMinutes is > 0 ? soul.PtySealMinutes : 10,
                ptyRemainingSeconds = ptyOn ? (int)(until!.Value - DateTime.UtcNow).TotalSeconds : 0,
            });
        });

        // Back-compat: legacy callers equate "terminal enabled" with the user Quick Exec capability.
        app.MapGet("/terminal/enabled", async (BridgeDbContext db) =>
        {
            var soul = await GetFirstSoulAsync(db);
            return Results.Ok(new { enabled = soul?.QuickExecEnabled ?? false });
        });

        // ── Agent Projects capability ───────────────────────────────────────────
        app.MapPost("/terminal/projects-enable", (BridgeDbContext db) => SetCapabilityAsync(db, s => s.ProjectsEnabled = true,  "projects"));
        app.MapPost("/terminal/projects-disable", (BridgeDbContext db) => SetCapabilityAsync(db, s => s.ProjectsEnabled = false, "projects"));

        // ── User Quick Exec capability (also aliased by the legacy enable/disable routes) ──
        app.MapPost("/terminal/quick-exec-enable",  (BridgeDbContext db) => SetCapabilityAsync(db, s => s.QuickExecEnabled = true,  "quickExec"));
        app.MapPost("/terminal/quick-exec-disable", (BridgeDbContext db) => SetCapabilityAsync(db, s => s.QuickExecEnabled = false, "quickExec"));
        app.MapPost("/terminal/enable",  (BridgeDbContext db) => SetCapabilityAsync(db, s => s.QuickExecEnabled = true,  "quickExec"));
        app.MapPost("/terminal/disable", (BridgeDbContext db) => SetCapabilityAsync(db, s => s.QuickExecEnabled = false, "quickExec"));

        // GET /terminal/config — node-side Terminal policy: maximum allowed paths and blocked patterns.
        // The web may only narrow these; the node owns the authoritative scope.
        app.MapGet("/terminal/config", async (BridgeDbContext db) =>
        {
            var soul = await GetFirstSoulAsync(db);
            if (soul == null) return Results.Problem("No soul configured on this bridge");
            return Results.Ok(new
            {
                allowedPaths = soul.GetTerminalAllowedPaths(),
                blockedCommands = soul.GetTerminalBlockedCommands(),
                projects = soul.GetTerminalProjects(),
            });
        });

        // POST /terminal/config — set the node-side Terminal policy.
        app.MapPost("/terminal/config", async (TerminalConfigRequest req, BridgeDbContext db) =>
        {
            var soul = await GetFirstSoulForUpdateAsync(db);
            if (soul == null) return Results.Problem("No soul configured on this bridge");

            var blocked = req.BlockedCommands?
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .ToArray() ?? [];

            string[] allowed;
            if (req.Projects is not null)
            {
                // Rich path: store named projects and derive the authoritative allowed-path set from them.
                var projects = req.Projects
                    .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Path))
                    .Select(p => new BridgeTerminalProject(
                        string.IsNullOrWhiteSpace(p.Name) ? BridgeTerminalProject.DeriveName(p.Path) : p.Name.Trim(),
                        p.Path.Trim(),
                        (p.Description ?? "").Trim()))
                    .ToList();
                soul.TerminalProjectsJson = System.Text.Json.JsonSerializer.Serialize(projects);
                allowed = projects.Select(p => p.Path).ToArray();
            }
            else
            {
                // Legacy path: bare allowed-paths list; keep projects derived from it (drop stored names).
                allowed = req.AllowedPaths?
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .ToArray() ?? [];
                soul.TerminalProjectsJson = null;
            }

            soul.TerminalAllowedPathsJson = System.Text.Json.JsonSerializer.Serialize(allowed);
            soul.TerminalBlockedCommandsJson = System.Text.Json.JsonSerializer.Serialize(blocked);
            await db.SaveChangesAsync();
            return Results.Ok(new { allowedPaths = allowed, blockedCommands = blocked, projects = soul.GetTerminalProjects() });
        });

        // GET /terminal/projects — bridge-authoritative list of Terminal projects.
        // The web may only display these; it cannot edit them. Each project inherits this node's
        // platform and node id so path rendering and routing stay correct.
        app.MapGet("/terminal/projects", async (BridgeDbContext db) =>
        {
            var soul = await GetFirstSoulAsync(db);
            if (soul == null) return Results.Problem("No soul configured on this bridge");

            var platform = GetNodePlatform();
            var nodeId = soul.NodeId;

            var projects = soul.GetTerminalProjects()
                .Select(p => new
                {
                    name = p.Name,
                    path = p.Path,
                    description = p.Description,
                    nodeId,
                    platform,
                })
                .ToArray();

            return Results.Ok(new { projects });
        });

        app.MapPost("/terminal/complete", async (TerminalCompleteRequest req, BridgeDbContext db) =>
        {
            var soul = await GetFirstSoulAsync(db);
            if (soul is not { QuickExecEnabled: true })
                return Results.Json(new { error = QuickExecNotEnabledMessage }, statusCode: 403);

            var sessionId = string.IsNullOrWhiteSpace(req.SessionId)
                ? "default"
                : req.SessionId;

            var cwd = req.Cwd;
            if (string.IsNullOrWhiteSpace(cwd))
                _sessionCwd.TryGetValue(sessionId, out cwd);
            if (string.IsNullOrWhiteSpace(cwd))
                cwd = "~";

            var policy = SecurityPolicy.FromNodeAndRequest(
                nodeAllowedPaths: soul.GetTerminalAllowedPaths(),
                requestAllowedPaths: req.AllowedPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray());

            try
            {
                var result = TerminalCompletion.Complete(req.Line, req.Cursor, cwd, policy);
                return Results.Ok(result);
            }
            catch (TerminalSecurityException ex)
            {
                return Results.Json(new { error = ex.Message, blocked = true }, statusCode: 403);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Terminal completion failed: {ex.Message}");
            }
        });

        app.MapPost("/terminal/exec", async (TerminalExecRequest req, BridgeDbContext db, SecurityAuditLog audit) =>
        {
            var soul = await GetFirstSoulAsync(db);
            if (soul is not { QuickExecEnabled: true })
            {
                audit.Record("terminal", "exec-denied", allowed: false, capability: "terminal_quick_exec",
                    detail: $"command={req.Command}");
                return Results.Json(new { error = QuickExecNotEnabledMessage }, statusCode: 403);
            }

            if (string.IsNullOrWhiteSpace(req.Command))
                return Results.BadRequest(new { error = "command is required" });

            var sessionId = string.IsNullOrWhiteSpace(req.SessionId)
                ? "default"
                : req.SessionId;

            var policy = SecurityPolicy.FromNodeAndRequest(
                nodeAllowedPaths:     soul.GetTerminalAllowedPaths(),
                requestAllowedPaths:  req.AllowedPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray(),
                nodeBlockedCommands:  soul.GetTerminalBlockedCommands(),
                requestBlockedCommands: req.BlockedCommands?.Where(c => !string.IsNullOrWhiteSpace(c)).ToArray());

            // Resolve working directory: explicit request > previously tracked session cwd > home.
            var cwd = req.Cwd;
            if (string.IsNullOrWhiteSpace(cwd))
                _sessionCwd.TryGetValue(sessionId, out cwd);
            if (string.IsNullOrWhiteSpace(cwd))
                cwd = "~";

            var workDir = BuiltinTools.Expand(cwd);

            var timeoutSeconds = req.TimeoutSeconds.HasValue
                ? Math.Clamp(req.TimeoutSeconds.Value, 1, MaxTimeoutSeconds)
                : DefaultTimeoutSeconds;

            var auditDetail = $"command={req.Command} cwd={workDir}";
            try
            {
                string stdout, stderr;
                int exitCode;
                bool timedOut;

                // Phase 1 cwd persistence: intercept a bare "cd <dir>" so the panel feels like a shell
                // even though each non-cd command runs in a fresh /bin/sh -c process.
                var cdTarget = TryParseCd(req.Command);
                if (cdTarget != null)
                {
                    var newDir = ResolveCdTarget(workDir, cdTarget);
                    policy.EnforcePath(newDir);
                    if (!Directory.Exists(newDir))
                    {
                        audit.Record("terminal", "exec-blocked", allowed: false, capability: "terminal",
                            detail: $"{auditDetail} reason=path-not-found");
                        return Results.Ok(new TerminalExecResponse
                        {
                            ExitCode = 1,
                            Stderr   = $"cd: no such directory: {cdTarget}",
                            Cwd      = workDir,
                        });
                    }

                    _sessionCwd[sessionId] = newDir;
                    audit.Record("terminal", "exec-cd", allowed: true, capability: "terminal",
                        detail: auditDetail);
                    return Results.Ok(new TerminalExecResponse
                    {
                        ExitCode = 0,
                        Stdout   = "",
                        Stderr   = "",
                        Cwd      = newDir,
                    });
                }

                // Fail helpfully: truly interactive programs need a real PTY. Running them in the
                // quick-exec sandbox would hang or render garbage, so short-circuit with a clear hint.
                if (IsInteractiveProgram(req.Command))
                {
                    audit.Record("terminal", "exec-interactive-hint", allowed: true, capability: "terminal",
                        detail: auditDetail);
                    return Results.Ok(new TerminalExecResponse
                    {
                        ExitCode = 1,
                        Stderr   = "// interactive program — switch the terminal to PTY mode",
                        Cwd      = workDir,
                    });
                }

                (stdout, stderr, exitCode, timedOut) =
                    await BuiltinTools.RunShellCommandAsync(req.Command, workDir, timeoutSeconds, policy);

                // Cap total output so the UI and the bridge→web JSON round-trip stay bounded.
                var total = stdout.Length + stderr.Length;
                if (total > MaxOutputBytes)
                {
                    var half = MaxOutputBytes / 2;
                    stdout = stdout.Length > half ? stdout[..half] + "\n… (truncated)" : stdout;
                    stderr = stderr.Length > half ? stderr[..half] + "\n… (truncated)" : stderr;
                }

                if (timedOut)
                    stderr = string.IsNullOrEmpty(stderr) ? "⏱ TIMED OUT" : "⏱ TIMED OUT\n" + stderr;

                _sessionCwd[sessionId] = workDir;

                audit.Record("terminal", "exec", allowed: true, capability: "terminal",
                    detail: $"{auditDetail} exit={exitCode} timedOut={timedOut}");
                return Results.Ok(new TerminalExecResponse
                {
                    ExitCode = timedOut ? null : exitCode,
                    Stdout   = stdout,
                    Stderr   = stderr,
                    TimedOut = timedOut,
                    Cwd      = workDir,
                });
            }
            catch (TerminalSecurityException ex)
            {
                audit.Record("terminal", "exec-blocked", allowed: false, capability: "terminal",
                    detail: $"{auditDetail} reason={ex.Message}");
                return Results.Json(new { error = ex.Message, blocked = true }, statusCode: 403);
            }
            catch (Exception ex)
            {
                audit.Record("terminal", "exec-error", allowed: false, capability: "terminal",
                    detail: $"{auditDetail} error={ex.Message}");
                return Results.Problem($"Terminal execution failed: {ex.Message}");
            }
        });

        app.MapGet("/terminal/pty-enabled", async (BridgeDbContext db) =>
        {
            var soul = await GetFirstSoulAsync(db);
            var minutes = soul?.PtySealMinutes is > 0 ? soul.PtySealMinutes : 10;
            var until   = soul?.PtyEnabledUntil;
            var enabled = until.HasValue && until.Value > DateTime.UtcNow;
            var remainingSeconds = enabled ? (int)(until!.Value - DateTime.UtcNow).TotalSeconds : 0;
            return Results.Ok(new
            {
                enabled,
                minutes,
                expiresAt = enabled ? until : null,
                remainingSeconds,
            });
        });

        app.MapPost("/terminal/pty-enable", async (PtyEnableRequest req, BridgeDbContext db, PtySessionStore ptyStore, SecurityAuditLog audit) =>
        {
            // PTY is its own capability, gated solely by an approved Inquisitorial Seal below —
            // independent of the Projects and Quick Exec toggles.
            // The server must have already driven and verified the seal; here we consume the
            // locally-stored approved seal bound to the terminal_pty capability and start the
            // time-limited node-local PTY grant. Consumed seals cannot be replayed.
            if (string.IsNullOrWhiteSpace(req.SealId))
                return Results.BadRequest(new { error = "sealId is required" });

            var approved = SealEndpoints.TryConsumeSeal(req.SealId, "terminal_pty");
            if (!approved)
            {
                audit.Record("terminal", "pty-enable-denied", allowed: false, capability: "terminal_pty",
                    detail: $"sealId={req.SealId[..Math.Min(req.SealId.Length, 8)]}");
                return Results.Json(new { error = "seal not approved or not for terminal_pty" }, statusCode: 403);
            }

            var soul = await GetFirstSoulForUpdateAsync(db);
            if (soul == null)
                return Results.Problem("No soul configured on this bridge");

            var minutes = soul.PtySealMinutes is > 0 ? soul.PtySealMinutes : 10;
            var until   = DateTime.UtcNow.AddMinutes(minutes);
            soul.PtyEnabled      = true;
            soul.PtyEnabledUntil = until;
            await db.SaveChangesAsync();
            await ptyStore.SetGrantAsync(until);
            audit.Record("terminal", "pty-enabled", allowed: true, capability: "terminal_pty",
                detail: $"expiresAt={until:O}");
            return Results.Ok(new { enabled = true, minutes, expiresAt = until });
        });

        app.MapPost("/terminal/pty-revoke", async (BridgeDbContext db, PtySessionStore ptyStore, SecurityAuditLog audit) =>
        {
            var soul = await GetFirstSoulForUpdateAsync(db);
            if (soul == null) return Results.Problem("No soul configured on this bridge");
            soul.PtyEnabled      = false;
            soul.PtyEnabledUntil = null;
            await db.SaveChangesAsync();
            // Kill any live shell immediately — revoke must stop the terminal at once.
            await ptyStore.SetGrantAsync(null);
            audit.Record("terminal", "pty-revoked", allowed: true, capability: "terminal_pty");
            return Results.Ok(new { enabled = false });
        });

        // Node-side control of the seal grant lifetime. Default 10 minutes; clamped to [1, 1440].
        app.MapPost("/terminal/pty-duration", async (PtyDurationRequest req, BridgeDbContext db) =>
        {
            var soul = await GetFirstSoulForUpdateAsync(db);
            if (soul == null) return Results.Problem("No soul configured on this bridge");
            soul.PtySealMinutes = Math.Clamp(req.Minutes, 1, 1440);
            await db.SaveChangesAsync();
            return Results.Ok(new { minutes = soul.PtySealMinutes });
        });

        app.MapPost("/terminal/pty", async (PtySessionRequest req, PtySessionStore ptyStore, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.SessionId))
                return Results.BadRequest(new { error = "sessionId is required" });

            // Never spawn a shell without a currently-valid seal grant. Sync the store's in-memory
            // grant window from the DB (authoritative, survives restarts) before deciding.
            var soul  = await GetFirstSoulAsync(db);
            var until = soul?.PtyEnabledUntil;
            if (!(until.HasValue && until.Value > DateTime.UtcNow))
                return Results.Json(new { error = "pty grant not active" }, statusCode: 403);
            await ptyStore.SetGrantAsync(until);

            var cwd = string.IsNullOrWhiteSpace(req.Cwd) ? "~" : req.Cwd;
            var cols = req.Cols is > 0 and <= 512 ? req.Cols.Value : 80;
            var rows = req.Rows is > 0 and <= 128 ? req.Rows.Value : 24;

            var ok = await ptyStore.EnsureAsync(req.SessionId, cwd, cols, rows);
            return ok
                ? Results.Ok(new { started = true })
                : Results.Problem("PTY session could not be started");
        });
    }

    private static async Task<BridgeSoul?> GetFirstSoulAsync(BridgeDbContext db)
    {
        var soul = await db.Souls.AsNoTracking().FirstOrDefaultAsync(x => x.Name != "");
        if (soul != null) return soul;
        return await db.Souls.AsNoTracking().FirstOrDefaultAsync();
    }

    private static async Task<BridgeSoul?> GetFirstSoulForUpdateAsync(BridgeDbContext db)
    {
        var soul = await db.Souls.FirstOrDefaultAsync(x => x.Name != "");
        if (soul != null) return soul;
        return await db.Souls.FirstOrDefaultAsync();
    }

    // Flip one capability flag on the node's soul and report the resulting three-way state.
    private static async Task<IResult> SetCapabilityAsync(BridgeDbContext db, Action<BridgeSoul> mutate, string which)
    {
        var soul = await GetFirstSoulForUpdateAsync(db);
        if (soul == null) return Results.Problem("No soul configured on this bridge");
        mutate(soul);
        await db.SaveChangesAsync();
        return Results.Ok(new { projects = soul.ProjectsEnabled, quickExec = soul.QuickExecEnabled, changed = which });
    }

    private const string QuickExecNotEnabledMessage = "Quick Exec not enabled on this bridge. Open http://localhost:5741 → Terminal / Projects and enable Quick Exec.";

    private static string GetNodePlatform() =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsLinux() ? "Linux"
        : "Unknown";

    /// <summary>
    /// Detects commands that are almost certainly interactive TUI programs. These need a real PTY;
    /// running them through quick exec would hang, corrupt scrollback, or silently fail.
    /// </summary>
    private static bool IsInteractiveProgram(string command)
    {
        var trimmed = command.TrimStart();
        var firstToken = trimmed.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstToken)) return false;

        // Bare executable names that are interactive TUIs.
        var interactive = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "vim", "vi", "nvim", "nano", "pico", "emacs",
            "less", "more", "most",
            "top", "htop", "btm", "glances",
            "claude",
        };
        if (interactive.Contains(firstToken)) return true;

        // ssh with no remote command argument is interactive; ssh "host command" is not.
        if (firstToken.Equals("ssh", StringComparison.OrdinalIgnoreCase))
        {
            var args = trimmed[3..].Trim();
            // Quick heuristic: if there's no command separator and the only args look like host/options,
            // treat as interactive. This is intentionally conservative.
            if (string.IsNullOrWhiteSpace(args)) return true;
            var tokens = args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 1) return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the directory argument if <paramref name="command"/> is a bare "cd &lt;dir&gt;" (no
    /// chaining operators), otherwise null. Intentionally simple — compound commands like
    /// "cd foo && bar" run in a subshell and don't persist cwd, which matches Phase 1 semantics.
    /// </summary>
    private static string? TryParseCd(string command)
    {
        var trimmed = command.TrimStart();
        if (!trimmed.StartsWith("cd ", StringComparison.OrdinalIgnoreCase)) return null;

        // Reject anything that chains commands — we only want a plain cd.
        var arg = trimmed[3..].Trim();
        if (string.IsNullOrEmpty(arg)) return null;
        if (arg.IndexOfAny([';', '&', '|', '\n', '\r']) >= 0) return null;
        return arg;
    }

    private static string ResolveCdTarget(string currentDir, string target)
    {
        if (target == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (target.StartsWith("~/") || target.StartsWith("~\\"))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), target[2..]);
        if (Path.IsPathRooted(target))
            return Path.GetFullPath(target);
        return Path.GetFullPath(Path.Combine(currentDir, target));
    }
}

public record TerminalExecRequest(
    string Command,
    string? Cwd = null,
    string? SessionId = null,
    string[]? AllowedPaths = null,
    string[]? BlockedCommands = null,
    int? TimeoutSeconds = null);

public record TerminalConfigRequest(string[]? AllowedPaths, string[]? BlockedCommands, TerminalProjectInput[]? Projects = null);
public record TerminalProjectInput(string? Name, string Path, string? Description);

public record PtyEnableRequest(string SealId);

public record PtyDurationRequest(int Minutes);

public record PtySessionRequest(
    string SessionId,
    string? Cwd = null,
    int? Cols = null,
    int? Rows = null);

public class TerminalExecResponse
{
    public int?    ExitCode { get; set; }
    public string  Stdout   { get; set; } = "";
    public string  Stderr   { get; set; } = "";
    public bool    TimedOut { get; set; }
    public string  Cwd      { get; set; } = "";
}
