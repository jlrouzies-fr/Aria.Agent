using System.Text.Json;

namespace Aria.Web.Services.ModelBridge;

/// <summary>
/// Reads the user's declared project locations (Terminal › Allowed Projects) and lists/reads files
/// under them via the local bridge. Backs the chat "#" file reference picker and the Explorer's
/// "Changes" git tab. The bridge enforces AllowedPaths on every call, so this never reaches outside
/// a declared project.
///
/// Every call is also subject to the bridge's Layer B context-grant gate (the same one the chat
/// agent's sensitive ops hit): without a live session grant the bridge refuses with 403 +
/// <c>contextApprovalRequired</c>, and <see cref="SendWithApprovalAsync"/> drives the node-side
/// approval ceremony and retries. The grant is session-scoped and shared with the chat agent —
/// whichever surface asks first covers both for the grant's lifetime. Callers should always pass
/// the circuit's session token (<c>SessionState.SessionToken</c>) so the refusal can be resolved.
/// </summary>
public class ProjectFilesClient(ModelBridgeRegistry registry, ContextApprovalService approval)
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Raised when a call starts (true) or finishes (false) waiting for the human's node-side
    /// context approval, with the user id. Singleton service — subscribers must filter by their own
    /// user and unsubscribe on dispose.</summary>
    public event Action<string, bool>? ApprovalPendingChanged;

    /// <summary>
    /// Sends a POST through the tunnel and, when the bridge refuses for lack of a Layer B context
    /// grant, drives the node-side approval ceremony once and retries the call. Without a session id
    /// there is nothing to scope a grant to, so the refusal is returned as-is (fail-closed).
    /// </summary>
    private async Task<(int StatusCode, string? Body)?> SendWithApprovalAsync(
        string userId, string path, string body, string? nodeId, string? sessionId, CancellationToken ct = default)
    {
        var result = await registry.SendLocalRestAsync(userId, "POST", path, body, nodeId: nodeId, sessionId: sessionId);
        if (result is { StatusCode: 403, Body: { } refused } && IsContextApprovalRefusal(refused)
            && !string.IsNullOrEmpty(sessionId))
        {
            bool approved;
            ApprovalPendingChanged?.Invoke(userId, true);
            try { approved = await approval.RequestGrantAsync(userId, sessionId, ct); }
            catch (OperationCanceledException) { approved = false; }
            finally { ApprovalPendingChanged?.Invoke(userId, false); }
            if (approved)
                result = await registry.SendLocalRestAsync(userId, "POST", path, body, nodeId: nodeId, sessionId: sessionId);
        }
        return result;
    }

    private static bool IsContextApprovalRefusal(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("contextApprovalRequired", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    /// <summary>
    /// Parses the Terminal tool's "AllowedPaths" config JSON ([{name, path, description, nodeId?, platform?}]) into a list
    /// of projects. Shared by the Tools modal and the chat picker so there is one parser.
    /// </summary>
    public static List<TerminalProject> ParseProjects(string? allowedPathsJson)
    {
        var projects = new List<TerminalProject>();
        if (string.IsNullOrWhiteSpace(allowedPathsJson)) return projects;
        try
        {
            using var doc = JsonDocument.Parse(allowedPathsJson);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var name = e.TryGetProperty("name",        out var np) ? np.GetString() ?? "" : "";
                var path = e.TryGetProperty("path",        out var pp) ? pp.GetString() ?? "" : "";
                var desc = e.TryGetProperty("description", out var dp) ? dp.GetString() ?? "" : "";
                var nodeId  = e.TryGetProperty("nodeId",   out var nid) ? nid.GetString() : null;
                var platform = e.TryGetProperty("platform", out var plf) ? plf.GetString() : null;
                projects.Add(new TerminalProject(name, path, desc, nodeId, platform));
            }
        }
        catch { }
        return projects;
    }

    /// <summary>Lists files under <paramref name="root"/> whose relative path matches <paramref name="filter"/>.</summary>
    public async Task<List<ProjectFileEntry>> ListFilesAsync(
        string userId, string root, string? filter, string[] allowedPaths, int limit = 50, string? nodeId = null,
        string? sessionId = null, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { root, filter, limit, allowedPaths }, _json);
            var result = await SendWithApprovalAsync(userId, "/project-files/list", body, nodeId, sessionId, ct);
            if (result?.StatusCode == 200 && result.Value.Body != null)
            {
                var resp = JsonSerializer.Deserialize<ListResponse>(result.Value.Body, _json);
                return resp?.Files ?? [];
            }
        }
        catch { }
        return [];
    }

    /// <summary>Full recursive listing (files + dirs) under <paramref name="root"/>, for building an
    /// explorer tree client-side. Returns null on any failure.</summary>
    public async Task<ProjectTreeResult?> ListTreeAsync(
        string userId, string root, string[] allowedPaths, string? nodeId = null, string? sessionId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { root, allowedPaths }, _json);
            var result = await SendWithApprovalAsync(userId, "/project-files/tree", body, nodeId, sessionId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
            {
                var resp = JsonSerializer.Deserialize<TreeResponse>(result.Value.Body, _json);
                if (resp != null)
                    return new ProjectTreeResult(resp.Files, resp.Dirs, resp.Truncated);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Runs a read-only git command ("diff" | "status" | "log") under <paramref name="root"/>
    /// on the bridge. Returns null on any failure (including a non-zero git exit, e.g. not a repo).</summary>
    public async Task<ProjectGitResult?> RunGitAsync(
        string userId, string root, string mode, string[] allowedPaths, string? nodeId = null, string? sessionId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { root, mode, allowedPaths }, _json);
            var result = await SendWithApprovalAsync(userId, "/project-git/run", body, nodeId, sessionId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
            {
                var resp = JsonSerializer.Deserialize<GitRunResponse>(result.Value.Body, _json);
                if (resp != null)
                    return new ProjectGitResult(resp.Output, resp.Truncated);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Runs <c>git status --porcelain=v1 -z</c> and returns a parsed status view, or null on failure.</summary>
    public async Task<ProjectGitStatusResult?> GetGitStatusAsync(
        string userId, string root, string[] allowedPaths, string? nodeId = null, string? sessionId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { root, mode = "status-porcelain", allowedPaths }, _json);
            var result = await SendWithApprovalAsync(userId, "/project-git/run", body, nodeId, sessionId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
                return JsonSerializer.Deserialize<ProjectGitStatusResult>(result.Value.Body, _json);
        }
        catch { }
        return null;
    }

    /// <summary>Runs <c>git diff [--cached] -- path</c> under <paramref name="root"/>. Returns null on failure.</summary>
    public async Task<ProjectGitDiffResult?> GetGitDiffAsync(
        string userId, string root, string relPath, bool staged, string[] allowedPaths, string? nodeId = null, string? sessionId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { root, mode = "diff-file", path = relPath, staged, allowedPaths }, _json);
            var result = await SendWithApprovalAsync(userId, "/project-git/run", body, nodeId, sessionId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
                return JsonSerializer.Deserialize<ProjectGitDiffResult>(result.Value.Body, _json);
        }
        catch { }
        return null;
    }

    /// <summary>Stages the given relative paths. Returns null on failure.</summary>
    public async Task<GitWriteResult?> StageAsync(
        string userId, string root, IEnumerable<string> relPaths, string[] allowedPaths, string? nodeId = null, string? sessionId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { root, mode = "stage", paths = relPaths.ToArray(), allowedPaths }, _json);
            var result = await SendWithApprovalAsync(userId, "/project-git/run", body, nodeId, sessionId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
                return JsonSerializer.Deserialize<GitWriteResult>(result.Value.Body, _json);
        }
        catch { }
        return null;
    }

    /// <summary>Unstages the given relative paths. Returns null on failure.</summary>
    public async Task<GitWriteResult?> UnstageAsync(
        string userId, string root, IEnumerable<string> relPaths, string[] allowedPaths, string? nodeId = null, string? sessionId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { root, mode = "unstage", paths = relPaths.ToArray(), allowedPaths }, _json);
            var result = await SendWithApprovalAsync(userId, "/project-git/run", body, nodeId, sessionId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
                return JsonSerializer.Deserialize<GitWriteResult>(result.Value.Body, _json);
        }
        catch { }
        return null;
    }

    /// <summary>Commits staged changes with the given message. Returns null on failure.</summary>
    public async Task<GitCommitResult?> CommitAsync(
        string userId, string root, string message, string[] allowedPaths, string? nodeId = null, string? sessionId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { root, mode = "commit", message, allowedPaths }, _json);
            var result = await SendWithApprovalAsync(userId, "/project-git/run", body, nodeId, sessionId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
                return JsonSerializer.Deserialize<GitCommitResult>(result.Value.Body, _json);
        }
        catch { }
        return null;
    }

    /// <summary>Discards working-tree changes for a single relative path. Returns null on failure.</summary>
    public async Task<GitWriteResult?> DiscardAsync(
        string userId, string root, string relPath, string[] allowedPaths, string? nodeId = null, string? sessionId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { root, mode = "discard", path = relPath, allowedPaths }, _json);
            var result = await SendWithApprovalAsync(userId, "/project-git/run", body, nodeId, sessionId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
                return JsonSerializer.Deserialize<GitWriteResult>(result.Value.Body, _json);
        }
        catch { }
        return null;
    }

    /// <summary>Reads a file's text content (size-capped on the bridge). Returns null on any failure.</summary>
    public async Task<ProjectFileReadResult?> ReadFileAsync(string userId, string absPath, string[] allowedPaths, string? nodeId = null, string? sessionId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { path = absPath, allowedPaths }, _json);
            var result = await SendWithApprovalAsync(userId, "/project-files/read", body, nodeId, sessionId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
            {
                var resp = JsonSerializer.Deserialize<ReadResponse>(result.Value.Body, _json);
                if (resp != null)
                    return new ProjectFileReadResult(resp.Content, resp.Truncated, resp.Hash);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Writes text content to a file on the bridge, using optimistic concurrency via baseHash.
    /// Returns null on transport failure; a conflict response is represented by Conflict=true.</summary>
    public async Task<ProjectFileWriteResult?> WriteFileAsync(
        string userId, string absPath, string content, string baseHash, string[] allowedPaths, string? nodeId = null, string? sessionId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { path = absPath, content, baseHash, allowedPaths }, _json);
            var result = await SendWithApprovalAsync(userId, "/project-files/write", body, nodeId, sessionId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
            {
                var resp = JsonSerializer.Deserialize<WriteResponse>(result.Value.Body, _json);
                if (resp != null)
                    return new ProjectFileWriteResult(true, resp.Hash, null, null, false);
            }
            else if (result?.StatusCode == 409 && result.Value.Body != null)
            {
                var resp = JsonSerializer.Deserialize<WriteConflictResponse>(result.Value.Body, _json);
                if (resp != null)
                    return new ProjectFileWriteResult(false, null, resp.Content, resp.Hash, true, resp.Diff);
            }
        }
        catch { }
        return new ProjectFileWriteResult(false, null, null, null, false);
    }

    /// <summary>Reverts a file mutation by undo token. Returns null on any failure.</summary>
    public async Task<RevertResult?> RevertAsync(
        string userId, string undoToken, string[] allowedPaths, bool force = false, string? nodeId = null, string? sessionId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { undoToken, allowedPaths, force }, _json);
            var result = await SendWithApprovalAsync(userId, "/project-files/revert", body, nodeId, sessionId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
            {
                var resp = JsonSerializer.Deserialize<RevertResponse>(result.Value.Body, _json);
                if (resp != null)
                    return new RevertResult(resp.Path, resp.Reverted, resp.ReverseDiff);
            }
            else if (result?.StatusCode == 409 && result.Value.Body != null)
            {
                var err = JsonSerializer.Deserialize<RevertErrorResponse>(result.Value.Body, _json);
                if (err?.HashMismatch == true)
                    return new RevertResult(null, false, null, true);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Reverts all unreverted file mutations recorded under one checkpoint (usually one
    /// agent turn). Returns null on failure.</summary>
    public async Task<RevertCheckpointResult?> RevertCheckpointAsync(
        string userId, string checkpoint, string[] allowedPaths, string? nodeId = null, string? sessionId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { checkpoint, allowedPaths }, _json);
            var result = await SendWithApprovalAsync(userId, "/project-files/revert-checkpoint", body, nodeId, sessionId);
            if (result?.StatusCode == 200 && result.Value.Body != null)
            {
                var resp = JsonSerializer.Deserialize<RevertCheckpointResponse>(result.Value.Body, _json);
                if (resp != null)
                    return new RevertCheckpointResult(resp.Checkpoint, resp.RewindCheckpoint, resp.Reverted, resp.Skipped, resp.Missing, resp.Results);
            }
        }
        catch { }
        return null;
    }

    private record ListResponse(List<ProjectFileEntry> Files, int Scanned, bool Truncated);
    private record TreeResponse(List<string> Files, List<string> Dirs, int Scanned, bool Truncated);
    private record ReadResponse(string Path, string Content, bool Truncated, string Hash);
    private record WriteResponse(string Path, string Hash);
    private record WriteConflictResponse(string Path, string Content, string Hash, string Error, string? Diff);
    private record GitRunResponse(string Output, bool Truncated);
    private record RevertResponse(string Path, bool Reverted, string? ReverseDiff);
    private record RevertErrorResponse(string Error, bool HashMismatch);
    private record RevertCheckpointResponse(
        string Checkpoint,
        string RewindCheckpoint,
        int Reverted,
        int Skipped,
        int Missing,
        List<RevertCheckpointEntry> Results);
}

public record RevertResult(string? Path, bool Reverted, string? ReverseDiff, bool HashMismatch = false);
public record RevertCheckpointResult(
    string Checkpoint,
    string RewindCheckpoint,
    int Reverted,
    int Skipped,
    int Missing,
    List<RevertCheckpointEntry> Results);
public record RevertCheckpointEntry(string UndoToken, string Path, string Status, string Detail);

public record TerminalProject(string Name, string Path, string Description, string? NodeId = null, string? Platform = null);
public record ProjectFileEntry(string RelPath, string AbsPath);
public record ProjectTreeResult(List<string> Files, List<string> Dirs, bool Truncated);
public record ProjectFileReadResult(string Content, bool Truncated, string Hash);
public record ProjectFileWriteResult(bool Success, string? Hash, string? CurrentContent, string? CurrentHash, bool Conflict, string? Diff = null);
public record ProjectGitResult(string Output, bool Truncated);

public record ProjectGitStatusEntry(string StatusCode, bool Staged, string Path, string? OriginalPath);
public record ProjectGitStatusResult(string Branch, int Ahead, int Behind, List<ProjectGitStatusEntry> Entries, string? Error = null);
public record ProjectGitDiffResult(string Output, bool Truncated, string Path, bool Staged);
public record GitWriteResult(bool Success, string[]? Paths = null, string? Path = null, string? Error = null);
public record GitCommitResult(bool Success, string? Output = null, string? Error = null);
