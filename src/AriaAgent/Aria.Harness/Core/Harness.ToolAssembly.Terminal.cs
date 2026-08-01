using Aria.Harness.Bridge;
using Aria.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aria.Harness.Core;

public sealed partial class Harness
{
    private async Task<(
        bool Registered,
        (string Name, string Path, string Description, string? NodeId, string? Platform)[] Projects)> TryRegisterTerminalToolsAsync(
        List<AITool> tools,
        Dictionary<string?, string> terminalNodePlatforms,
        HarnessOptions options,
        HarnessContext context,
        string? llmNodeId,
        IReadOnlyDictionary<string, string> toolConfig,
        CancellationToken ct)
    {
        if (!await _runtime.IsBridgeAvailableAsync(context, ct))
            return (false, []);

        // Bridge-authoritative project list takes precedence; fall back to the legacy
        // tool-config AllowedPaths only when the host did not supply it.
        var allNamedPaths = options.TerminalProjects is { Count: > 0 }
            ? options.TerminalProjects.Select(p => (p.Name, p.Path, p.Description, p.NodeId, p.Platform)).ToArray()
            : ParseNamedPaths(toolConfig.GetValueOrDefault("AllowedPaths", ""));
        var scopedNamedPaths = allNamedPaths;
        var blockedCmds      = ParseConfigLines(toolConfig.GetValueOrDefault("BlockedCommands", ""));

        // Active-project scope: when the chat has a project selected, restrict the Terminal
        // tool to just that project so the bridge's path enforcement blocks every other
        // declared project. Only narrows when the selection actually matches a declared
        // project — an unmatched/stale path leaves all projects accessible rather than
        // locking the agent out entirely.
        if (!string.IsNullOrWhiteSpace(options.ActiveProjectPath))
        {
            static string Norm(string p)
            {
                try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar); }
                catch { return p; }
            }
            var target = Norm(options.ActiveProjectPath);
            var scoped = allNamedPaths.Where(p => Norm(p.Path) == target).ToArray();
            if (scoped.Length > 0) scopedNamedPaths = scoped;
        }

        // Group projects by target bridge node. Null/empty nodeId means "use the LLM node".
        var projectsByNode = scopedNamedPaths
            .GroupBy(p => string.IsNullOrEmpty(p.NodeId) ? llmNodeId : p.NodeId)
            .ToList();

        var hasTerminalTools = false;
        var nodeGroups = new List<(string? NodeId, string[] Paths, IList<AITool> Tools)>();
        foreach (var group in projectsByNode)
        {
            var nodeId = group.Key;
            var projectsInGroup = group.ToArray();
            var allowedPaths = projectsInGroup.Select(e => e.Path).ToArray();
            var builtinSrv = new McpServerConfig(
                Name:            "Terminal",
                Command:         "__aria_builtin__",
                Arguments:       [],
                Transport:       McpTransport.LocalBridge,
                AllowedPaths:    allowedPaths.Length > 0 ? allowedPaths : null,
                BlockedCommands: blockedCmds.Length > 0 ? blockedCmds : null);

            try
            {
                var termTools = await LoadBridgeToolsAsync(builtinSrv, context, nodeId);
                if (termTools.Count > 0)
                {
                    nodeGroups.Add((nodeId, allowedPaths, termTools));
                    hasTerminalTools = true;
                }

                var platform = projectsInGroup.Select(p => p.Platform).FirstOrDefault(p => !string.IsNullOrEmpty(p));
                if (!string.IsNullOrEmpty(platform) && !terminalNodePlatforms.ContainsKey(nodeId))
                    terminalNodePlatforms[nodeId] = platform;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Terminal built-in tools failed to load for node {NodeId}", nodeId ?? "(default)");
            }
        }

        if (nodeGroups.Count == 1)
        {
            foreach (var t in nodeGroups[0].Tools) tools.Add(t);
        }
        else if (nodeGroups.Count > 1)
        {
            // Projects live on several machines but the tool NAMES are identical — adding
            // every node's set verbatim would collide and strand all calls on the first
            // node (a Windows path then hits the Mac bridge and is blocked). Merge into
            // one path-routed dispatcher per tool name.
            var defaultIdx = Math.Max(0, nodeGroups.FindIndex(g => g.NodeId == llmNodeId));
            foreach (var name in nodeGroups.SelectMany(g => g.Tools.Select(t => t.Name)).Distinct())
            {
                var candidates = new List<PathRoutedTerminalTool.Candidate>();
                var defCandidate = 0;
                for (var i = 0; i < nodeGroups.Count; i++)
                {
                    var fn = nodeGroups[i].Tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == name);
                    if (fn == null) continue;
                    if (i == defaultIdx) defCandidate = candidates.Count;
                    candidates.Add(new PathRoutedTerminalTool.Candidate(fn, nodeGroups[i].Paths, nodeGroups[i].NodeId));
                }
                if (candidates.Count > 0)
                    tools.Add(new PathRoutedTerminalTool(candidates, defCandidate, options.NodeLabels));
            }
        }

        return (hasTerminalTools, hasTerminalTools ? allNamedPaths : []);
    }
}
