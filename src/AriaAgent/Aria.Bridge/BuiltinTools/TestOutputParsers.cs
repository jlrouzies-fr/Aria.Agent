using System.Text;
using System.Text.RegularExpressions;

namespace Aria.Bridge;

/// <summary>
/// Console-output parsers for the run_tests builtin: one per ecosystem plus a generic fallback.
/// Each turns raw runner output into counts + a capped list of failing tests (name, file:line,
/// first error line) so the model gets structured feedback instead of an unbounded stdout dump.
/// Best-effort screen scraping: an unrecognised format simply yields no counts/failures, and the
/// caller still reports exit code + output tail.
/// </summary>
internal static class TestOutputParsers
{
    internal enum TestOutputKind { DotNet, Pytest, Jest, Cargo, Go, Generic }

    internal sealed record TestFailure(string Name, string? Location, string? Message);

    internal sealed record ParsedTestRun(int? Passed, int? Failed, int? Skipped, IReadOnlyList<TestFailure> Failures)
    {
        public static readonly ParsedTestRun Empty = new(null, null, null, []);
    }

    internal const int MaxFailures = 20;
    private const int TailChars    = 1500;

    // Runner pipes are usually colourless already, but a project env var (FORCE_COLOR & co.) can
    // re-enable ANSI escapes and break every line anchor below.
    private static readonly Regex Ansi = new("\x1B\\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    internal static string StripAnsi(string s) => Ansi.Replace(s, "");

    internal static ParsedTestRun Parse(TestOutputKind kind, string output) => kind switch
    {
        TestOutputKind.DotNet => ParseDotNet(output),
        TestOutputKind.Pytest => ParsePytest(output),
        TestOutputKind.Jest   => ParseJest(output),
        TestOutputKind.Cargo  => ParseCargo(output),
        TestOutputKind.Go     => ParseGo(output),
        _                     => ParsedTestRun.Empty,
    };

    /// <summary>
    /// Renders the model-facing result: header + counts + up to <see cref="MaxFailures"/> failures,
    /// then — on failure — a capped output tail. Success stays at header + counts (plus a couple of
    /// trailing output lines when the runner reported no counts). Capped at maxOutput overall.
    /// </summary>
    internal static string FormatResult(
        string command, ParsedTestRun parsed, int exitCode, TimeSpan elapsed, string combinedOutput, int maxOutput)
    {
        var sb = new StringBuilder();
        // Model-facing output: invariant culture, so a comma-decimal host locale can't fork the format.
        var secs = elapsed.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        sb.AppendLine($"◈ TEST RUN [{command}] — {(exitCode == 0 ? "PASSED" : "FAILED")} (exit {exitCode}, {secs}s)");

        if (parsed.Passed != null || parsed.Failed != null || parsed.Skipped != null)
        {
            var parts = new List<string>();
            if (parsed.Passed  is { } p) parts.Add($"passed: {p}");
            if (parsed.Failed  is { } f) parts.Add($"failed: {f}");
            if (parsed.Skipped is { } s) parts.Add($"skipped: {s}");
            sb.AppendLine(string.Join("  ", parts));
        }

        foreach (var failure in parsed.Failures.Take(MaxFailures))
        {
            sb.Append("✗ ").Append(failure.Name);
            if (!string.IsNullOrWhiteSpace(failure.Location))
                sb.Append(" — ").Append(failure.Location);
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(failure.Message))
                sb.Append("  ").AppendLine(failure.Message);
        }
        if (parsed.Failures.Count > MaxFailures)
            sb.AppendLine($"  … and {parsed.Failures.Count - MaxFailures} more failures");

        string result;
        if (exitCode != 0 && combinedOutput.Length > 0)
        {
            var tail = combinedOutput.Length <= TailChars ? combinedOutput : combinedOutput[^TailChars..];
            result = sb + $"— output tail (last {tail.Length} chars) —\n{tail}";
        }
        else if (exitCode == 0 && parsed.Passed == null && parsed.Failed == null && combinedOutput.Length > 0)
        {
            // No counts recognised — leave the model the last couple of lines instead of nothing.
            var last = combinedOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .TakeLast(2);
            result = sb + string.Join('\n', last);
        }
        else
        {
            result = sb.ToString().TrimEnd();
        }

        return result.Length <= maxOutput ? result : result[..(maxOutput - 1)] + "…";
    }

    // Summary counters shared by several runners: "2 failed, 181 passed, 1 skipped …".
    private static readonly Regex CountToken = new(
        @"(?<n>\d+)\s+(?<kind>failed|passed|skipped|errors?|xfailed|deselected)", RegexOptions.Compiled);

