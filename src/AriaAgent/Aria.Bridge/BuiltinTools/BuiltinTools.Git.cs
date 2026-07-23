using System.Text.Json;
using Aria.Bridge.Endpoints;

namespace Aria.Bridge;

/// <summary>
/// Agent-facing git tools. Thin wrappers over the same git runner the Explorer's /project-git
/// endpoint uses (<see cref="GitEndpoints.RunGitAsync"/>); the repo path and every file path
/// are validated against the request's <see cref="SecurityPolicy"/> exactly like the file tools.
/// </summary>
public static partial class BuiltinTools
{
    private static IEnumerable<BridgeToolInfo> GitToolInfos()
    {
        yield return new("git_status",
            "Show the working-tree status (short format) and current branch of a git repository.",
            Js("""
               {"type":"object",
                "properties":{
                  "repo_path": {"type":"string","description":"Absolute path to the repository (or any directory inside it)."}
                },
                "required":["repo_path"]}
               """));

        yield return new("git_diff",
            "Show the diff of unstaged changes in a git repository. Set staged to true for staged changes; pass path to limit to one file.",
            Js("""
               {"type":"object",
                "properties":{
                  "repo_path": {"type":"string","description":"Absolute path to the repository."},
                  "path":      {"type":"string","description":"Optional single file to diff (absolute, or relative to repo_path)."},
                  "staged":    {"type":"boolean","description":"Diff staged (cached) changes instead of unstaged. Defaults to false."}
                },
                "required":["repo_path"]}
               """));

        yield return new("git_log",
            "Show recent commits in one-line format.",
            Js("""
               {"type":"object",
                "properties":{
                  "repo_path": {"type":"string","description":"Absolute path to the repository."},
                  "count":     {"type":"integer","description":"Number of commits to show (1-100). Defaults to 20."}
                },
                "required":["repo_path"]}
               """));

        yield return new("git_stage",
            "Stage specific files (git add). Always pass explicit paths.",
            Js("""
               {"type":"object",
                "properties":{
                  "repo_path": {"type":"string","description":"Absolute path to the repository."},
                  "paths":     {"type":"array","items":{"type":"string"},"description":"Files to stage (absolute, or relative to repo_path)."}
                },
                "required":["repo_path","paths"]}
               """));

        yield return new("git_commit",
            "Commit the currently staged changes with a message.",
            Js("""
               {"type":"object",
                "properties":{
                  "repo_path": {"type":"string","description":"Absolute path to the repository."},
                  "message":   {"type":"string","description":"Commit message."}
                },
                "required":["repo_path","message"]}
               """));

        yield return new("git_discard",
            "DESTRUCTIVE: discard uncommitted changes in specific files (checkout tracked files, delete untracked ones). Requires explicit paths — never discards the whole repository.",
            Js("""
               {"type":"object",
                "properties":{
                  "repo_path": {"type":"string","description":"Absolute path to the repository."},
                  "paths":     {"type":"array","items":{"type":"string"},"description":"Files to discard changes in (absolute, or relative to repo_path). Must name specific files."}
                },
                "required":["repo_path","paths"]}
               """));
    }

    private static async Task<ToolCallResponse> GitStatusAsync(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var root = ResolveRepoRoot(args, policy);
        return await RunGitReadOnlyAsync(root, ["status", "--short", "--branch"], "git status failed");
    }

    private static async Task<ToolCallResponse> GitDiffAsync(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var root   = ResolveRepoRoot(args, policy);
        var staged = args.Bool("staged") ?? false;

        var gitArgs = new List<string> { "diff" };
        if (staged) gitArgs.Add("--cached");
        if (args.Str("path") is { Length: > 0 } p)
        {
            gitArgs.Add("--");
            gitArgs.Add(ResolveRepoRelativePath(root, p, policy));
        }
        return await RunGitReadOnlyAsync(root, gitArgs, "git diff failed");
    }

    private static async Task<ToolCallResponse> GitLogAsync(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var root  = ResolveRepoRoot(args, policy);
        var count = Math.Clamp(args.Int("count") ?? 20, 1, 100);
        return await RunGitReadOnlyAsync(root, ["log", "--oneline", $"-{count}"], "git log failed");
    }

