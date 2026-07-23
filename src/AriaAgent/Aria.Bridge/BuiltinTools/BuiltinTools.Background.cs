using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aria.Bridge;

public static partial class BuiltinTools
{
    private const int WaitForDefaultTimeoutSeconds = 30;
    private const int WaitForMaxTimeoutSeconds     = 120;
    private static readonly TimeSpan WaitForPollInterval = TimeSpan.FromMilliseconds(500);

    private static IEnumerable<BridgeToolInfo> BackgroundToolInfos()
    {
        yield return new("run_background",
            "Start a long-running command (dev server, watcher, etc.) detached from the tool call. " +
            "Returns immediately with pid and log_file; the job is tracked and can be followed with wait_for, process_output, process_list and process_kill.",
            Js("""
               {"type":"object",
                "properties":{
                  "command":     {"type":"string","description":"Shell command to run detached."},
                  "working_dir": {"type":"string","description":"Working directory (absolute path). Defaults to the session cwd or the first allowed project root."}
                },
                "required":["command"]}
               """));

        yield return new("wait_for",
            "Wait for a readiness condition: a TCP port on 127.0.0.1 to accept connections, a URL to return any HTTP response, or a regex to appear in a tracked background job's log.",
            Js("""
               {"type":"object",
                "properties":{
                  "port":           {"type":"integer","description":"TCP port on 127.0.0.1 to wait for."},
                  "url":            {"type":"string","description":"Absolute http:// or https:// URL to wait for. Any HTTP response (even 4xx/5xx) counts as up."},
                  "pid":            {"type":"integer","description":"Background job pid whose log should be watched."},
                  "pattern":        {"type":"string","description":".NET regex to search for in the job's log (required with pid)."},
                  "timeout_seconds":{"type":"integer","description":"Maximum seconds to wait (default 30, max 120)."}
                }}
               """));
    }

    private static async Task<ToolCallResponse> RunBackgroundAsync(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var command = args.Str("command") ?? throw new ArgumentException("'command' is required");
        policy?.EnforceCommand(command);

        var workDir = ResolveWorkDir(args, policy);
        return await BashExecBackgroundAsync(command, workDir);
    }

    private static async Task<ToolCallResponse> WaitForAsync(Dictionary<string, JsonElement> args)
    {
        var port = args.Int("port");
        var url = args.Str("url");
        var pid = args.Int("pid");
        var pattern = args.Str("pattern");
        var timeoutSeconds = Math.Clamp(args.Int("timeout_seconds") ?? WaitForDefaultTimeoutSeconds, 1, WaitForMaxTimeoutSeconds);

        var hasPort = port.HasValue;
        var hasUrl = url != null;
        var hasPidPattern = pid.HasValue && pattern != null;

        if (!((hasPort && !hasUrl && !hasPidPattern) ||
              (!hasPort && hasUrl && !hasPidPattern) ||
              (!hasPort && !hasUrl && hasPidPattern)))
        {
            return Err("Exactly one condition is required: 'port', 'url', or both 'pid' and 'pattern'.");
        }

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        if (hasPort)
        {
            var targetPort = port!.Value;
            while (DateTime.UtcNow < deadline)
            {
                if (await TryConnectLocalPortAsync(targetPort))
                {
                    return Ok(new
                    {
                        success = true,
                        condition = "port",
                        port = targetPort,
                        elapsed_seconds = elapsed.Elapsed.TotalSeconds,
                    });
                }
                await Task.Delay(WaitForPollInterval);
            }

            return Ok(new
            {
                success = false,
                condition = "port",
                port = targetPort,
                elapsed_seconds = elapsed.Elapsed.TotalSeconds,
                note = $"Timed out after {timeoutSeconds}s waiting for port {targetPort}.",
            });
        }

        if (hasUrl)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                return Err("Invalid URL. Provide an absolute http:// or https:// URL.");

            while (DateTime.UtcNow < deadline)
            {
                var (ok, status) = await TryGetUrlAsync(url);
                if (ok)
                {
                    return Ok(new
                    {
                        success = true,
                        condition = "url",
                        url,
                        elapsed_seconds = elapsed.Elapsed.TotalSeconds,
                        status_code = status,
                    });
                }
                await Task.Delay(WaitForPollInterval);
            }

            return Ok(new
            {
                success = false,
                condition = "url",
                url,
                elapsed_seconds = elapsed.Elapsed.TotalSeconds,
                note = $"Timed out after {timeoutSeconds}s waiting for {url}.",
            });
        }

        // pid + pattern
        var targetPid = pid!.Value;
        var targetPattern = pattern!;
        if (!BackgroundJobs.TryGetValue(targetPid, out var job))
        {
            return Err($"Unknown pid {targetPid}: wait_for only tracks background jobs started via " +
                       "bash_exec(background:true), run_background, or a foreground bash_exec that exceeded its timeout.");
        }

        var regex = new Regex(targetPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        while (DateTime.UtcNow < deadline)
        {
            var (matched, tail) = TryMatchLog(job.LogPath, regex);
            if (matched != null)
            {
                return Ok(new
                {
                    success = true,
                    condition = "pid_pattern",
                    pid = targetPid,
                    pattern = targetPattern,
                    elapsed_seconds = elapsed.Elapsed.TotalSeconds,
                    matched_line = matched,
                    log_tail = tail,
                });
            }
            await Task.Delay(WaitForPollInterval);
        }

        var (_, finalTail) = TryMatchLog(job.LogPath, regex);
        return Ok(new
        {
            success = false,
            condition = "pid_pattern",
            pid = targetPid,
            pattern = targetPattern,
            elapsed_seconds = elapsed.Elapsed.TotalSeconds,
            log_tail = finalTail,
            note = $"Timed out after {timeoutSeconds}s waiting for pattern in pid {targetPid} log.",
        });
    }

    private static async Task<bool> TryConnectLocalPortAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(bool ok, int? status)> TryGetUrlAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var resp = await _httpRequestClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return (true, (int)resp.StatusCode);
        }
        catch
        {
            return (false, null);
        }
    }

    private static (string? matchedLine, string tail) TryMatchLog(string logPath, Regex regex)
    {
        if (!File.Exists(logPath)) return (null, "");

        var text = ReadLogTail(logPath, 5000, out _);
        var lines = text.Split('\n');
        var tail = string.Join('\n', lines.TakeLast(20));

        foreach (var line in lines)
        {
            try
            {
                if (regex.IsMatch(line))
                    return (line, tail);
            }
            catch { /* malformed regex mid-evaluation — treat as no match */ }
        }
        return (null, tail);
    }

    private static ToolCallResponse Ok(object payload)
        => new(JsonSerializer.Serialize(payload), IsError: false);
}