    private static (int? passed, int? failed, int? skipped) ScanCounts(string line)
    {
        int? passed = null, failed = null, skipped = null;
        foreach (Match c in CountToken.Matches(line))
        {
            var n = int.Parse(c.Groups["n"].Value);
            switch (c.Groups["kind"].Value)
            {
                case "passed":  passed  = n; break;
                case "failed":  failed  = n; break;
                case "skipped": skipped = n; break;
                // pytest reports collection/runtime errors apart from failures; both fail the run.
                case "error":
                case "errors":  failed = (failed ?? 0) + n; break;
            }
        }
        return (passed, failed, skipped);
    }

    // ── dotnet test (VSTest console) ────────────────────────────────────────────

    // Per-test failure line: "  Failed Namespace.Type.Test [5 ms]". The summary one-liner
    // ("Failed!  - Failed: 2, …") and the legacy "Failed: N" counter can't match — after
    // "Failed" they continue with '!' or ':'.
    private static readonly Regex DotNetFailedTest = new(
        @"^\s*Failed\s+(?<name>[A-Za-z_][\w.]*(?:\([^)]*\))?)(\s*\[[^\]]*\])?\s*$", RegexOptions.Compiled);
    // xUnit.net console echo of the same failure: "[xUnit.net 00:00:01.2]     Namespace.Type.Test [FAIL]"
    private static readonly Regex DotNetXunitFail = new(
        @"^(?:\[xUnit\.net[^\]]*\]\s+)?(?<name>[A-Za-z_][\w.]*(?:\([^)]*\))?)\s+\[FAIL\]\s*$", RegexOptions.Compiled);
    // SDK-style summary: "Failed!  - Failed:     2, Passed:   181, Skipped:     0, Total:   183, …"
    private static readonly Regex DotNetNewSummary = new(
        @"-\s+Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+)", RegexOptions.Compiled);
    private static readonly Regex DotNetStackLoc = new(
        @"\bin\s+(?<file>[^\s:]+\.cs):line\s+(?<line>\d+)", RegexOptions.Compiled);

    private static ParsedTestRun ParseDotNet(string output)
    {
        var failures = new List<TestFailure>();
        int? passed = null, failed = null, skipped = null;
        var expectErrorMessage = false;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();

            var summary = DotNetNewSummary.Match(line);
            if (summary.Success)
            {
                failed  = int.Parse(summary.Groups["failed"].Value);
                passed  = int.Parse(summary.Groups["passed"].Value);
                skipped = int.Parse(summary.Groups["skipped"].Value);
                continue;
            }

            // Legacy VSTest block: "Total tests: 183 / Passed: 181 / Failed: 2 / Skipped: 0".
            if (passed  == null && TryCountLine(trimmed, "Passed:",  out var lp)) passed  = lp;
            if (failed  == null && TryCountLine(trimmed, "Failed:",  out var lf)) failed  = lf;
            if (skipped == null && TryCountLine(trimmed, "Skipped:", out var ls)) skipped = ls;

            var m = DotNetFailedTest.Match(line);
            var x = m.Success ? null : DotNetXunitFail.Match(line);
            if (m.Success || x!.Success)
            {
                failures.Add(new TestFailure((m.Success ? m : x).Groups["name"].Value, null, null));
                expectErrorMessage = false;
                continue;
            }

            if (failures.Count == 0) continue;
            var current = failures[^1];

            if (trimmed.StartsWith("Error Message:", StringComparison.Ordinal))
            {
                expectErrorMessage = true;
                continue;
            }
            if (trimmed.StartsWith("Stack Trace:", StringComparison.Ordinal))
            {
                expectErrorMessage = false;
                continue;
            }
            if (expectErrorMessage && trimmed.Length > 0)
            {
                failures[^1] = current with { Message = trimmed };
                expectErrorMessage = false;
                continue;
            }

            var loc = DotNetStackLoc.Match(line);
            if (loc.Success && current.Location == null)
                failures[^1] = current with
                { Location = $"{Path.GetFileName(loc.Groups["file"].Value)}:{loc.Groups["line"].Value}" };
        }

