using System.Text.Json;
using Aria.Bridge.Services.Noosphere;
using Microsoft.Extensions.DependencyInjection;

namespace Aria.Bridge;

// Node-side Noosphere memory tools (Inscribe/Probe/Contemplate). Running these on the bridge means
// memories are written and read on the user's own machine — the server never sees them — and both
// Aria.Web and Aria.Console get them through the same bridge tool path (live tool blocks, tunnel
// routing). Deliberately NOT added to GetToolInfos(): the terminal tool's /tools/list would
// otherwise leak these into every session; Harness registers them explicitly (case "memory").
public static partial class BuiltinTools
{
    private static NoosphereService? _memoryService;

    public static void ConfigureMemory(IServiceProvider services) =>
        _memoryService = services.GetRequiredService<NoosphereService>();

    private static async Task<ToolCallResponse> InscribeToolAsync(Dictionary<string, JsonElement> args)
    {
        if (_memoryService == null) return new("Noosphere is not available on this node.", true);
        var content = args.Str("content") ?? throw new ArgumentException("'content' is required");
        await _memoryService.EnqueueInscribeAsync(content, null, CancellationToken.None);
        // Inscribe is fire-and-forget (extraction happens on a background worker after this call already
        // returns), so THIS engram's own outcome isn't known yet. A recent extraction failure on the same
        // channel is a strong signal the same thing is about to happen again — surface it as a heads-up
        // rather than silently letting every inscribe look identically successful.
        return new("Engram committed to the Noosphere. The Archivum shall preserve this truth." + RecentExtractionFailureNote(_memoryService), false);
    }

    private static string RecentExtractionFailureNote(NoosphereService svc)
    {
        var (error, at) = svc.LastExtractionFailure;
        if (error == null || at == null || DateTime.UtcNow - at.Value > TimeSpan.FromMinutes(5)) return "";
        return $"\n\n// NOTE: the last extraction attempt on this node failed ({error}) — this engram may have been stored as unstructured raw text instead of parsed facts. Check the bridge's Memory tab.";
    }

    // Embeddings can fail silently at the HTTP layer (a misconfigured/unreachable channel still returns
    // a 200 with no vectors — see NoosphereEmbedder) and NoosphereService already degrades gracefully to
    // keyword+graph search when that happens. That's the right behavior for the *search itself*, but the
    // degradation was previously invisible to the agent — Probe/Contemplate just returned normal-looking
    // results with no hint that semantic recall didn't run. Appending this note whenever the vector leg
    // didn't fire means the model (and, if it chooses to mention it, the user) actually finds out — with
    // the real reason (e.g. the target server's own "model not found" text) when one is available.
    private static string VectorLegDegradedNote(NoosphereService svc)
    {
        if (!svc.EmbeddingsEnabled)
            return "\n\n// NOTE: semantic (embeddings) search is disabled in this node's Memory config — results reflect keyword + graph search only.";
        var (error, _) = svc.LastEmbeddingFailure;
        return error != null
            ? $"\n\n// NOTE: semantic (embeddings) search did not run for this probe — results reflect keyword + graph search only. Reason: {error}"
            : "\n\n// NOTE: semantic (embeddings) search did not run for this probe — results reflect keyword + graph search only. Check the bridge's Memory tab / log for why.";
    }

    private static async Task<ToolCallResponse> ProbeToolAsync(Dictionary<string, JsonElement> args)
    {
        if (_memoryService == null) return new("Noosphere is not available on this node.", true);
        var query = args.Str("query") ?? throw new ArgumentException("'query' is required");
        var probe = await _memoryService.ProbeAsync(query, null, 4096, CancellationToken.None);
        var text = probe.Results.Count == 0
            ? "// NO RECORDS FOUND in the Noosphere for that query."
            : string.Join("\n\n", probe.Results.Select(r => r.Text));
        if (!probe.Legs.Vector) text += VectorLegDegradedNote(_memoryService);
        return new(text, false);
    }

    private static async Task<ToolCallResponse> ContemplateToolAsync(Dictionary<string, JsonElement> args)
    {
        if (_memoryService == null) return new("Noosphere is not available on this node.", true);
        var query = args.Str("query") ?? throw new ArgumentException("'query' is required");
        var (text, legs) = await _memoryService.ContemplateAsync(query, null, CancellationToken.None);
        return new(legs.Vector ? text : text + VectorLegDegradedNote(_memoryService), false);
    }
}
