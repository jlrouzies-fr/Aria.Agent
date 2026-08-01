using Aria.Bridge.Data;
using Aria.Bridge.Infrastructure;
using Aria.Bridge.Services.Noosphere;
using Aria.Shared;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

// Noosphere REST surface — used by the agent's builtin Inscribe/Probe/Contemplate tools (via
// BridgePostAsync) and by the Aria.Web nav flyout panel (via SendLocalRestAsync/LocalRestRequest).
public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this WebApplication app)
    {
        app.MapPost("/memory/inscribe", async (InscribeRequest req, NoosphereService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Content)) return Results.BadRequest("content required");
            var ingestId = await svc.EnqueueInscribeAsync(req.Content, req.Bank, ct);
            return Results.Accepted(value: new { ok = true, ingestId });
        });

        app.MapPost("/memory/probe", async (ProbeRequest req, NoosphereService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query)) return Results.BadRequest("query required");
            var maxTokens = req.MaxTokens is > 0 ? req.MaxTokens.Value : 4096;
            var probe = await svc.ProbeAsync(req.Query, req.Bank, maxTokens, ct);
            return Results.Ok(new
            {
                results = probe.Results.Select(r => new { id = r.Id, text = r.Text, score = r.Score, entities = r.Entities, createdAt = r.CreatedAt }),
                legs = new { vector = probe.Legs.Vector, fts = probe.Legs.Fts, graph = probe.Legs.Graph }
            });
        });

        app.MapPost("/memory/contemplate", async (ContemplateRequest req, NoosphereService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query)) return Results.BadRequest("query required");
            var (text, legs) = await svc.ContemplateAsync(req.Query, req.Bank, ct);
            return Results.Ok(new { text, legs = new { vector = legs.Vector, fts = legs.Fts, graph = legs.Graph } });
        });

        // Synthesis over a pre-gathered engram blob (no re-probe). Used by cross-node recall: the caller
        // fans Probe out across nodes, merges, then synthesises once on the LLM node.
        app.MapPost("/memory/synthesize", async (SynthesizeRequest req, NoosphereService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query)) return Results.BadRequest("query required");
            var text = await svc.SynthesizeAsync(req.Query, req.Blob ?? "", ct);
            return Results.Ok(new { text });
        });

        app.MapGet("/memory/engrams", async (int? offset, int? limit, string? entityId, string? q, string? bank, NoosphereService svc, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 20, 1, 200);
            var list = await svc.ListEngramsAsync(offset ?? 0, take, entityId, q, bank, ct);
            return Results.Ok(list.Select(e => new
            {
                id = e.Id, content = e.Content, timeAnchor = e.TimeAnchor, createdAt = e.CreatedAt,
                entities = e.Entities, hasEmbedding = e.HasEmbedding
            }));
        });

        app.MapDelete("/memory/engrams/{id}", async (string id, NoosphereService svc, CancellationToken ct) =>
        {
            var ok = await svc.DeleteEngramAsync(id, ct);
            return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        app.MapGet("/memory/entities", async (int? limit, string? bank, NoosphereService svc, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 500);
            var list = await svc.ListEntitiesAsync(take, bank, ct);
            return Results.Ok(list.Select(e => new { id = e.Id, name = e.Name, kind = e.Kind, engramCount = e.EngramCount }));
        });

        // Full entity graph (all entities + all relations) for the full-page /memory canvas.
        app.MapGet("/memory/graph", async (string? bank, NoosphereService svc, CancellationToken ct) =>
        {
            var graph = await svc.GetGraphAsync(bank, ct);
            return Results.Ok(new
            {
                nodes = graph.Nodes.Select(n => new { id = n.Id, name = n.Name, kind = n.Kind, engramCount = n.EngramCount, group = n.Group }),
                edges = graph.Edges.Select(e => new { from = e.From, to = e.To, relation = e.Relation })
            });
        });

        app.MapGet("/memory/stats", async (string? bank, NoosphereService svc, CancellationToken ct) =>
        {
            var stats = await svc.StatsAsync(bank, ct);
            // lastExtractionError is in-process (cleared on the next successful extract) — the web nav
            // polls it so a silent LM-Studio-down failure still lights a warning after Inscribe returns.
            var (extractErr, extractAt) = svc.LastExtractionFailure;
            return Results.Ok(new
            {
                engrams = stats.Engrams, entities = stats.Entities, links = stats.Links,
                pendingIngests = stats.PendingIngests, embeddedCount = stats.EmbeddedCount,
                embeddingsConfigured = stats.EmbeddingsConfigured, extractionConfigured = stats.ExtractionConfigured,
                rawIngests = stats.RawIngests,
                lastExtractionError = extractErr,
                lastExtractionErrorAt = extractAt
            });
        });

        // Re-runs extraction for every ingest that fell back to unstructured "raw" storage (e.g. because
        // the extraction call failed/timed out under the old uncapped-thinking-model behavior). Deletes
        // each item's raw engram first so reprocessing doesn't duplicate it, then requeues through the
        // normal worker path. Local-origin only — this walks/rewrites the node's own memory store.
        app.MapPost("/memory/reprocess-raw", async (HttpRequest req, NoosphereService svc, CancellationToken ct) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(req))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var count = await svc.ReprocessRawIngestsAsync(ct);
            return Results.Ok(new { ok = true, requeued = count });
        });

        app.MapGet("/memory/status", async (string? bank, NoosphereService svc, CancellationToken ct) =>
        {
            var stats = await svc.StatsAsync(bank, ct);
            return Results.Ok(new
            {
                ok = true,
                embeddingsConfigured = stats.EmbeddingsConfigured,
                extractionConfigured = stats.ExtractionConfigured,
                engrams = stats.Engrams,
                pendingIngests = stats.PendingIngests
            });
        });

        // GET /memory/config — current Noosphere channel selection plus available channels.
        app.MapGet("/memory/config", async (NoosphereConfigService cfg, BridgeDbContext db, CancellationToken ct) =>
        {
            var config = await cfg.GetConfigAsync(ct);
            var custom = await db.Channels.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
            var channels = new List<object>();
            foreach (var c in custom)
                channels.Add(new { name = c.Name, kind = "local", models = ChannelEndpoints.ParseModels(c.ModelsJson) });
            foreach (var p in PublicProviderCatalog.Providers)
                channels.Add(new { name = p.Name, kind = "public", models = p.DefaultModels });

            return Results.Ok(new
            {
                extractionChannelName = config.ExtractionChannelName,
                embeddingsChannelName = config.EmbeddingsChannelName,
                embeddingsEnabled = config.EmbeddingsEnabled,
                embeddingsModel = config.EmbeddingsModel,
                extractionModel = config.ExtractionModel,
                channels
            });
        });

        // PUT /memory/config — local-origin only (not tunnel-relayable); the server cannot change
        // which channel the bridge resolves for memory LLM calls.
        app.MapPut("/memory/config", async (HttpRequest req, SaveNoosphereConfigRequest dto, NoosphereConfigService cfg, CancellationToken ct) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(req))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            await cfg.SaveConfigAsync(new NoosphereConfigService.SaveRequest(
                dto.ExtractionChannelName, dto.EmbeddingsChannelName, dto.EmbeddingsEnabled, dto.EmbeddingsModel, dto.ExtractionModel), ct);
            return Results.Ok(new { ok = true });
        });

        // Anchors lead extraction toward known groupings (e.g. Terminal projects) — synced from
        // Aria.Web whenever the source config changes. "source" defaults to the one origin today.
        app.MapGet("/memory/anchors", async (string? bank, string? source, NoosphereService svc, CancellationToken ct) =>
        {
            var list = await svc.GetAnchorsAsync(bank, source ?? "terminal-project", ct);
            return Results.Ok(list.Select(a => new { name = a.Name, description = a.Description }));
        });

        app.MapPut("/memory/anchors", async (SyncAnchorsRequest req, NoosphereService svc, CancellationToken ct) =>
        {
            var anchors = (req.Anchors ?? []).Select(a => new NoosphereService.AnchorItem(a.Name, a.Description ?? "")).ToList();
            await svc.SyncAnchorsAsync(anchors, req.Bank, req.Source ?? "terminal-project", ct);
            return Results.Ok(new { ok = true, count = anchors.Count });
        });

        app.MapPost("/memory/entities/merge", async (MergeEntityRequest req, NoosphereService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.SourceId) || string.IsNullOrWhiteSpace(req.TargetId))
                return Results.BadRequest("sourceId and targetId required");
            var ok = await svc.MergeEntityAsync(req.SourceId, req.TargetId, ct);
            return ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { ok = false, error = "merge rejected" });
        });
    }
}

public record InscribeRequest(string Content, string? Bank = null);
public record ProbeRequest(string Query, string? Bank = null, int? MaxTokens = null);
public record ContemplateRequest(string Query, string? Bank = null);
public record SynthesizeRequest(string Query, string? Blob = null);
public record AnchorDto(string Name, string? Description);
public record SyncAnchorsRequest(List<AnchorDto>? Anchors, string? Bank = null, string? Source = null);
public record MergeEntityRequest(string SourceId, string TargetId);
public record SaveNoosphereConfigRequest(string? ExtractionChannelName, string? EmbeddingsChannelName, bool EmbeddingsEnabled, string? EmbeddingsModel = null, string? ExtractionModel = null);
