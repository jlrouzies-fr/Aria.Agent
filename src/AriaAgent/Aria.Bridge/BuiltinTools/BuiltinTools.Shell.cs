using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aria.Bridge.Endpoints;

namespace Aria.Bridge;

public static partial class BuiltinTools
{
    private const int DefaultTimeoutSeconds = 120;
    private const int MaxTimeoutSeconds     = 600;
    private static readonly TimeSpan LauncherTimeout = TimeSpan.FromSeconds(15);

    private static IEnumerable<BridgeToolInfo> ShellToolInfos()
    {
        yield return new("bash_exec",
            IsWindows
                ? "Run a command via cmd.exe. Returns JSON with stdout, stderr and exit_code. For PowerShell use: powershell -Command \"...\""
                : "Run a shell command via /bin/sh -c. Returns JSON with stdout, stderr and exit_code.",
            Js("""
               {"type":"object",
                "properties":{
                  "command":         {"type":"string","description":"Shell command to run."},
                  "working_dir":     {"type":"string","description":"Working directory (absolute path). Defaults to the session cwd — set by a previous bare 'cd <dir>' command — or the first allowed project root."},
                  "timeout_seconds": {"type":"integer","description":"Kill the command if it hasn't exited after this many seconds (default 120, max 600). Applies to normal (non-background) commands only."},
                  "background":      {"type":"boolean","description":"For long-running commands that never exit on their own (dev servers, watchers, etc.) — do NOT put '&' in the command yourself, set this instead. Redirects the command's stdout+stderr to a log file and returns immediately with {pid, log_file}; tail the log with read_file and stop the process with a follow-up bash_exec running 'kill <pid>' (or 'taskkill /PID <pid> /F' on Windows)."}
                },
                "required":["command"]}
               """));
    }

    // Agent-shell cwd persistence. Every bash_exec spawns a fresh /bin/sh -c, so a bare "cd <dir>"
    // would otherwise be lost between calls — we intercept it (mirroring the Quick Exec panel's
    // _sessionCwd) and remember the directory for subsequent calls. No session identifier reaches
    // /tools/call, so this is a single cwd per bridge process.
    private static string? _sessionCwd;

    // Test hook: the agent session cwd is process-wide static state.
    internal static void ResetSessionCwd() => _sessionCwd = null;

    private static async Task<ToolCallResponse> BashExecAsync(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var command  = args.Str("command") ?? throw new ArgumentException("'command' is required");
        var explicitWorkDir = args.Str("working_dir");
        var background = args.Bool("background") ?? false;

        policy?.EnforceCommand(command);

        // Resolve the working directory: explicit arg > remembered session cwd > first allowed
        // project root > user home. Defaulting to an allowed root (rather than wherever the bridge
        // happens to run) keeps relative paths inside the declared scope — RunShellCommandAsync
        // enforces the policy on the final cwd either way, so a narrowed request scope can only
        // ever shrink this, never widen it.
        string workDir;
        if (explicitWorkDir != null)
        {
            workDir = Expand(explicitWorkDir);
            policy?.EnforcePath(workDir);
        }
        else if (_sessionCwd != null && Directory.Exists(_sessionCwd))
        {
            workDir = _sessionCwd;
            policy?.EnforcePath(workDir);
        }
        else if (policy?.AllowedPaths?.FirstOrDefault() is { } firstAllowed)
        {
            workDir = Expand(firstAllowed);
        }
        else
        {
            workDir = Expand("~");
        }

        // cwd persistence: a bare "cd <dir>" updates the session cwd instead of running (the
        // spawned shell would exit immediately and the cd would be lost). The target is validated
        // against the same policy as any other path.
        if (!background && TerminalEndpoints.TryParseCd(command) is { } cdTarget)
        {
            var newDir = TerminalEndpoints.ResolveCdTarget(workDir, cdTarget);
            policy?.EnforcePath(newDir);
            if (!Directory.Exists(newDir))
            {
                return new ToolCallResponse(JsonSerializer.Serialize(new
                {
                    exit_code = 1,
                    stdout    = (string?)null,
                    stderr    = $"cd: no such directory: {cdTarget}",
                }), IsError: true);
            }

            _sessionCwd = newDir;
            return new ToolCallResponse(JsonSerializer.Serialize(new
            {
                exit_code = 0,
                stdout    = (string?)null,
                stderr    = (string?)null,
                cwd       = newDir,
            }), IsError: false);
        }

        if (background) return await BashExecBackgroundAsync(command, workDir);

        var timeoutSeconds = Math.Clamp(args.Int("timeout_seconds") ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds);

        var (stdout, stderr, exitCode, timedOut) =
            await RunShellCommandAsync(command, workDir, timeoutSeconds, policy);

        _sessionCwd = workDir;

        var result = JsonSerializer.Serialize(new
        {
            exit_code = timedOut ? (int?)null : exitCode,
            stdout    = stdout.Length > 0 ? stdout : null,
            stderr    = stderr.Length > 0 ? stderr : null,
            timed_out = timedOut,
        });
        return new ToolCallResponse(result, timedOut || exitCode != 0);
    }

