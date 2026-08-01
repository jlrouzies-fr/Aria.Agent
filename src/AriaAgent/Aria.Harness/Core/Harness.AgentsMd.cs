using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aria.Harness.Core;

public sealed partial class Harness
{
    /// <summary>
    /// Load the active project's <c>AGENTS.md</c> via Benign <c>read_file</c> (no context grant).
    /// Returns raw/numbered file text, or null when absent, blocked, or the bridge is unreachable.
    /// Fail-soft: never blocks session creation.
    /// </summary>
    private async Task<string?> TryLoadAgentsMdAsync(
        string activeProjectPath,
        (string Name, string Path, string Description, string? NodeId, string? Platform)[] projects,
        string? llmNodeId,
        HarnessContext context,
        CancellationToken ct)
    {
        static string Norm(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return p.TrimEnd('/', '\\'); }
        }

        var target = Norm(activeProjectPath);
        var match = projects.FirstOrDefault(p =>
            string.Equals(Norm(p.Path), target, StringComparison.OrdinalIgnoreCase));

        // Prefer the project's own node; fall back to the LLM node (single-machine default).
        var nodeId = !string.IsNullOrEmpty(match.Path) && !string.IsNullOrEmpty(match.NodeId)
            ? match.NodeId
            : llmNodeId;

        // Narrow AllowedPaths to the active project so a compromised server cannot browse siblings
        // through this session-setup read. The node still fail-closes if Projects is off.
        var allowedRoot = !string.IsNullOrEmpty(match.Path) ? match.Path : activeProjectPath;
        var agentsPath = AgentsMdPrompt.ResolvePath(allowedRoot);

        var body = JsonSerializer.Serialize(new
        {
            command       = "__aria_builtin__",
            arguments     = Array.Empty<string>(),
            toolName      = "read_file",
            toolArguments = new Dictionary<string, object> { ["path"] = agentsPath },
            serverName    = "Terminal",
            sessionId     = context.SessionId,
            policy        = new { allowedPaths = new[] { allowedRoot } },
        });

        try
        {
            var responseJson = await _runtime.BridgePostAsync(
                "http://localhost:5741/tools/call", body, context, ct, nodeId: nodeId);

            if (string.IsNullOrWhiteSpace(responseJson) ||
                responseJson.Contains("CONTEXT_APPROVAL_REQUIRED", StringComparison.Ordinal))
                return null;

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("isError", out var errEl) && errEl.GetBoolean())
                return null;
            if (!root.TryGetProperty("text", out var textEl))
                return null;

            var text = textEl.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AGENTS.md load skipped for {Path}", agentsPath);
            return null;
        }
    }
}
