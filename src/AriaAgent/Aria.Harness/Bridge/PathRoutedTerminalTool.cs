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
    public sealed record Candidate(AIFunction Tool, string[] PathPrefixes);

    // Argument keys that can carry an absolute path, by convention of the builtin terminal tools.
    private static readonly string[] PathArgKeys = ["path", "working_dir", "base_dir", "pattern"];

    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _jsonSchema;
    private readonly IReadOnlyList<Candidate> _candidates;
    private readonly int _defaultIndex;

    public PathRoutedTerminalTool(IReadOnlyList<Candidate> candidates, int defaultIndex)
    {
        _candidates   = candidates;
        _defaultIndex = defaultIndex >= 0 && defaultIndex < candidates.Count ? defaultIndex : 0;
        var template  = candidates[0].Tool;
        _name         = template.Name;
        _description  = template.Description;
        _jsonSchema   = template.JsonSchema;
    }

    public override string Name => _name;
    public override string Description => _description;
    public override JsonElement JsonSchema => _jsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var target = Resolve(arguments);
        return await target.InvokeAsync(arguments, cancellationToken);
    }

    private AIFunction Resolve(AIFunctionArguments arguments)
    {
        var paths = PathArgKeys
            .Select(k => arguments.TryGetValue(k, out var v) ? AsString(v) : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Normalize(s!))
            .ToList();
        if (paths.Count == 0) return _candidates[_defaultIndex].Tool;

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
        return (best ?? _candidates[_defaultIndex]).Tool;
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
