using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Aria.Bridge;

public static partial class BuiltinTools
{
    private const int ProcessOutputDefaultTailLines = 200;
    private const int ProcessOutputMaxReadBytes = 256 * 1024;
    private const int MaxTrackedBackgroundJobs = 200;
    private static readonly TimeSpan KillGracePeriod = TimeSpan.FromSeconds(3);

    // Registry of background jobs launched via bash_exec background:true. In-memory only: jobs are
    // registered at launch time and the registry is the safety boundary for process_kill — a pid
    // that isn't here can never be signaled, no matter what the model asks for. Status is resolved
    // lazily from the OS plus the exit-code sidecar the launcher wrapper writes when the command
    // finishes (a detached child can't be WaitForExit'd by us, and pid liveness alone can't tell
    // a clean exit from a kill).
    private static readonly ConcurrentDictionary<int, BackgroundJob> BackgroundJobs = new();

    internal sealed class BackgroundJob
    {
        public required int      Pid          { get; init; }
        public required string   Command      { get; init; }
        public required DateTime StartedAtUtc { get; init; }
        public required string   LogPath      { get; init; }
        public required string   ExitCodePath { get; init; }
        public bool Stopped { get; set; }     // set by process_kill
    }

    // Test hook: the registry is process-wide static state.
    internal static void ResetBackgroundJobs() => BackgroundJobs.Clear();

    private static IEnumerable<BridgeToolInfo> ProcessToolInfos()
    {
        yield return new("process_list",
            "List background jobs started via bash_exec(background:true) in this bridge session: pid, command, started_at, status (running/exited/stopped), exit_code when known, and log_file.",
            Js("""
               {"type":"object",
                "properties":{}}
               """));

        yield return new("process_output",
            "Read the log of a background job started via bash_exec(background:true). Returns the job's current status plus the last tail_lines of its log file.",
            Js("""
               {"type":"object",
                "properties":{
                  "pid":        {"type":"integer","description":"Pid of the background job (see process_list). Only jobs started via bash_exec(background:true) are tracked."},
                  "tail_lines": {"type":"integer","description":"Number of log lines to return from the end (default 200)."}
                },
                "required":["pid"]}
               """));

        yield return new("process_kill",
            "Stop a background job started via bash_exec(background:true). Sends SIGTERM, then SIGKILL after a short grace period if it is still running. Refuses any pid that is not a tracked background job.",
            Js("""
               {"type":"object",
                "properties":{
                  "pid": {"type":"integer","description":"Pid of the background job to stop (see process_list). Only jobs started via bash_exec(background:true) can be killed."}
                },
                "required":["pid"]}
               """));
    }

    private static void RegisterBackgroundJob(BackgroundJob job)
    {
        BackgroundJobs[job.Pid] = job;

        // Bound the registry: evict the oldest finished jobs once it grows past the cap.
        if (BackgroundJobs.Count > MaxTrackedBackgroundJobs)
        {
            foreach (var old in BackgroundJobs.Values
                         .OrderBy(j => JobStatus(j).Status == "running" ? 1 : 0)
                         .ThenBy(j => j.StartedAtUtc)
                         .Take(BackgroundJobs.Count - MaxTrackedBackgroundJobs)
                         .Select(j => j.Pid)
                         .ToList())
                BackgroundJobs.TryRemove(old, out _);
        }
    }

    private static (string Status, int? ExitCode) JobStatus(BackgroundJob job)
    {
        // The exit-code sidecar is the authoritative "exited" signal: the launcher wrapper writes
        // it only when the command itself finishes, so a clean exit is distinguishable from a
        // still-running process even after pid reuse windows and without a waitable handle.
        if (File.Exists(job.ExitCodePath) &&
            int.TryParse(File.ReadAllText(job.ExitCodePath).Trim(), out var exitCode))
            return (job.Stopped ? "stopped" : "exited", exitCode);

        if (IsPidRunning(job.Pid))
            return ("running", null);

        // No sidecar (killed, or launched before the wrapper existed) — exit code unknown.
        return (job.Stopped ? "stopped" : "exited", null);
    }