        // The xUnit "[FAIL]" echo and the "Failed …" block describe the same test — merge by name.
        var merged = failures
            .GroupBy(f => f.Name)
            .Select(g => new TestFailure(
                g.Key,
                g.Select(f => f.Location).FirstOrDefault(l => l != null),
                g.Select(f => f.Message).FirstOrDefault(m2 => m2 != null)))
            .ToList();
        return new ParsedTestRun(passed, failed, skipped, merged);
    }

    private static bool TryCountLine(string line, string label, out int n)
    {
        n = 0;
        return line.StartsWith(label, StringComparison.Ordinal)
            && int.TryParse(line[label.Length..].Trim(), out n);
    }

    // ── pytest ──────────────────────────────────────────────────────────────────

    // Short-summary lines: "FAILED tests/test_cart.py::test_empty - Failed: DID NOT RAISE …"
    private static readonly Regex PytestFailedLine = new(
        @"^FAILED\s+(?<name>\S+)(?:\s+-\s+(?<msg>.*))?$", RegexOptions.Compiled);
    // Failure-body location: "tests/test_cart.py:88: Failed"
    private static readonly Regex PytestBodyLoc = new(
        @"^(?<file>[\w./\\-]+\.py):(?<line>\d+):", RegexOptions.Compiled);

    private static ParsedTestRun ParsePytest(string output)
    {
        var lines = output.Split('\n');

        // The FAILED summary lines carry no line numbers; the FAILURES body above them does.
        var fileLines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var loc = PytestBodyLoc.Match(raw.TrimEnd('\r'));
            if (loc.Success) fileLines.TryAdd(loc.Groups["file"].Value, loc.Groups["line"].Value);
        }

        var failures = new List<TestFailure>();
        int? passed = null, failed = null, skipped = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');

            var m = PytestFailedLine.Match(line);
            if (m.Success)
            {
                var name = m.Groups["name"].Value;
                var sep  = name.IndexOf("::", StringComparison.Ordinal);
                var file = sep > 0 ? name[..sep] : null;
                var loc  = file != null && fileLines.TryGetValue(file, out var ln) ? $"{file}:{ln}" : file;
                var msg  = m.Groups["msg"] is { Success: true, Value.Length: > 0 } g ? g.Value : null;
                failures.Add(new TestFailure(name, loc, msg));
                continue;
            }

            // Final summary: "=== 2 failed, 181 passed, 1 skipped in 42.31s ==="
            if (line.StartsWith('=') && line.EndsWith('='))
            {
                var (p, f, s) = ScanCounts(line);
                passed ??= p; failed ??= f; skipped ??= s;
            }
        }

        return new ParsedTestRun(passed, failed, skipped, failures);
    }

    // ── jest / vitest ───────────────────────────────────────────────────────────

    private static readonly Regex JestFailFile = new(@"^\s*FAIL\s+(?<file>\S+)", RegexOptions.Compiled);
    private static readonly Regex JestBullet   = new(@"^\s*●\s+(?<name>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex JestCross    = new(@"^\s*[✕×]\s+(?<name>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex JestStackLoc = new(
        @"\bat\s+.*?\(?(?<loc>[\w./\\-]+\.[cm]?[jt]sx?:\d+)(?::\d+)?\)?\s*$", RegexOptions.Compiled);

    private static ParsedTestRun ParseJest(string output)
    {
        var failures = new List<TestFailure>();
        string? currentFile = null;
        int? passed = null, failed = null, skipped = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();

            var f = JestFailFile.Match(line);
            if (f.Success) { currentFile = f.Groups["file"].Value; continue; }

            // jest "Tests:  1 failed, 181 passed, 182 total" / vitest "Tests  2 failed | 181 passed (183)"
            if (trimmed.StartsWith("Tests:", StringComparison.Ordinal) ||
                trimmed.StartsWith("Tests ", StringComparison.Ordinal))
            {
                var (p, f2, s) = ScanCounts(trimmed);
                passed ??= p; failed ??= f2; skipped ??= s;
                continue;
            }

            var b = JestBullet.Match(line);
            var x = b.Success ? null : JestCross.Match(line);
            if (b.Success || x!.Success)
            {
                var name = (b.Success ? b : x).Groups["name"].Value;
                if (name.StartsWith("Console", StringComparison.OrdinalIgnoreCase)) continue;
                failures.Add(new TestFailure(name, currentFile, null));
                continue;
            }

            if (failures.Count == 0) continue;
            var current = failures[^1];

            var loc = JestStackLoc.Match(line);
            if (loc.Success && current.Location?.Contains(':') != true)
            {
                failures[^1] = current with { Location = loc.Groups["loc"].Value };
                continue;
            }
            if (current.Message == null && trimmed.Length > 0
                && !trimmed.StartsWith("at ", StringComparison.Ordinal)
                && !trimmed.StartsWith('●') && !trimmed.StartsWith('✓') && !trimmed.StartsWith('✕'))
            {
                failures[^1] = current with { Message = trimmed };
            }
        }

        return new ParsedTestRun(passed, failed, skipped, failures);
    }

    // ── cargo test ──────────────────────────────────────────────────────────────

    private static readonly Regex CargoResult = new(
        @"test result: \w+\.\s*(?<passed>\d+) passed; (?<failed>\d+) failed; (?<ignored>\d+) ignored", RegexOptions.Compiled);
    private static readonly Regex CargoStdoutBlock = new(
        @"^---- (?<name>\S+) stdout ----\s*$", RegexOptions.Compiled);
    private static readonly Regex CargoPanic = new(
        @"panicked at (?<loc>[^\s:]+:\d+)(?::\d+)?:", RegexOptions.Compiled);

    private static ParsedTestRun ParseCargo(string output)
    {
        var seenResult = false;
        int passed = 0, failed = 0, ignored = 0;
        var names   = new List<string>();
        var details = new Dictionary<string, (string? Loc, string? Msg)>(StringComparer.Ordinal);

        string? detailName = null;
        var inFailureList      = false;
        var expectPanicMessage = false;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            var r = CargoResult.Match(line);
            if (r.Success)
            {
                // A workspace prints one result line per test binary — aggregate them.
                seenResult = true;
                passed  += int.Parse(r.Groups["passed"].Value);
                failed  += int.Parse(r.Groups["failed"].Value);
                ignored += int.Parse(r.Groups["ignored"].Value);
                inFailureList = false;
                continue;
            }

            if (line == "failures:") { inFailureList = true; detailName = null; continue; }
            if (inFailureList)
            {
                // The closing "failures:" block lists one 4-space-indented test name per line.
                if (line.Length > 4 && line.StartsWith("    ") && !line[4..].Contains(' '))
                {
                    names.Add(line.Trim());
                    continue;
                }
                inFailureList = false;
            }

            var s = CargoStdoutBlock.Match(line);
            if (s.Success) { detailName = s.Groups["name"].Value; expectPanicMessage = false; continue; }
            if (detailName == null) continue;

            var p = CargoPanic.Match(line);
            if (p.Success)
            {
                details[detailName] = (p.Groups["loc"].Value, details.GetValueOrDefault(detailName).Msg);
                expectPanicMessage = true;
                continue;
            }
            if (expectPanicMessage && line.Trim().Length > 0 && !line.StartsWith("note:", StringComparison.Ordinal))
            {
                details[detailName] = (details.GetValueOrDefault(detailName).Loc, line.Trim());
                expectPanicMessage = false;
            }
        }

        if (names.Count == 0 && details.Count > 0) names.AddRange(details.Keys);
        var failures = names.Distinct()
            .Select(n => new TestFailure(n, details.GetValueOrDefault(n).Loc, details.GetValueOrDefault(n).Msg))
            .ToList();

        return seenResult
            ? new ParsedTestRun(passed, failed, ignored, failures)
            : new ParsedTestRun(null, null, null, failures);
    }

    // ── go test ─────────────────────────────────────────────────────────────────

    // Top-level failures only: subtests indent their own "--- FAIL:" and their parent already
    // prints one, so counting both would double the tally.
    private static readonly Regex GoFail = new(@"^--- FAIL:\s+(?<name>\S+)", RegexOptions.Compiled);
    private static readonly Regex GoLoc  = new(@"^\s+(?<file>\S+\.go):(?<line>\d+):\s*(?<msg>.*)$", RegexOptions.Compiled);

    private static ParsedTestRun ParseGo(string output)
    {
        var failures = new List<TestFailure>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            var f = GoFail.Match(line);
            if (f.Success)
            {
                failures.Add(new TestFailure(f.Groups["name"].Value, null, null));
                continue;
            }

            if (failures.Count == 0) continue;
            var current = failures[^1];

            var loc = GoLoc.Match(line);
            if (loc.Success && current.Location == null)
                failures[^1] = current with
                {
                    Location = $"{loc.Groups["file"].Value}:{loc.Groups["line"].Value}",
                    Message  = loc.Groups["msg"].Value is { Length: > 0 } msg ? msg : null,
                };
        }

        // go test prints no passed count on the console — only failures are enumerable.
        return new ParsedTestRun(null, failures.Count > 0 ? failures.Count : null, null, failures);
    }
}
