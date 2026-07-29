using System.Diagnostics;
using System.Text.Json;

namespace Aria.Bridge;

public static partial class BuiltinTools
{
    private const int DefaultMaxOutputChars = 4000;
    private const int MaxOutputCharsCap     = 20000;

    private static IEnumerable<BridgeToolInfo> RunTestsToolInfos()
    {
        yield return new("run_tests",
            "Run the project's build/test/lint/run command and return STRUCTURED results: pass/fail counts plus failing test names with file:line, capped for context — instead of a raw stdout dump. Infers the command from project files (same detection as project_info) or takes an explicit one; a test-name filter is mapped to the ecosystem's native flag (dotnet/pytest/jest/vitest/cargo/go). Prefer this over bash_exec for build/test verification. Long suites convert to a tracked background job on timeout, same as bash_exec.",
            Js("""
               {"type":"object",
                "properties":{
                  "cwd":       {"type":"string","description":"Project directory to run in (absolute path). Defaults to the session cwd — or the first allowed project root."},
                  "kind":      {"type":"string","enum":["test","build","lint","run"],"description":"Which command to run (default \"test\"). lint inference is intentionally weak — pass an explicit command for linters."},
                  "command":   {"type":"string","description":"Explicit command override; skips inference. When set, 'filter' is rejected — append any filter flags to the command yourself."},
                  "filter":    {"type":"string","description":"Test-name filter, applied only to INFERRED test commands; mapped to the ecosystem's native flag (--filter / -k / -t / -run)."},
                  "timeoutSec":{"type":"integer","description":"Seconds before the run converts to a tracked background job instead of being killed (default 120, max 600)."},
                  "maxOutput": {"type":"integer","description":"Maximum characters of the structured result (default 4000)."}
                }}
               """));
    }

    private static async Task<ToolCallResponse> RunTestsAsync(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var kind = (args.Str("kind") ?? "test").Trim().ToLowerInvariant();
        if (kind is not ("test" or "build" or "lint" or "run"))
            return Err($"Unknown kind '{kind}' — expected test|build|lint|run.");

        var commandOverride = args.Str("command");
        var filter          = args.Str("filter");

        // cwd is scope-checked like every other path argument; fall back to the shell's own
        // resolution chain (session cwd → first allowed root → home) when omitted.
        string workDir;
        if (args.Str("cwd") is { } cwdArg)
        {
            workDir = Expand(cwdArg);
            policy?.EnforcePath(workDir);
        }
        else
        {
            workDir = ResolveWorkDir(args, policy);
        }

        if (!Directory.Exists(workDir))
            return Err($"Directory not found: {workDir}");

        string command;
        TestOutputParsers.TestOutputKind parser;
        if (!string.IsNullOrWhiteSpace(commandOverride))
        {
            if (!string.IsNullOrWhiteSpace(filter))
                return Err("'filter' only applies to inferred commands — append the ecosystem's filter flag to 'command' yourself.");
            command = commandOverride;
            parser  = SniffParser(commandOverride);
        }
        else
        {
            if (InferCommand(workDir, kind) is not { } inferred)
                return Err($"Couldn't infer a {kind} command at {workDir}. Pass 'command' explicitly, or run project_info to see what was detected.");
            command = inferred.Command;
            parser  = ParserFor(inferred.Ecosystem);

            if (!string.IsNullOrWhiteSpace(filter))
            {
                if (kind != "test")
                    return Err("'filter' only applies to kind=\"test\".");
                if (ApplyFilter(inferred.Ecosystem, command, filter) is not { } filtered)
                    return Err($"No filter-flag mapping for the {inferred.Ecosystem} ecosystem — pass a full 'command' with the filter appended yourself.");
                command = filtered;
            }
        }

        var timeoutSec = Math.Clamp(args.Int("timeoutSec") ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds);
        var maxOutput  = Math.Clamp(args.Int("maxOutput") ?? DefaultMaxOutputChars, 200, MaxOutputCharsCap);

        // Same execution path as bash_exec: SecurityPolicy inspection inside, timeout converts to a
        // tracked background job rather than killing a long suite.
        var sw = Stopwatch.StartNew();
        var (stdout, stderr, exitCode, timedOut, convertedPid, logPath) =
            await RunShellCommandWithConversionAsync(command, workDir, timeoutSec, policy);
        sw.Stop();

        if (timedOut && convertedPid.HasValue)
        {
            var result = JsonSerializer.Serialize(new
            {
                converted_to_background = true,
                pid       = convertedPid.Value,
                log_file  = logPath,
                note      = $"Test run exceeded the {timeoutSec}s timeout and is STILL RUNNING as background job pid={convertedPid.Value}. " +
                            $"Output continues to {logPath}. Use process_output to check it and process_kill to stop it. " +
                            "Raise timeoutSec next time, or narrow the run with filter.",
            });
            return new ToolCallResponse(result, IsError: false);
        }

        var combined = TestOutputParsers.StripAnsi((stdout + "\n" + stderr).Trim());
        var parsed   = TestOutputParsers.Parse(parser, combined);
        var text     = TestOutputParsers.FormatResult(command, parsed, exitCode, sw.Elapsed, combined, maxOutput);
        return new ToolCallResponse(text, timedOut || exitCode != 0);
    }

