using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Aria.Harness.Bridge;

/// <summary>
/// Dispatches one terminal tool name (bash_exec, read_file, …) across several bridge nodes.
/// Terminal projects can live on different machines; loading each node's tool set verbatim would
/// register the same function names twice, and only the first registration would ever run —
/// sending, say, a Windows path to the Mac bridge, which correctly refuses it. This wrapper keeps
/// ONE function per name and routes each call to the node whose project path matches the path-like
/// argument of the call (longest normalized prefix wins). Calls with no recognizable path go to the
/// default candidate (the LLM node's group when present, else the first).
/// </summary>
public sealed class PathRoutedTerminalTool : AIFunction
{
    /// <summary>One node's implementation of the tool plus the project paths that select it.</summary>
    public sealed record Candidate(AIFunction Tool, string[] PathPrefixes, string? NodeId);

    // Argument keys that can carry an absolute path, by convention of the builtin terminal tools.
    private static readonly string[] PathArgKeys = ["path", "working_dir", "base_dir", "pattern"];

    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _jsonSchema;
    private readonly IReadOnlyList<Candidate> _candidates;
    private readonly int _defaultIndex;
    private readonly IReadOnlyDictionary<string, string>? _nodeLabels;

    public PathRoutedTerminalTool(
        IReadOnlyList<Candidate> candidates,
        int defaultIndex,
        IReadOnlyDictionary<string, string>? nodeLabels = null)
    {
        _candidates   = candidates;
        _defaultIndex = defaultIndex >= 0 && defaultIndex < candidates.Count ? defaultIndex : 0;
        _nodeLabels   = nodeLabels;
        var template  = candidates[0].Tool;
        _name         = template.Name;
        _description  = template.Description;
        _jsonSchema   = template.JsonSchema;
    }

    public override string Name => _name;
    public override string Description => _description;
    public override JsonElement JsonSchema => _jsonSchema;

    /// <summary>Node id a path-less call would run on (the session's default node group).</summary>
    public string? DefaultNodeId => _candidates[_defaultIndex].NodeId;

    /// <summary>
    /// The node id THIS call would be routed to, given the governance view of the arguments.
    /// Governance uses it to gate cross-node (fleet) routing before the call runs.
    /// </summary>
    public string? ResolveTargetNodeId(IReadOnlyDictionary<string, JsonElement> args)
    {
        var paths = PathArgKeys
            .Select(k => args.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Normalize(s!))
            .ToList();
        return ResolveCandidate(paths).NodeId;
    }

    /// <summary>Display name for a node: its registry label when known, else a short id slice.</summary>
    public string DescribeNode(string nodeId) =>
        _nodeLabels != null && _nodeLabels.TryGetValue(nodeId, out var label) && !string.IsNullOrWhiteSpace(label)
            ? label
            : nodeId.Length > 8 ? nodeId[..8] : nodeId;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var paths = PathArgKeys
            .Select(k => arguments.TryGetValue(k, out var v) ? AsString(v) : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Normalize(s!))
            .ToList();
        var target = ResolveCandidate(paths);
        return await target.Tool.InvokeAsync(arguments, cancellationToken);
    }

    private Candidate ResolveCandidate(List<string> paths)
    {
        if (paths.Count == 0) return _candidates[_defaultIndex];

        Candidate? best = null;
        var bestLen = -1;
        foreach (var cand in _candidates)
        foreach (var prefix in cand.PathPrefixes)
        {
            var np = Normalize(prefix);
            if (np.Length <= bestLen) continue;
            if (paths.Any(p => p.StartsWith(np, StringComparison.OrdinalIgnoreCase)))
            {
                best = cand;
                bestLen = np.Length;
            }
        }
        return best ?? _candidates[_defaultIndex];
    }

    private static string? AsString(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => null,
    };

    // Case- and separator-insensitive comparison so C:\Users\X matches c:/users/x. Quotes are
    // stripped because users paste paths with surrounding quotes into the config.
    private static string Normalize(string path) => path.Trim().Trim('"', '\'').Replace('\\', '/').TrimEnd('/');
}