    private static async Task<ToolCallResponse> GitStageAsync(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var root  = ResolveRepoRoot(args, policy);
        var paths = ResolveRepoRelativePaths(root, args, policy);
        if (paths.Count == 0)
            return Err("git_stage requires a non-empty 'paths' array of specific files.");

        var gitArgs = new List<string> { "add", "--" };
        gitArgs.AddRange(paths);
        var result = await GitEndpoints.RunGitAsync(root, gitArgs);
        if (result.ExitCode != 0)
            return Err($"git add failed: {StderrOr(result, "git command failed")}");
        return new ToolCallResponse($"Staged {paths.Count} path(s): {string.Join(", ", paths)}", false);
    }

    private static async Task<ToolCallResponse> GitCommitAsync(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var root    = ResolveRepoRoot(args, policy);
        var message = args.Str("message") ?? throw new ArgumentException("'message' is required");

        var result = await GitEndpoints.RunGitAsync(root, ["commit", "-m", message]);
        if (result.ExitCode != 0)
            return Err($"git commit failed: {StderrOr(result, "git command failed")}");
        return new ToolCallResponse(result.Stdout.Trim(), false);
    }

    private static async Task<ToolCallResponse> GitDiscardAsync(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var root  = ResolveRepoRoot(args, policy);
        var paths = ResolveRepoRelativePaths(root, args, policy);
        if (paths.Count == 0)
            return Err("git_discard requires a non-empty 'paths' array of specific files. Whole-repository discard is not supported.");

        var discarded = new List<string>();
        foreach (var rel in paths)
        {
            // Untracked files are not affected by git checkout; remove them with git clean instead.
            var statusResult = await GitEndpoints.RunGitAsync(root, ["status", "--porcelain=v1", "-z", "--", rel]);
            var isUntracked = statusResult.ExitCode == 0 &&
                              statusResult.Stdout.Contains($"?? {rel}\0");

            var result = isUntracked
                ? await GitEndpoints.RunGitAsync(root, ["clean", "-f", "-q", "--", rel])
                : await GitEndpoints.RunGitAsync(root, ["checkout", "--", rel]);
            if (result.ExitCode != 0)
                return Err($"git discard failed for '{rel}': {StderrOr(result, "git command failed")}");
            discarded.Add(rel);
        }
        return new ToolCallResponse($"Discarded changes in {discarded.Count} path(s): {string.Join(", ", discarded)}", false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ResolveRepoRoot(Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var root = Expand(args.Str("repo_path") ?? throw new ArgumentException("'repo_path' is required"));
        policy?.EnforcePath(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository directory not found: {root}");
        return root;
    }

    // Accepts absolute or repo-relative paths; returns a repo-relative path for git. Every
    // resolved absolute path must pass the policy — the agent cannot reach outside the allowed
    // roots by smuggling an absolute path into a repo-relative argument.
    private static string ResolveRepoRelativePath(string root, string p, SecurityPolicy? policy)
    {
        var abs = Path.IsPathRooted(p.Trim())
            ? Expand(p)
            : Path.GetFullPath(Path.Combine(root, p.Replace('\\', '/').TrimStart('/')));
        policy?.EnforcePath(abs);

        var rel = Path.GetRelativePath(root, abs).Replace('\\', '/');
        if (rel == ".." || rel.StartsWith("../"))
            throw new ArgumentException($"Path '{p}' is outside the repository {root}.");
        return rel;
    }

    private static List<string> ResolveRepoRelativePaths(
        string root, Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var result = new List<string>();
        foreach (var p in args.StrArray("paths") ?? [])
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var rel = ResolveRepoRelativePath(root, p, policy);
            // Refuse repo-root / wildcard targets: discard and stage must name specific files.
            if (rel is "." or "./" or "*") continue;
            result.Add(rel);
        }
        return result;
    }

    private static async Task<ToolCallResponse> RunGitReadOnlyAsync(string root, IEnumerable<string> gitArgs, string failureLabel)
    {
        var result = await GitEndpoints.RunGitAsync(root, gitArgs);
        if (result.ExitCode != 0)
            return Err($"{failureLabel}: {StderrOr(result, "git command failed")}");

        var truncated = result.Stdout.Length > GitEndpoints.MaxOutputChars;
        var output = truncated ? result.Stdout[..GitEndpoints.MaxOutputChars] + "\n… (truncated)" : result.Stdout;
        return new ToolCallResponse(output.Length > 0 ? output : "(no changes)", false);
    }

    private static string StderrOr((int ExitCode, string Stdout, string Stderr) result, string fallback) =>
        string.IsNullOrWhiteSpace(result.Stderr) ? fallback : result.Stderr.Trim();
}
