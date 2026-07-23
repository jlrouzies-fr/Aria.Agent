using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
                ? "Run a command via cmd.exe. Returns JSON with stdout, stderr and exit_code. For PowerShell use: powershell -Command \"...\". For dev servers or long-running commands use run_background instead; if a foreground bash_exec overruns timeout_seconds it is automatically converted to a tracked background job instead of being killed."
                : "Run a shell command via /bin/sh -c. Returns JSON with stdout, stderr and exit_code. For dev servers or long-running commands use run_background instead; if a foreground bash_exec overruns timeout_seconds it is automatically converted to a tracked background job instead of being killed.",
            Js("""
               {"type":"object",
                "properties":{
                  "command":         {"type":"string","description":"Shell command to run."},
                  "working_dir":     {"type":"string","description":"Working directory (absolute path). Defaults to the session cwd — set by a previous bare 'cd <dir>' command — or the first allowed project root."},
                  "timeout_seconds": {"type":"integer","description":"If the command hasn't exited after this many seconds (default 120, max 600), it is NOT killed. Instead it keeps running as a tracked background job and the result contains {pid, log_file, converted_to_background: true}. Use process_output to check it and process_kill to stop it. Prefer run_background for commands you already know are long-running."},
                  "background":      {"type":"boolean","description":"For long-running commands that never exit on their own (dev servers, watchers, etc.) — do NOT put '&' in the command yourself, set this instead, or prefer the run_background tool. Redirects the command's stdout+stderr to a log file and returns immediately with {pid, log_file}. The job is tracked: check it with process_list / process_output, wait for readiness with wait_for, and stop it with process_kill."}
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
        var background = args.Bool("background") ?? false;

        policy?.EnforceCommand(command);

        var workDir = ResolveWorkDir(args, policy);

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

        var (stdout, stderr, exitCode, timedOut, convertedPid, logPath) =
            await RunShellCommandWithConversionAsync(command, workDir, timeoutSeconds, policy);

        _sessionCwd = workDir;

        if (timedOut && convertedPid.HasValue)
        {
            var result = JsonSerializer.Serialize(new
            {
                exit_code = (int?)null,
                stdout    = stdout.Length > 0 ? stdout : null,
                stderr    = stderr.Length > 0 ? stderr : null,
                timed_out = true,
                converted_to_background = true,
                pid       = convertedPid.Value,
                log_file  = logPath,
                note      = $"Command exceeded the {timeoutSeconds}s timeout and is STILL RUNNING as background job pid={convertedPid.Value}. " +
                            $"Output continues to {logPath}. Use process_output to check it, process_kill to stop it, " +
                            "and wait_for to detect readiness. Next time, use run_background for long-running commands.",
            });
            return new ToolCallResponse(result, IsError: false);
        }

        var normalResult = JsonSerializer.Serialize(new
        {
            exit_code = timedOut ? (int?)null : exitCode,
            stdout    = stdout.Length > 0 ? stdout : null,
            stderr    = stderr.Length > 0 ? stderr : null,
            timed_out = timedOut,
        });
        return new ToolCallResponse(normalResult, timedOut || exitCode != 0);
    }

    // Resolve the working directory: explicit arg > remembered session cwd > first allowed
    // project root > user home. Defaulting to an allowed root (rather than wherever the bridge
    // happens to run) keeps relative paths inside the declared scope; a narrowed request scope
    // can only ever shrink this, never widen it.
    private static string ResolveWorkDir(Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        if (args.Str("working_dir") is { } explicitWorkDir)
        {
            var d = Expand(explicitWorkDir);
            policy?.EnforcePath(d);
            return d;
        }

        if (_sessionCwd != null && Directory.Exists(_sessionCwd))
        {
            policy?.EnforcePath(_sessionCwd);
            return _sessionCwd;
        }

        if (policy?.AllowedPaths?.FirstOrDefault() is { } firstAllowed)
            return Expand(firstAllowed);

        return Expand("~");
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

    /// <summary>
    /// Runs a foreground shell command. If it exceeds the timeout it is converted to a tracked
    /// background job instead of being killed, so dev servers and other long-running processes
    /// survive and keep logging. Returns any partial output captured up to the timeout point.
    /// </summary>
    internal static async Task<(string stdout, string stderr, int exitCode, bool timedOut, int? convertedPid, string? logPath)>
        RunShellCommandWithConversionAsync(string command, string workDir, int timeoutSeconds, SecurityPolicy? policy)
    {
        policy?.EnforceCommand(command);
        policy?.EnforcePath(workDir);

        var proc = BuildProcess(command, workDir);
        var spool = new OutputSpool();
        proc.Start();

        var outTask = spool.DrainStdoutAsync(proc.StandardOutput);
        var errTask = spool.DrainStderrAsync(proc.StandardError);

        var timedOut = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
        }

        if (!timedOut)
        {
            await Task.WhenAll(outTask, errTask);
            var (stdout, stderr) = spool.Snapshot();
            int exitCode;
            try { exitCode = proc.ExitCode; } catch { exitCode = -1; }
            proc.Dispose();
            return (stdout, stderr, exitCode, false, null, null);
        }

        // The process survived the timeout. Register it as a tracked background job so the model
        // can follow it with process_output / process_kill / wait_for. We are still the parent,
        // so we can drain its pipes and write an exit-code sidecar when it finally exits.
        // On POSIX the registered pid is the shell wrapper; SIGTERM may not reach grandchildren,
        // which is the same honest limit background:true already has. On Windows we keep the
        // cmd.exe pid and process_kill uses Kill(entireProcessTree:true), so cleanup is reliable.
        var logDir = Path.Combine(workDir, ".aria-bg");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"bg-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        var exitPath = logPath + ".exit";

        spool.LogPath = logPath;

        var pid = proc.Id;
        RegisterBackgroundJob(new BackgroundJob
        {
            Pid          = pid,
            Command      = command,
            StartedAtUtc = DateTime.UtcNow,
            LogPath      = logPath,
            ExitCodePath = exitPath,
        });

        // Fire-and-forget continuation: finish draining, persist final combined output, record the
        // exit code, and dispose the process handle. This runs cheaply in the background.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(outTask, errTask);
                await proc.WaitForExitAsync();
                var (stdout, stderr) = spool.Snapshot();
                int exitCode;
                try { exitCode = proc.ExitCode; } catch { exitCode = -1; }
                File.WriteAllText(logPath, stdout + stderr);
                File.WriteAllText(exitPath, exitCode.ToString());
            }
            catch { /* best effort: process may already be gone or log dir removed */ }
            finally { proc.Dispose(); }
        });

        var (partialOut, partialErr) = spool.Snapshot();
        return (partialOut, partialErr, -1, true, pid, logPath);
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

        // Exit-code sidecar (POSIX only): the detached subshell records the command's exit code
        // when it finishes, so process_list can report "exited N" without a waitable handle —
        // the pid alone can't distinguish a clean exit from a kill once we're not the parent.
        var exitPath = logPath + ".exit";

        var launcherCommand = IsWindows
            ? $"$p = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c','{EscapePs(command)} > \"{EscapePs(logPath)}\" 2>&1' -WindowStyle Hidden -PassThru; Write-Output \"BG_PID:$($p.Id)\""
            : $"({command}; echo $? > '{exitPath}') > '{logPath}' 2>&1 < /dev/null &\necho BG_PID:$!";

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

        RegisterBackgroundJob(new BackgroundJob
        {
            Pid          = pid.Value,
            Command      = command,
            StartedAtUtc = DateTime.UtcNow,
            LogPath      = logPath,
            ExitCodePath = exitPath,
        });

        var result = JsonSerializer.Serialize(new
        {
            background = true,
            pid,
            log_file   = logPath,
            note       = "Process started detached and tracked. Use wait_for to detect readiness, " +
                         "process_output to read its log, process_list to see all background jobs, " +
                         "and process_kill to stop it.",
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

    // Concurrent stdout/stderr accumulator used by RunShellCommandWithConversionAsync. While the
    // command is foreground we only buffer; if it gets converted to a background job, the captured
    // snapshot is flushed to the log file and subsequent output is appended live.
    private sealed class OutputSpool
    {
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly object _sync = new();
        private string? _logPath;

        public string LogPath
        {
            set
            {
                lock (_sync)
                {
                    if (_logPath != null) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(value)!);
                    _logPath = value;
                    File.WriteAllText(_logPath, _stdout.ToString() + _stderr.ToString());
                }
            }
        }

        public Task DrainStdoutAsync(StreamReader reader) => DrainAsync(reader, _stdout);
        public Task DrainStderrAsync(StreamReader reader) => DrainAsync(reader, _stderr);

        private async Task DrainAsync(StreamReader reader, StringBuilder buffer)
        {
            var chunk = new char[4096];
            int n;
            while ((n = await reader.ReadAsync(chunk, 0, chunk.Length)) > 0)
            {
                var s = new string(chunk, 0, n);
                lock (_sync)
                {
                    buffer.Append(s);
                    if (_logPath != null) File.AppendAllText(_logPath, s);
                }
            }
        }

        public (string stdout, string stderr) Snapshot()
        {
            lock (_sync) return (_stdout.ToString(), _stderr.ToString());
        }
    }
}
