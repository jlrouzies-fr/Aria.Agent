using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aria.Bridge.Data;
using Aria.Bridge.Services.Logging;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// Runs allow-listed git commands under a declared project location. Backs the chat "#git:*" references
/// and the Explorer's "Changes" tab. Access is gated by the <em>node-authoritative</em> policy
/// (<see cref="NodeTerminalPolicy.ResolveAsync"/>) — exactly like <see cref="ProjectFileEndpoints"/>: the
/// node's declared Allowed Paths are the maximum scope, a server-supplied request may only narrow them,
/// and an empty node list blocks every path — so the server can never run git outside a declared project.
/// </summary>
public static class GitEndpoints
{
    private const int MaxOutputChars = 200_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapGitEndpoints(this WebApplication app)
    {
        // POST /project-git/run — run one of a fixed set of git commands under Root.
        app.MapPost("/project-git/run", async (ProjectGitRunRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Root))
                return Results.BadRequest("root required");

            // Node-authoritative scope: the node's declared Allowed Paths bound every git operation;
            // the server-supplied AllowedPaths may only narrow them (empty node paths = block all).
            var policy = await NodeTerminalPolicy.ResolveAsync(db, req.AllowedPaths);

            var root = Path.GetFullPath(req.Root);
            try { policy.EnforcePath(root); }
            catch (TerminalSecurityException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }

            if (!Directory.Exists(root))
                return Results.NotFound($"Directory not found: {root}");

            var mode = req.Mode?.ToLowerInvariant() ?? "";

            try
            {
                return mode switch
                {
                    "diff" or "status" or "log" => await RunReadOnlyAsync(root, mode),
                    "status-porcelain" => await RunStatusPorcelainAsync(root),
                    "diff-file" => await RunDiffFileAsync(root, req, policy),
                    "stage" => await RunStageAsync(root, req, policy),
                    "unstage" => await RunUnstageAsync(root, req, policy),
                    "commit" => await RunCommitAsync(root, req),
                    "discard" => await RunDiscardAsync(root, req, policy),
                    _ => Results.BadRequest("mode must be one of: diff, status, log, status-porcelain, diff-file, stage, unstage, commit, discard")
                };
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Failed to run git: {ex.Message}" }, statusCode: 500);
            }
        });
    }

    private static async Task<IResult> RunReadOnlyAsync(string root, string mode)
    {
        string[] gitArgs = mode switch
        {
            "diff"   => ["diff"],
            "status" => ["status", "--porcelain"],
            "log"    => ["log", "--oneline", "-20"],
            _        => []
        };

        var result = await RunGitAsync(root, gitArgs);
        if (result.ExitCode != 0)
            return Results.Json(new { error = string.IsNullOrWhiteSpace(result.Stderr) ? "git command failed" : result.Stderr.Trim() }, statusCode: 422);

        var truncated = result.Stdout.Length > MaxOutputChars;
        var output = truncated ? result.Stdout[..MaxOutputChars] : result.Stdout;
        return Results.Ok(new { output, truncated });
    }

    private static async Task<IResult> RunStatusPorcelainAsync(string root)
    {
        var statusResult = await RunGitAsync(root, ["status", "--porcelain=v1", "-z"]);
        if (statusResult.ExitCode != 0)
            return Results.Json(new { error = string.IsNullOrWhiteSpace(statusResult.Stderr) ? "git status failed" : statusResult.Stderr.Trim() }, statusCode: 422);

        var branchResult = await RunGitAsync(root, ["rev-parse", "--abbrev-ref", "HEAD"]);
        var branch = branchResult.ExitCode == 0 ? branchResult.Stdout.Trim() : "HEAD";

        int ahead = 0, behind = 0;
        if (branch != "HEAD")
        {
            var upstreamResult = await RunGitAsync(root, ["rev-list", "--left-right", "--count", "HEAD...@{upstream}"]);
            if (upstreamResult.ExitCode == 0)
            {
                var parts = upstreamResult.Stdout.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b))
                {
                    ahead = a;
                    behind = b;
                }
            }
        }

        var entries = ParsePorcelainV1Z(statusResult.Stdout);
        return Results.Ok(new GitStatusResult(branch, ahead, behind, entries));
    }

    private static List<GitStatusEntry> ParsePorcelainV1Z(string stdout)
    {
        // --porcelain=v1 -z uses NUL terminators and, for renames, a NUL between original and new path.
        var entries = new List<GitStatusEntry>();
        if (string.IsNullOrEmpty(stdout)) return entries;

        var raw = stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < raw.Length; i++)
        {
            var item = raw[i];
            if (item.Length < 3 || item[2] != ' ')
                continue; // defensive: should not happen with v1

            var code = item[..2];
            var path = item[3..];
            string? originalPath = null;

            // Rename entries: "R100 original\0new"; the next raw segment is the new path.
            if (code[0] == 'R' || code[1] == 'R')
            {
                if (i + 1 < raw.Length)
                {
                    originalPath = path;
                    path = raw[++i];
                }
            }

            entries.Add(new GitStatusEntry(code, IsStagedCode(code), path, originalPath));
        }
        return entries;
    }

    private static bool IsStagedCode(string code) => code[0] switch
    {
        'A' or 'M' or 'D' or 'R' => true,
        _ => false
    };

    private static async Task<IResult> RunDiffFileAsync(string root, ProjectGitRunRequest req, SecurityPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(req.Path))
            return Results.BadRequest("path required");

        var rel = req.Path.Replace('\\', '/').TrimStart('/');
        var absPath = Path.GetFullPath(Path.Combine(root, rel));
        try { policy.EnforcePath(absPath); }
        catch (TerminalSecurityException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }

        var args = req.Staged == true
            ? new[] { "diff", "--cached", "--", rel }
            : new[] { "diff", "--", rel };

        var result = await RunGitAsync(root, args);
        if (result.ExitCode != 0)
            return Results.Json(new { error = string.IsNullOrWhiteSpace(result.Stderr) ? "git diff failed" : result.Stderr.Trim() }, statusCode: 422);

        var truncated = result.Stdout.Length > MaxOutputChars;
        var output = truncated ? result.Stdout[..MaxOutputChars] : result.Stdout;
        return Results.Ok(new { output, truncated, path = req.Path, staged = req.Staged == true });
    }

    private static async Task<IResult> RunStageAsync(string root, ProjectGitRunRequest req, SecurityPolicy policy)
    {
        var paths = NormalizeAndValidatePaths(root, req.Paths, policy);
        if (paths.Count == 0)
            return Results.BadRequest("no valid paths to stage");

        var args = new List<string> { "add", "--" };
        args.AddRange(paths);
        var result = await RunGitAsync(root, args);
        BridgeLogger.Log("INFO", $"git stage in {root}: {paths.Count} path(s), exit={result.ExitCode}");
        if (result.ExitCode != 0)
            return Results.Json(new { error = string.IsNullOrWhiteSpace(result.Stderr) ? "git add failed" : result.Stderr.Trim() }, statusCode: 422);

        return Results.Ok(new { success = true, paths });
    }

    private static async Task<IResult> RunUnstageAsync(string root, ProjectGitRunRequest req, SecurityPolicy policy)
    {
        var paths = NormalizeAndValidatePaths(root, req.Paths, policy);
        if (paths.Count == 0)
            return Results.BadRequest("no valid paths to unstage");

        var args = new List<string> { "restore", "--staged", "--" };
        args.AddRange(paths);
        var result = await RunGitAsync(root, args);
        BridgeLogger.Log("INFO", $"git unstage in {root}: {paths.Count} path(s), exit={result.ExitCode}");
        if (result.ExitCode != 0)
            return Results.Json(new { error = string.IsNullOrWhiteSpace(result.Stderr) ? "git restore failed" : result.Stderr.Trim() }, statusCode: 422);

        return Results.Ok(new { success = true, paths });
    }

    private static async Task<IResult> RunCommitAsync(string root, ProjectGitRunRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Message))
            return Results.BadRequest("message required");

        var result = await RunGitAsync(root, ["commit", "-m", req.Message]);
        BridgeLogger.Log("INFO", $"git commit in {root}: exit={result.ExitCode}");
        if (result.ExitCode != 0)
            return Results.Json(new { error = string.IsNullOrWhiteSpace(result.Stderr) ? "git commit failed" : result.Stderr.Trim() }, statusCode: 422);

        return Results.Ok(new { success = true, output = result.Stdout.Trim() });
    }

    private static async Task<IResult> RunDiscardAsync(string root, ProjectGitRunRequest req, SecurityPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(req.Path))
            return Results.BadRequest("path required");

        var rel = req.Path.Replace('\\', '/').TrimStart('/');
        var absPath = Path.GetFullPath(Path.Combine(root, rel));
        try { policy.EnforcePath(absPath); }
        catch (TerminalSecurityException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }

        // Untracked files are not affected by git checkout; remove them with git clean instead.
        var statusResult = await RunGitAsync(root, ["status", "--porcelain=v1", "-z", "--", rel]);
        var isUntracked = statusResult.ExitCode == 0 &&
                          (statusResult.Stdout.Contains($"?? {rel}\0") || statusResult.Stdout == $"?? {rel}\0");

        (int ExitCode, string Stdout, string Stderr) result;
        if (isUntracked)
        {
            result = await RunGitAsync(root, ["clean", "-f", "-q", "--", rel]);
            BridgeLogger.Log("INFO", $"git discard (clean) in {root}: {rel}, exit={result.ExitCode}");
            if (result.ExitCode != 0)
                return Results.Json(new { error = string.IsNullOrWhiteSpace(result.Stderr) ? "git clean failed" : result.Stderr.Trim() }, statusCode: 422);
        }
        else
        {
            result = await RunGitAsync(root, ["checkout", "--", rel]);
            BridgeLogger.Log("INFO", $"git discard in {root}: {rel}, exit={result.ExitCode}");
            if (result.ExitCode != 0)
                return Results.Json(new { error = string.IsNullOrWhiteSpace(result.Stderr) ? "git checkout failed" : result.Stderr.Trim() }, statusCode: 422);
        }

        return Results.Ok(new { success = true, path = req.Path });
    }

    private static List<string> NormalizeAndValidatePaths(string root, string[]? paths, SecurityPolicy policy)
    {
        var result = new List<string>();
        if (paths == null) return result;

        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var rel = p.Replace('\\', '/').TrimStart('/');
            var absPath = Path.GetFullPath(Path.Combine(root, rel));
            try
            {
                policy.EnforcePath(absPath);
                result.Add(rel);
            }
            catch (TerminalSecurityException)
            {
                // skip disallowed paths
            }
        }
        return result;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(string root, IEnumerable<string> args)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName               = "git",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = root,
        };
        proc.StartInfo.ArgumentList.Add("-C");
        proc.StartInfo.ArgumentList.Add(root);
        foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);

        proc.Start();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        var stdout = await outTask;
        var stderr = await errTask;
        return (proc.ExitCode, stdout, stderr);
    }
}

public record ProjectGitRunRequest(
    string Root,
    string Mode,
    string[]? AllowedPaths,
    string? Path = null,
    string[]? Paths = null,
    string? Message = null,
    bool? Staged = null);

public record GitStatusEntry(
    [property: JsonPropertyName("statusCode")] string StatusCode,
    [property: JsonPropertyName("staged")] bool Staged,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("originalPath")] string? OriginalPath);

public record GitStatusResult(
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("ahead")] int Ahead,
    [property: JsonPropertyName("behind")] int Behind,
    [property: JsonPropertyName("entries")] List<GitStatusEntry> Entries,
    [property: JsonPropertyName("error")] string? Error = null);
