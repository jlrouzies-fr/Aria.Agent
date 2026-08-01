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

        // Still queue — never drop the user's content — but the ack the model sees depends on whether
        // this node's extraction channel is currently healthy. A recent LM-Studio-down failure used to
        // ride along as a soft NOTE under a success string, and the model cheerfully claimed the
        // Archivum was sealed while the ingest fell back to opaque raw text (or sat broken).
        await _memoryService.EnqueueInscribeAsync(content, null, CancellationToken.None);
        var (text, isError) = FormatInscribeAck(
            _memoryService.LastExtractionFailure.Error,
            _memoryService.LastExtractionFailure.At,
            Environment.MachineName,
            DateTime.UtcNow);
        return new(text, isError);
    }

    /// <summary>
    /// Builds the Inscribe tool's model-facing ack. Pure so tests can assert the failure path without
    /// spinning up a bridge. A recent extraction failure elevates to <c>IsError</c> — the content was
    /// still queued, but the model must NOT claim structured memory was preserved.
    /// </summary>
    internal static (string Text, bool IsError) FormatInscribeAck(
        string? recentError, DateTime? recentErrorAt, string nodeLabel, DateTime utcNow)
    {
        var node = string.IsNullOrWhiteSpace(nodeLabel) ? "this node" : nodeLabel.Trim();
        var locality =
            $" Memory stays on node \"{node}\" only — it is not replicated to other bridges. " +
            "Open Noosphere for that device to verify.";

        if (recentError != null && recentErrorAt != null
            && utcNow - recentErrorAt.Value <= TimeSpan.FromMinutes(5))
        {
            return (
                "INSCRIBE DEGRADED on node \"" + node + "\": content was queued, but this node's " +
                "Noosphere extraction channel is currently failing (" + recentError + "). " +
                "The engram will likely land as unstructured raw text without entities/relations " +
                "until the extraction model is reachable again (e.g. start LM Studio / fix the URL " +
                "in the bridge Memory tab). Do NOT tell the user the Archivum was sealed or that " +
                "future sessions will recall this — tell them extraction is broken on this node and " +
                "they should fix it, then retry." + locality,
                true);
        }

        return (
            "Engram queued on node \"" + node + "\" for Noosphere extraction. " +
            "Structured facts appear after the local extraction model finishes — check that node's " +
            "Memory tab if nothing shows up." + locality,
            false);
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