    /// <summary>
    /// Runs a single shell command under the given policy and returns captured stdout/stderr.
    /// Shared between the agent-facing bash_exec tool and the user-facing /terminal/exec endpoint.
    /// </summary>
    internal static async Task<(string stdout, string stderr, int exitCode, bool timedOut)> RunShellCommandAsync(
        string command, string workDir, int timeoutSeconds, SecurityPolicy? policy)
    {
        policy?.EnforceCommand(command);
        policy?.EnforcePath(workDir);

        using var proc = BuildProcess(command, workDir);
        return await RunAsync(proc, TimeSpan.FromSeconds(timeoutSeconds));
    }

    // Runs the command detached from the tool call: output goes to a log file (never to the pipe
    // the tool would otherwise block reading from), and the tool returns as soon as the launcher
    // has forked it — not when the command itself exits. Fixes the classic "model backgrounds a
    // dev server with '&' and the tool call hangs forever" deadlock: a backgrounded child inherits
    // the SAME stdout/stderr pipe the tool captures, so ReadToEndAsync never sees EOF because the
    // still-running child keeps the write end open. Redirecting to a file instead means nothing
    // backgrounded ever touches that pipe.
    private static async Task<ToolCallResponse> BashExecBackgroundAsync(string command, string workDir)
    {
        // Under workDir (not the system temp dir): the model's follow-up read_file on log_file must
        // land inside the turn's AllowedPaths scope, which is rooted at the project — a path under
        // /tmp is outside that scope and would trip the governance approval gate for no reason.
        var logDir = Path.Combine(workDir, ".aria-bg");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"bg-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");

        var launcherCommand = IsWindows
            ? $"$p = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c','{EscapePs(command)} > \"{EscapePs(logPath)}\" 2>&1' -WindowStyle Hidden -PassThru; Write-Output \"BG_PID:$($p.Id)\""
            : $"({command}) > '{logPath}' 2>&1 < /dev/null &\necho BG_PID:$!";

        using var proc = IsWindows
            ? BuildProcessRaw("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", launcherCommand], workDir)
            : BuildProcess(launcherCommand, workDir);

        var (stdout, stderr, exitCode, timedOut) = await RunAsync(proc, LauncherTimeout);

        var pidLine = stdout.Split('\n').FirstOrDefault(l => l.StartsWith("BG_PID:"));
        var pid     = pidLine != null && int.TryParse(pidLine["BG_PID:".Length..].Trim(), out var p) ? p : (int?)null;

        if (timedOut || pid == null)
        {
            var msg = timedOut
                ? "Timed out launching the background command."
                : $"Failed to launch background command (exit {exitCode}): {stderr}";
            return new ToolCallResponse(msg, IsError: true);
        }

        var result = JsonSerializer.Serialize(new
        {
            background = true,
            pid,
            log_file   = logPath,
            note       = "Process started detached. Use read_file on log_file to check its output, " +
                         $"and bash_exec (\"{(IsWindows ? $"taskkill /PID {pid} /F" : $"kill {pid}")}\") to stop it.",
        });
        return new ToolCallResponse(result, IsError: false);
    }

    private static string EscapePs(string s) => s.Replace("'", "''");

    private static Process BuildProcess(string command, string workDir)
    {
        string exe;
        string[] shellArgs;
        if (IsWindows)
        {
            exe       = "cmd.exe";
            shellArgs = ["/c", command];
        }
        else
        {
            exe       = "/bin/sh";
            shellArgs = ["-c", command];
        }
        return BuildProcessRaw(exe, shellArgs, workDir);
    }

    private static Process BuildProcessRaw(string exe, string[] shellArgs, string workDir)
    {
        var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName               = exe,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = workDir,
        };
        foreach (var a in shellArgs) proc.StartInfo.ArgumentList.Add(a);
        return proc;
    }

    // Waits for the process to exit, killing its whole tree if it overruns the timeout. Reading
    // stdout/stderr concurrently with WaitForExitAsync (rather than after it) avoids the classic
    // deadlock from filling the pipe buffer while nothing drains it.
    private static async Task<(string stdout, string stderr, int exitCode, bool timedOut)> RunAsync(
        Process proc, TimeSpan timeout)
    {
        proc.Start();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();

        var timedOut = false;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { proc.Kill(entireProcessTree: true); } catch { /* already exiting/exited */ }
            // Killing releases the pipes' write end, so the reads below can now reach EOF; bound
            // the wait in case some detached grandchild survives the kill and keeps a pipe open.
            try
            {
                using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await proc.WaitForExitAsync(killCts.Token);
            }
            catch (OperationCanceledException) { /* give up waiting further; read with what we have */ }
        }

        string stdout = "", stderr = "";
        try
        {
            var reads = Task.WhenAll(outTask, errTask);
            if (await Task.WhenAny(reads, Task.Delay(TimeSpan.FromSeconds(5))) == reads)
            {
                stdout = outTask.Result;
                stderr = errTask.Result;
            }
        }
        catch { /* process may have been killed mid-write; keep whatever was captured, if anything */ }

        int exitCode;
        try { exitCode = proc.ExitCode; } catch { exitCode = -1; }

        return (stdout, stderr, exitCode, timedOut);
    }
}