    private static bool IsPidRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; } // no such process (or no permission) → treat as gone
    }

    private static ToolCallResponse ProcessList()
    {
        var jobs = BackgroundJobs.Values
            .OrderByDescending(j => j.StartedAtUtc)
            .Select(j =>
            {
                var (status, exitCode) = JobStatus(j);
                return new
                {
                    pid        = j.Pid,
                    command    = j.Command,
                    started_at = j.StartedAtUtc,
                    status,
                    exit_code  = exitCode,
                    log_file   = j.LogPath,
                };
            })
            .ToArray();

        return new ToolCallResponse(JsonSerializer.Serialize(jobs), false);
    }

    private static ToolCallResponse ProcessOutput(Dictionary<string, JsonElement> args)
    {
        var pid = args.Int("pid") ?? throw new ArgumentException("'pid' is required");
        var tailLines = Math.Clamp(args.Int("tail_lines") ?? ProcessOutputDefaultTailLines, 1, 5000);

        if (!BackgroundJobs.TryGetValue(pid, out var job))
            return Err($"Unknown pid {pid}: process_output only tracks background jobs started via bash_exec(background:true) in this bridge session. Use process_list to see them.");

        var (status, exitCode) = JobStatus(job);

        string output;
        var truncated = false;
        if (File.Exists(job.LogPath))
        {
            output = ReadLogTail(job.LogPath, tailLines, out truncated);
        }
        else
        {
            output = "";
        }

        return new ToolCallResponse(JsonSerializer.Serialize(new
        {
            pid,
            status,
            exit_code = exitCode,
            log_file  = job.LogPath,
            output,
            truncated,
        }), false);
    }

    // Reads only the tail of the log (bounded bytes from the end, shared read so a live writer
    // doesn't block us) — background logs are unbounded by definition.
    private static string ReadLogTail(string path, int tailLines, out bool truncated)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var start = Math.Max(0, fs.Length - ProcessOutputMaxReadBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        var text = reader.ReadToEnd();

        truncated = start > 0;
        var lines = text.Split('\n');
        if (start > 0 && lines.Length > 0)
            lines = lines[1..]; // first line may be partial — drop it
        if (lines.Length > tailLines)
        {
            lines = lines[^tailLines..];
            truncated = true;
        }
        return string.Join('\n', lines).TrimEnd('\n');
    }

    private static async Task<ToolCallResponse> ProcessKillAsync(Dictionary<string, JsonElement> args)
    {
        var pid = args.Int("pid") ?? throw new ArgumentException("'pid' is required");

        // Registry membership is the entire safety boundary: never signal an arbitrary pid.
        if (!BackgroundJobs.TryGetValue(pid, out var job))
            return Err($"Refusing to kill pid {pid}: it is not a background job started via bash_exec(background:true) in this bridge session. Use process_list to see the tracked jobs.");

        if (!IsPidRunning(pid))
        {
            job.Stopped = true;
            var (status, exitCode) = JobStatus(job);
            return new ToolCallResponse(JsonSerializer.Serialize(new
            {
                pid,
                status,
                exit_code = exitCode,
                note      = "Process was not running.",
            }), false);
        }

        try
        {
            string signaled;
            if (IsWindows)
            {
                // No SIGTERM equivalent for a detached console process — terminate directly.
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                signaled = "terminate";
            }
            else
            {
                // SIGTERM first, SIGKILL only if it ignores the grace period.
                using (var term = Process.Start(new ProcessStartInfo("/bin/kill", pid.ToString())
                       { UseShellExecute = false })!)
                    await term.WaitForExitAsync();

                var deadline = DateTime.UtcNow + KillGracePeriod;
                while (IsPidRunning(pid) && DateTime.UtcNow < deadline)
                    await Task.Delay(100);

                if (IsPidRunning(pid))
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill(entireProcessTree: true);
                    signaled = "SIGKILL";
                }
                else
                {
                    signaled = "SIGTERM";
                }
            }

            job.Stopped = true;
            return new ToolCallResponse(JsonSerializer.Serialize(new
            {
                pid,
                status     = "stopped",
                exit_code  = (int?)null,
                signaled,
                log_file   = job.LogPath,
            }), false);
        }
        catch (Exception ex)
        {
            return Err($"Failed to kill pid {pid}: {ex.Message}");
        }
    }
}