    // Reuses project_info's detectors (same order, same priority) and picks the first non-null
    // command for the requested kind. lint is not inferred anywhere today — weak by design, an
    // explicit command covers it.
    internal static (string Ecosystem, string Command)? InferCommand(string root, string kind)
    {
        if (kind == "lint") return null;

        foreach (var eco in new ProjectInfoResult?[]
        {
            TryDetectPython(root), TryDetectDotNet(root), TryDetectNode(root), TryDetectRust(root),
            TryDetectGo(root), TryDetectPowerShell(root), TryDetectRuby(root), TryDetectPhp(root),
        })
        {
            if (eco == null) continue;
            var cmd = kind switch
            {
                "build" => eco.BuildCommand,
                "run"   => eco.RunCommand,
                _       => eco.TestCommand,
            };
            if (!string.IsNullOrWhiteSpace(cmd)) return (eco.Ecosystem, cmd);
        }
        return null;
    }

    private static TestOutputParsers.TestOutputKind ParserFor(string ecosystem) => ecosystem switch
    {
        ".net"   => TestOutputParsers.TestOutputKind.DotNet,
        "python" => TestOutputParsers.TestOutputKind.Pytest,
        "node"   => TestOutputParsers.TestOutputKind.Jest,
        "rust"   => TestOutputParsers.TestOutputKind.Cargo,
        "go"     => TestOutputParsers.TestOutputKind.Go,
        _        => TestOutputParsers.TestOutputKind.Generic,
    };

    // For an explicit command the ecosystem is unknown; recognise the runner by name so its
    // output still gets the structured parser instead of the generic tail-only fallback.
    private static TestOutputParsers.TestOutputKind SniffParser(string command)
    {
        if (command.Contains("dotnet test")) return TestOutputParsers.TestOutputKind.DotNet;
        if (command.Contains("pytest"))      return TestOutputParsers.TestOutputKind.Pytest;
        if (command.Contains("vitest") || command.Contains("jest")) return TestOutputParsers.TestOutputKind.Jest;
        if (command.Contains("cargo test"))  return TestOutputParsers.TestOutputKind.Cargo;
        if (command.Contains("go test"))     return TestOutputParsers.TestOutputKind.Go;
        return TestOutputParsers.TestOutputKind.Generic;
    }

    // Maps a plain test-name filter onto the ecosystem's native flag for an INFERRED command.
    // Returns null when the ecosystem has no mapping — the caller rejects with guidance.
    internal static string? ApplyFilter(string ecosystem, string command, string filter)
    {
        switch (ecosystem)
        {
            case ".net":
                // VSTest wants a property expression; wrap a bare name fragment, pass real
                // expressions (Name~X / Category=Unit) through untouched.
                var expr = filter.Contains('~') || filter.Contains('=') ? filter : $"FullyQualifiedName~{filter}";
                return $"{command} --filter {ShellQuote(expr)}";
            case "python": return $"{command} -k {ShellQuote(filter)}";
            case "node":   return $"{command} -- -t {ShellQuote(filter)}";  // npm needs `--` to forward args to the script
            case "rust":   return $"{command} {ShellQuote(filter)}";        // positional substring match on test names
            case "go":     return $"{command} -run {ShellQuote(filter)}";
            default:       return null;
        }
    }

    private static string ShellQuote(string s) => IsWindows
        ? $"\"{s.Replace("\"", "\"\"")}\""
        : $"'{s.Replace("'", "'\\''")}'";
}
