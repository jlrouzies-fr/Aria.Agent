using System.Text.Json;
using Aria.Harness.Core;
using Microsoft.Extensions.AI;

namespace Aria.Harness.Bridge;

/// <summary>
/// Cross-node Noosphere recall (<see cref="RecallScope.AllNodes"/>). Memory stores are node-local and
/// never replicated, so to let the agent remember what was inscribed on any of the soul's machines this
/// tool queries each connected node's <c>/memory/probe</c> directly and merges the results by score
/// (each node already fuses its own vector/FTS/graph legs, so pooling by the returned score approximates
/// a global ranking). Registered in place of the single-node <see cref="BridgeMcpTool"/> "Probe" tool
/// when the recall scope is AllNodes; falls back to querying just the LLM node when only one is connected.
///
/// "Contemplate" reuses the same fan-out for the probe stage, then synthesises once on the LLM node via
/// <c>/memory/synthesize</c>, so deliberation also draws on every node's memory.
/// </summary>
public sealed class FanOutMemoryTool : AIFunction
{
    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _jsonSchema;
    private readonly IHarnessRuntime _runtime;
    private readonly HarnessContext _context;
    private readonly string? _llmNodeId;
    private readonly bool _synthesize;
    private readonly bool _allNodes;

    public FanOutMemoryTool(
        string name, string? description, JsonElement jsonSchema,
        IHarnessRuntime runtime, HarnessContext context, string? llmNodeId, bool synthesize, bool allNodes)
    {
        _name = name;
        _description = description ?? "";
        _jsonSchema = jsonSchema;
        _runtime = runtime;
        _context = context;
        _llmNodeId = llmNodeId;
        _synthesize = synthesize;
        _allNodes = allNodes;
    }

    public override string Name => _name;
    public override string Description => _description;
    public override JsonElement JsonSchema => _jsonSchema;

    private const int MaxChars = 4096 * 4; // ~4096 token budget for the merged recall blob.

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var query = arguments.TryGetValue("query", out var q) ? q?.ToString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(query))
            return "// A query is required.";

        // Target nodes: with AllNodes, every connected node (memory is node-local, never replicated, so
        // this is the only way to reach memory inscribed on another machine); with ThisNode, only the LLM
        // node. Either way we probe via /memory/probe — a Benign, ungated control-plane read — so recall
        // never trips the Layer B seal gate (unlike the /tools/call built-in path, which is Sensitive).
        List<string> targets;
        if (_allNodes)
        {
            var nodes = (await _runtime.GetBridgeNodeIdsAsync(_context, cancellationToken)).ToList();
            targets = nodes.Count > 0 ? nodes : (_llmNodeId is null ? [] : [_llmNodeId]);
        }
        else
        {
            targets = _llmNodeId is null ? [] : [_llmNodeId];
        }
        targets = targets.Distinct().ToList();

        var probeBody = JsonSerializer.Serialize(new { query, maxTokens = 4096 });
        var pooled = new List<(string Text, double Score)>();

        // Query nodes in parallel; a single node being offline/erroring must not sink the whole recall,
        // but it also shouldn't vanish silently — a node whose probe call throws counts as "no vector
        // leg" below, same as one that responded but degraded to keyword/graph only, so the agent still
        // finds out something didn't work rather than just seeing fewer/no results with no explanation.
        var results = await Task.WhenAll(targets.Select(async nodeId =>
        {
            try
            {
                var json = await _runtime.BridgePostAsync(
                    "http://localhost:5741/memory/probe", probeBody, _context, cancellationToken, nodeId: nodeId);
                return ParseResults(json);
            }
            catch { return (Items: new List<(string, double)>(), VectorLegRan: false); }
        }));

        foreach (var r in results) pooled.AddRange(r.Items);
        var anyVectorLegRan = results.Any(r => r.VectorLegRan);
        var degradedNote = anyVectorLegRan
            ? ""
            : "\n\n// NOTE: semantic (embeddings) search did not run on any queried node for this probe — results reflect keyword + graph search only. Check each bridge's Memory tab / log for why.";

        if (pooled.Count == 0)
            return "// NO RECORDS FOUND in the Noosphere for that query." + degradedNote;

        // Merge by score desc; drop exact-duplicate texts (the same fact inscribed on two nodes), then
        // fill up to the token budget.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<string>();
        var budget = MaxChars;
        foreach (var (text, _) in pooled.OrderByDescending(x => x.Score))
        {
            if (string.IsNullOrWhiteSpace(text) || !seen.Add(text)) continue;
            if (merged.Count > 0 && budget - text.Length < 0) break;
            budget -= text.Length;
            merged.Add(text);
        }

        // Kept clean of the degraded-leg note — this is what actually goes into the synthesis prompt
        // below, and a meta-comment about tool internals has no business in that context.
        var blob = string.Join("\n\n", merged);
        if (!_synthesize) return blob + degradedNote;

        // Contemplate: synthesise the cross-node blob once, on the LLM node.
        try
        {
            var synthBody = JsonSerializer.Serialize(new { query, blob });
            var synthJson = await _runtime.BridgePostAsync(
                "http://localhost:5741/memory/synthesize", synthBody, _context, cancellationToken, nodeId: _llmNodeId);
            using var doc = JsonDocument.Parse(synthJson);
            if (doc.RootElement.TryGetProperty("text", out var t) && t.GetString() is { Length: > 0 } text)
                return text + degradedNote;
        }
        catch { /* synthesis unavailable — fall back to the raw merged engrams */ }

        return blob + degradedNote;
    }

    private static (List<(string Text, double Score)> Items, bool VectorLegRan) ParseResults(string json)
    {
        var list = new List<(string, double)>();
        var vectorLegRan = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("legs", out var legsEl) &&
                legsEl.TryGetProperty("vector", out var vecEl) && vecEl.ValueKind == JsonValueKind.True)
                vectorLegRan = true;
            if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return (list, vectorLegRan);
            foreach (var item in arr.EnumerateArray())
            {
                var text = item.TryGetProperty("text", out var te) ? te.GetString() ?? "" : "";
                var score = item.TryGetProperty("score", out var se) && se.TryGetDouble(out var s) ? s : 0.0;
                if (text.Length > 0) list.Add((text, score));
            }
        }
        catch { /* malformed node response — contributes nothing */ }
        return (list, vectorLegRan);
    }
}
