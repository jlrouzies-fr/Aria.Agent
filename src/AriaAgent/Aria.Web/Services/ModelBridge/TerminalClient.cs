using System.Text.Json;

namespace Aria.Web.Services.ModelBridge;

/// <summary>
/// Calls the bridge's user-driven /terminal endpoints for the chat shared terminal panel.
/// </summary>
public class TerminalClient(ModelBridgeRegistry registry)
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Fetches the bridge-authoritative Terminal policy. The web may only display these;
    /// editing happens on the bridge status page at http://localhost:5741.
    /// </summary>
    public async Task<(string[] AllowedPaths, string[] BlockedCommands)> GetConfigAsync(string userId, string? nodeId = null)
    {
        try
        {
            var result = await registry.SendLocalRestAsync(userId, "GET", "/terminal/config", nodeId: nodeId, timeoutSeconds: 10);
            if (result?.StatusCode == 200 && result.Value.Body != null)
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(result.Value.Body, _json);
                var allowed = doc.TryGetProperty("allowedPaths", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()
                    : [];
                var blocked = doc.TryGetProperty("blockedCommands", out var b) && b.ValueKind == JsonValueKind.Array
                    ? b.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()
                    : [];
                return (allowed, blocked);
            }
        }
        catch { }
        return ([], []);
    }

    /// <summary>
    /// Fetches the bridge-authoritative Terminal project list. The web may only display these;
    /// editing happens on the bridge status page at http://localhost:5741.
    /// </summary>
    public async Task<List<TerminalProject>> GetProjectsAsync(string userId, string? nodeId = null)
    {
        try
        {
            var result = await registry.SendLocalRestAsync(userId, "GET", "/terminal/projects", nodeId: nodeId, timeoutSeconds: 10);
            if (result?.StatusCode == 200 && result.Value.Body != null)
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(result.Value.Body, _json);
                if (doc.TryGetProperty("projects", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    return arr.EnumerateArray()
                        .Select(e => new TerminalProject(
                            Name: e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            Path: e.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                            Description: e.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                            NodeId: e.TryGetProperty("nodeId", out var nid) ? nid.GetString() : null,
                            Platform: e.TryGetProperty("platform", out var plf) ? plf.GetString() : null))
                        .Where(p => !string.IsNullOrWhiteSpace(p.Path))
                        .ToList();
                }
            }
        }
        catch { }
        return [];
    }

    /// <summary>
    /// Fetches the Terminal project list from EVERY connected node and merges them, so each bridge sees
    /// the others' projects (a multi-node soul shares one project picker). Each project is tagged with the
    /// node it actually lives on — the harness groups by NodeId to route exec, so a blank tag would make a
    /// remote project's commands run on the wrong machine.
    /// </summary>
    public async Task<List<TerminalProject>> GetAllProjectsAsync(string userId)
    {
        var nodes = registry.GetNodes(userId).OrderBy(n => n.ConnectedAt).ToList();
        var results = await Task.WhenAll(nodes.Select(async n =>
        {
            var projs = await GetProjectsAsync(userId, n.NodeId);
            return projs.Select(p => p with
            {
                NodeId   = string.IsNullOrEmpty(p.NodeId)   ? n.NodeId   : p.NodeId,
                Platform = string.IsNullOrEmpty(p.Platform) ? n.Platform : p.Platform,
            }).ToList();
        }));
        return results.SelectMany(r => r).ToList();
    }

    /// <summary>
    /// Executes a shell command on the cogitator node. Returns null if no bridge is connected or the call failed.
    /// </summary>
    public async Task<TerminalResult?> ExecuteAsync(
        string userId,
        string command,
        string? cwd,
        string sessionId,
        string[] allowedPaths,
        string[] blockedCommands,
        string? nodeId = null,
        int? timeoutSeconds = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                command,
                cwd,
                sessionId,
                allowedPaths,
                blockedCommands,
                timeoutSeconds,
            }, _json);

            var result = await registry.SendLocalRestAsync(userId, "POST", "/terminal/exec", body, nodeId: nodeId, timeoutSeconds: timeoutSeconds ?? 130);
            if (result?.StatusCode == 200 && result.Value.Body != null)
            {
                var resp = JsonSerializer.Deserialize<TerminalResult>(result.Value.Body, _json);
                if (resp != null) return resp;
            }
            else if (result?.StatusCode == 403 && result.Value.Body != null)
            {
                var err = JsonSerializer.Deserialize<JsonElement>(result.Value.Body, _json);
                var msg = err.TryGetProperty("error", out var e) ? e.GetString() : "Command blocked by node policy";
                return new TerminalResult
                {
                    ExitCode = 1,
                    Stderr = msg ?? "Command blocked by node policy",
                    Cwd = cwd ?? "",
                };
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Returns true if the node has enabled PTY mode, false if not or if the bridge is unreachable.
    /// </summary>
    public async Task<bool> IsPtyEnabledAsync(string userId, string? nodeId = null)
    {
        try
        {
            var result = await registry.SendLocalRestAsync(userId, "GET", "/terminal/pty-enabled", nodeId: nodeId, timeoutSeconds: 5);
            if (result?.StatusCode == 200 && result.Value.Body != null)
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(result.Value.Body, _json);
                if (doc.TryGetProperty("enabled", out var e)) return e.GetBoolean();
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Returns true if the node has enabled the Terminal capability (Quick Exec + PTY),
    /// false if not or if the bridge is unreachable.
    /// </summary>
    public async Task<bool> IsTerminalEnabledAsync(string userId, string? nodeId = null)
    {
        try
        {
            var result = await registry.SendLocalRestAsync(userId, "GET", "/terminal/enabled", nodeId: nodeId, timeoutSeconds: 5);
            if (result?.StatusCode == 200 && result.Value.Body != null)
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(result.Value.Body, _json);
                if (doc.TryGetProperty("enabled", out var e)) return e.GetBoolean();
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Persists the PTY enable flag on the node after a seal has been approved. Returns true on success.
    /// </summary>
    public async Task<bool> EnablePtyAsync(string userId, string sealId, string? nodeId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { sealId }, _json);
            var result = await registry.SendLocalRestAsync(userId, "POST", "/terminal/pty-enable", body, nodeId: nodeId, timeoutSeconds: 10);
            return result?.StatusCode == 200;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Requests shell-style Tab completion from the cogitator node. Returns null if no bridge is connected or the call failed.
    /// </summary>
    public async Task<TerminalCompletionResult?> CompleteAsync(
        string userId,
        string line,
        int cursor,
        string? cwd,
        string sessionId,
        string[] allowedPaths,
        string? nodeId = null)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                line,
                cursor,
                cwd,
                sessionId,
                allowedPaths,
            }, _json);

            var result = await registry.SendLocalRestAsync(userId, "POST", "/terminal/complete", body, nodeId: nodeId, timeoutSeconds: 5);
            if (result?.StatusCode == 200 && result.Value.Body != null)
            {
                var resp = JsonSerializer.Deserialize<TerminalCompletionResult>(result.Value.Body, _json);
                if (resp != null) return resp;
            }
        }
        catch { }
        return null;
    }
}

public class TerminalResult
{
    public int? ExitCode { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public bool TimedOut { get; set; }
    public string Cwd { get; set; } = "";
}

public class TerminalCompletionResult
{
    public int ReplaceStart { get; set; }
    public int ReplaceEnd { get; set; }
    public string CommonPrefix { get; set; } = "";
    public List<TerminalCompletionCandidate> Candidates { get; set; } = [];
    public bool Truncated { get; set; }
}

public class TerminalCompletionCandidate
{
    public string Text { get; set; } = "";
    public bool IsDir { get; set; }
}
