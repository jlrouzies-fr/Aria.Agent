using System.Collections.Concurrent;
using System.Numerics.Tensors;
using System.Threading.Channels;
using Aria.Bridge.Data;
using Aria.Bridge.Services.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aria.Bridge.Services.Noosphere;

// Facade over the Noosphere memory store: Inscribe (LLM extraction → engrams/entities/links),
// Probe (vector + FTS + graph legs merged by reciprocal rank fusion), Contemplate (probe + synthesis).
// One SQLite vault per bridge; rows scoped by (SoulId, Bank) — see docs/ideas/noosphere-native-memory-plan.md.
public class NoosphereService(
    IServiceScopeFactory scopeFactory,
    NoosphereExtractor extractor,
    NoosphereEmbedder embedder,
    NoosphereConfigService configService,
    NoosphereBuiltinRuntime builtinRuntime,
    ILogger<NoosphereService> logger)
{
    private readonly Channel<string> _ingestChannel = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, List<(string Id, float[] Vec)>> _vectorCache = new();

    // Pass-throughs so the builtin Memory tools (and the nav panel) can turn a silent extraction/
    // embedding failure into an actual message instead of a generic "check the log" note.
    public bool EmbeddingsEnabled => embedder.Enabled;
    public (string? Error, DateTime? At) LastEmbeddingFailure => (embedder.LastError, embedder.LastErrorAt);
    public (string? Error, DateTime? At) LastExtractionFailure => (extractor.LastError, extractor.LastErrorAt);

    public ChannelReader<string> IngestReader => _ingestChannel.Reader;
    public Task RequeueIngestAsync(string ingestId, CancellationToken ct) =>
        _ingestChannel.Writer.WriteAsync(ingestId, ct).AsTask();

    private static string NormalizeBank(string? bank) => string.IsNullOrWhiteSpace(bank) ? "default" : bank.Trim();

    private static async Task<string> GetActiveSoulIdAsync(BridgeDbContext db, CancellationToken ct)
    {
        var soul = await db.Souls.AsNoTracking().FirstOrDefaultAsync(s => s.Name != "", ct)
                   ?? await db.Souls.AsNoTracking().FirstOrDefaultAsync(ct);
        return soul?.Id ?? "";
    }

    private static string CacheKey(string soulId, string bank) => $"{soulId}|{bank}";
    private void InvalidateVectorCache(string soulId, string bank) => _vectorCache.TryRemove(CacheKey(soulId, bank), out _);

    // ── Inscribe ──────────────────────────────────────────────────────────────

    public async Task<string> EnqueueInscribeAsync(string content, string? bank, CancellationToken ct)
    {
        bank = NormalizeBank(bank);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var soulId = await GetActiveSoulIdAsync(db, ct);

        var ingest = new MemoryIngest { SoulId = soulId, Bank = bank, Content = content };
        db.MemoryIngests.Add(ingest);
        await db.SaveChangesAsync(ct);
        await _ingestChannel.Writer.WriteAsync(ingest.Id, ct);
        BridgeLogger.Log("INFO",
            $"Noosphere Inscribe queued ({content.Length} chars, bank={bank}, id={ingest.Id[..Math.Min(8, ingest.Id.Length)]})");
        return ingest.Id;
    }

    public async Task<List<string>> GetPendingIngestIdsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        return await db.MemoryIngests.AsNoTracking()
            .Where(i => i.Status == "pending" || i.Status == "error")
            .Select(i => i.Id)
            .ToListAsync(ct);
    }

    public async Task ProcessIngestAsync(string ingestId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var ingest = await db.MemoryIngests.FirstOrDefaultAsync(i => i.Id == ingestId, ct);
        if (ingest == null) return;

        List<NoosphereExtractor.ExtractedFact>? facts = null;
        try
        {
            var known = await GetKnownEntitiesForPromptAsync(db, ingest.SoulId, ingest.Bank, ingest.Content, ct);
            var anchors = await db.MemoryAnchors.AsNoTracking()
                .Where(a => a.SoulId == ingest.SoulId && a.Bank == ingest.Bank)
                .Select(a => new { a.Name, a.Description })
                .ToListAsync(ct);
            facts = await extractor.ExtractAsync(ingest.Content, known,
                anchors.Select(a => (a.Name, a.Description)).ToList(), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Noosphere] Extraction threw for ingest {Id}", ingestId);
            BridgeLogger.Log("ERROR", $"Noosphere extraction threw for ingest {ingestId[..Math.Min(8, ingestId.Length)]}: {ex.Message}");
        }

        try
        {
            if (facts == null || facts.Count == 0)
            {
                // No extraction available/succeeded — never lose data: keep the raw content as one engram.
                var raw = new Engram { SoulId = ingest.SoulId, Bank = ingest.Bank, IngestId = ingest.Id, Content = ingest.Content };
                db.Engrams.Add(raw);
                await db.SaveChangesAsync(ct);
                await EmbedEngramsAsync(db, [raw], ct);
                ingest.Status = "raw";
                var why = extractor.LastError ?? "no usable facts";
                BridgeLogger.Log("WARN",
                    $"Noosphere ingest {ingestId[..Math.Min(8, ingestId.Length)]} stored as raw — {why}");
            }
            else
            {
                var entityCache = new Dictionary<string, MemoryEntity>(StringComparer.OrdinalIgnoreCase);
                var newEngrams = new List<Engram>();

                foreach (var fact in facts)
                {
                    var engram = new Engram
                    {
                        SoulId = ingest.SoulId, Bank = ingest.Bank, IngestId = ingest.Id,
                        Content = fact.Content, TimeAnchor = fact.TimeAnchor
                    };
                    db.Engrams.Add(engram);
                    newEngrams.Add(engram);

                    foreach (var ent in fact.Entities)
                    {
                        var canonical = ent.Name.Trim().ToLowerInvariant();
                        if (canonical.Length == 0) continue;
                        if (!entityCache.TryGetValue(canonical, out var entity))
                        {
                            entity = await db.MemoryEntities.FirstOrDefaultAsync(
                                e => e.SoulId == ingest.SoulId && e.Bank == ingest.Bank && e.CanonicalName == canonical, ct);
                            if (entity == null)
                            {
                                entity = new MemoryEntity
                                {
                                    SoulId = ingest.SoulId, Bank = ingest.Bank,
                                    Name = ent.Name.Trim(), CanonicalName = canonical, Kind = ent.Kind
                                };
                                db.MemoryEntities.Add(entity);
                            }
                            entityCache[canonical] = entity;
                        }
                        db.EngramEntities.Add(new EngramEntity { EngramId = engram.Id, EntityId = entity.Id });
                    }

                    foreach (var rel in fact.Relations)
                    {
                        var fromCanon = rel.From.Trim().ToLowerInvariant();
                        var toCanon = rel.To.Trim().ToLowerInvariant();
                        if (!entityCache.TryGetValue(fromCanon, out var fromEnt) || !entityCache.TryGetValue(toCanon, out var toEnt))
                            continue; // relation references an entity not declared in this fact's entities[]
                        db.EntityLinks.Add(new EntityLink
                        {
                            SoulId = ingest.SoulId, Bank = ingest.Bank,
                            FromEntityId = fromEnt.Id, ToEntityId = toEnt.Id,
                            Relation = rel.Relation, EngramId = engram.Id
                        });
                    }
                }

                await db.SaveChangesAsync(ct);
                await EmbedEngramsAsync(db, newEngrams, ct);
                ingest.Status = "done";
                BridgeLogger.Log("INFO",
                    $"Noosphere ingest {ingestId[..Math.Min(8, ingestId.Length)]} done — {facts.Count} fact(s), {newEngrams.Count} engram(s)");
            }
        }
        catch (Exception ex)
        {
            ingest.Status = "error";
            ingest.Error = ex.Message;
            logger.LogError(ex, "[Noosphere] Ingest {Id} failed", ingestId);
            BridgeLogger.Log("ERROR", $"Noosphere ingest {ingestId[..Math.Min(8, ingestId.Length)]} failed: {ex.Message}");
        }

        ingest.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        InvalidateVectorCache(ingest.SoulId, ingest.Bank);
    }

    // Ingests that fell back to "raw" (unstructured single-engram storage, no entities/relations) — see
    // the fallback branch in ProcessIngestAsync above — were mostly a symptom of the extraction call
    // running uncapped against a thinking model (fixed in NoosphereExtractor). Re-running them now that
    // extraction is fixed lets the backlog pick up proper entity/relation structure instead of staying
    // stuck as opaque blobs forever. Deletes each item's old raw engram first (via the same cleanup path
    // DeleteEngramAsync uses) so re-extraction doesn't duplicate it alongside the new structured facts.
    public async Task<int> ReprocessRawIngestsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var rawIngestIds = await db.MemoryIngests.AsNoTracking()
            .Where(i => i.Status == "raw")
            .Select(i => i.Id)
            .ToListAsync(ct);

        foreach (var ingestId in rawIngestIds)
        {
            var oldEngramIds = await db.Engrams.Where(e => e.IngestId == ingestId).Select(e => e.Id).ToListAsync(ct);
            foreach (var engramId in oldEngramIds)
                await DeleteEngramAsync(engramId, ct);

            var ingest = await db.MemoryIngests.FirstOrDefaultAsync(i => i.Id == ingestId, ct);
            if (ingest != null) ingest.Status = "pending";
        }
        await db.SaveChangesAsync(ct);

        foreach (var ingestId in rawIngestIds)
            await RequeueIngestAsync(ingestId, ct);

        return rawIngestIds.Count;
    }

    // Feeds existing entity names into the extraction prompt so the model reuses exact names instead
    // of inventing variants (the source of naming drift like "Spectra" / "Spectra project" / "Spectra
    // Web UI" all meaning the same thing). Capped for prompt budget: below the threshold, send
    // everything; above it, prefer entities that share a token with the ingest text plus the busiest
    // entities overall, since those are the ones most likely to recur.
    private const int KnownEntitiesFullThreshold = 150;
    private const int KnownEntitiesTopByUsage = 50;

    private static async Task<List<(string Name, string? Kind)>> GetKnownEntitiesForPromptAsync(
        BridgeDbContext db, string soulId, string bank, string ingestContent, CancellationToken ct)
    {
        var all = await db.MemoryEntities.AsNoTracking()
            .Where(e => e.SoulId == soulId && e.Bank == bank)
            .Select(e => new { e.Id, e.Name, e.Kind })
            .ToListAsync(ct);
        if (all.Count == 0) return [];
        if (all.Count <= KnownEntitiesFullThreshold)
            return all.Select(e => (e.Name, e.Kind)).ToList();

        var contentTokens = System.Text.RegularExpressions.Regex.Matches(ingestContent.ToLowerInvariant(), @"[\w]{3,}")
            .Select(m => m.Value).ToHashSet();
        var overlap = all.Where(e => contentTokens.Contains(e.Name.ToLowerInvariant())
            || e.Name.ToLowerInvariant().Split(' ').Any(contentTokens.Contains)).ToList();

        var counts = await db.EngramEntities.AsNoTracking()
            .GroupBy(x => x.EntityId)
            .Select(g => new { EntityId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.EntityId, x => x.Count, ct);
        var busiest = all.OrderByDescending(e => counts.GetValueOrDefault(e.Id)).Take(KnownEntitiesTopByUsage);

        return overlap.Concat(busiest).DistinctBy(e => e.Id).Select(e => (e.Name, e.Kind)).ToList();
    }

    private async Task EmbedEngramsAsync(BridgeDbContext db, List<Engram> engrams, CancellationToken ct)
    {
        if (engrams.Count == 0 || !embedder.Enabled) return;
        var result = await embedder.EmbedBatchAsync(engrams.Select(e => e.Content).ToList(), ct);
        if (result == null) return; // embeddings unavailable — engrams remain FTS/graph-only until backfilled
        for (var i = 0; i < engrams.Count && i < result.Vectors.Count; i++)
        {
            engrams[i].Embedding = NoosphereEmbedder.Encode(result.Vectors[i]);
            engrams[i].EmbeddingModel = result.Model;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<string>> GetEngramIdsNeedingEmbeddingAsync(CancellationToken ct)
    {
        if (!embedder.Enabled) return [];
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        string? modelId;
        if (await configService.IsBuiltinActiveAsync(builtinRuntime, ct))
            modelId = NoosphereBuiltinCatalog.ModelIdFor(NoosphereBuiltinCatalog.RoleEmbed);
        else
        {
            var channel = await NoosphereChannelResolver.ResolveAsync(await configService.GetEmbeddingOptionsAsync(ct), db, ct);
            if (channel == null) return [];
            modelId = channel.Model;
        }
        return await db.Engrams.AsNoTracking()
            .Where(e => e.Embedding == null || e.EmbeddingModel != modelId)
            .Select(e => e.Id)
            .ToListAsync(ct);
    }

    public async Task BackfillEmbeddingAsync(string engramId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var engram = await db.Engrams.FirstOrDefaultAsync(e => e.Id == engramId, ct);
        if (engram == null) return;
        await EmbedEngramsAsync(db, [engram], ct);
        InvalidateVectorCache(engram.SoulId, engram.Bank);
    }

    // ── Probe ────────────────────────────────────────────────────────────────

    public record ProbeResultItem(string Id, string Text, double Score, List<string> Entities, DateTime CreatedAt);
    public record ProbeLegs(bool Vector, bool Fts, bool Graph);
    public record ProbeResult(List<ProbeResultItem> Results, ProbeLegs Legs);

    public async Task<ProbeResult> ProbeAsync(string query, string? bank, int maxTokens, CancellationToken ct)
    {
        bank = NormalizeBank(bank);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var soulId = await GetActiveSoulIdAsync(db, ct);

        var vectorRanked = new List<string>();
        var ftsRanked = new List<string>();
        var graphRanked = new List<string>();
        var vectorLegRan = false;
        var graphLegRan = false;

        // Vector leg
        if (embedder.Enabled)
        {
            try
            {
                using var embedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                embedCts.CancelAfter(TimeSpan.FromSeconds(8));
                var embedResult = await embedder.EmbedBatchAsync([query], embedCts.Token);
                if (embedResult is { Vectors.Count: > 0 })
                {
                    vectorLegRan = true;
                    var qVec = embedResult.Vectors[0];
                    var cache = await GetVectorCacheAsync(db, soulId, bank, ct);
                    vectorRanked = cache
                        .Where(v => v.Vec.Length == qVec.Length)
                        .Select(v => (v.Id, Score: TensorPrimitives.CosineSimilarity(qVec, v.Vec)))
                        .OrderByDescending(x => x.Score)
                        .Take(32)
                        .Select(x => x.Id)
                        .ToList();
                }
            }
            catch { /* embedder timeout/failure — recall degrades to fts/graph */ }
        }

        // FTS / keyword leg
        var tokens = System.Text.RegularExpressions.Regex.Matches(query.ToLowerInvariant(), @"[\w]+")
            .Select(m => m.Value).Where(t => t.Length > 1).Distinct().Take(12).ToList();
        if (tokens.Count > 0)
        {
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync(ct);
            try
            {
                if (NoosphereCapabilities.FtsAvailable)
                {
                    var matchExpr = string.Join(" OR ", tokens.Select(t => $"\"{t.Replace("\"", "")}\""));
                    await using var cmd = conn.CreateCommand();
                    // FTS5's MATCH/bm25() must reference the virtual table's real name, not a JOIN alias.
                    cmd.CommandText = """
                        SELECT e.Id FROM EngramsFts f
                        JOIN Engrams e ON e.Id = f.EngramId
                        WHERE EngramsFts MATCH @m AND e.SoulId = @soulId AND e.Bank = @bank
                        ORDER BY bm25(EngramsFts) LIMIT 32;
                    """;
                    AddParam(cmd, "@m", matchExpr); AddParam(cmd, "@soulId", soulId); AddParam(cmd, "@bank", bank);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) ftsRanked.Add(r.GetString(0));
                }
                else
                {
                    await using var cmd = conn.CreateCommand();
                    var likeClauses = tokens.Select((_, i) => $"Content LIKE @t{i}").ToList();
                    cmd.CommandText = $"""
                        SELECT Id FROM Engrams WHERE SoulId=@soulId AND Bank=@bank AND ({string.Join(" OR ", likeClauses)})
                        ORDER BY CreatedAt DESC LIMIT 32;
                    """;
                    AddParam(cmd, "@soulId", soulId); AddParam(cmd, "@bank", bank);
                    for (var i = 0; i < tokens.Count; i++) AddParam(cmd, $"@t{i}", $"%{tokens[i]}%");
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) ftsRanked.Add(r.GetString(0));
                }
            }
            finally { await conn.CloseAsync(); }
        }

        // Graph leg: seed entities whose canonical name appears in the query, expand 1 hop.
        var lowerQuery = query.ToLowerInvariant();
        var allEntities = await db.MemoryEntities.AsNoTracking()
            .Where(e => e.SoulId == soulId && e.Bank == bank)
            .Select(e => new { e.Id, e.CanonicalName })
            .ToListAsync(ct);
        var seedIds = allEntities
            .Where(e => e.CanonicalName.Length > 1 && lowerQuery.Contains(e.CanonicalName))
            .Select(e => e.Id)
            .ToHashSet();

        if (seedIds.Count > 0)
        {
            graphLegRan = true;
            var weights = seedIds.ToDictionary(id => id, _ => 1.0);

            var links = await db.EntityLinks.AsNoTracking()
                .Where(l => l.SoulId == soulId && l.Bank == bank && (seedIds.Contains(l.FromEntityId) || seedIds.Contains(l.ToEntityId)))
                .ToListAsync(ct);
            foreach (var link in links)
            {
                if (seedIds.Contains(link.FromEntityId) && !weights.ContainsKey(link.ToEntityId)) weights[link.ToEntityId] = 0.5;
                if (seedIds.Contains(link.ToEntityId) && !weights.ContainsKey(link.FromEntityId)) weights[link.FromEntityId] = 0.5;
            }

            var entityIds = weights.Keys.ToList();
            var engramLinks = await db.EngramEntities.AsNoTracking()
                .Where(x => entityIds.Contains(x.EntityId))
                .ToListAsync(ct);

            var engramScores = new Dictionary<string, double>();
            foreach (var link in engramLinks)
                engramScores[link.EngramId] = engramScores.GetValueOrDefault(link.EngramId) + weights[link.EntityId];

            var candidateIds = engramScores.Keys.ToList();
            var candidateEngrams = await db.Engrams.AsNoTracking()
                .Where(e => candidateIds.Contains(e.Id) && e.SoulId == soulId && e.Bank == bank)
                .Select(e => new { e.Id, e.CreatedAt })
                .ToListAsync(ct);

            graphRanked = candidateEngrams
                .OrderByDescending(e => engramScores[e.Id])
                .ThenByDescending(e => e.CreatedAt)
                .Take(32)
                .Select(e => e.Id)
                .ToList();
        }

        // Reciprocal rank fusion
        var rrf = new Dictionary<string, double>();
        void Accumulate(List<string> ranked)
        {
            for (var i = 0; i < ranked.Count; i++)
                rrf[ranked[i]] = rrf.GetValueOrDefault(ranked[i]) + 1.0 / (60 + i + 1);
        }
        Accumulate(vectorRanked); Accumulate(ftsRanked); Accumulate(graphRanked);

        var legs = new ProbeLegs(vectorLegRan, tokens.Count > 0, graphLegRan);
        var mergedIds = rrf.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        if (mergedIds.Count == 0) return new ProbeResult([], legs);

        var engramsById = await db.Engrams.AsNoTracking()
            .Where(e => mergedIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);

        var entityNamesByEngram = await (
            from ee in db.EngramEntities.AsNoTracking()
            join me in db.MemoryEntities.AsNoTracking() on ee.EntityId equals me.Id
            where mergedIds.Contains(ee.EngramId)
            select new { ee.EngramId, me.Name }
        ).ToListAsync(ct);
        var entityLookup = entityNamesByEngram.GroupBy(x => x.EngramId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).ToList());

        var results = new List<ProbeResultItem>();
        var tokenBudget = maxTokens;
        foreach (var id in mergedIds)
        {
            if (!engramsById.TryGetValue(id, out var engram)) continue;
            var estTokens = Math.Max(1, engram.Content.Length / 4);
            if (results.Count > 0 && tokenBudget - estTokens < 0) break;
            tokenBudget -= estTokens;
            results.Add(new ProbeResultItem(engram.Id, engram.Content, rrf[id], entityLookup.GetValueOrDefault(id, []), engram.CreatedAt));
        }

        return new ProbeResult(results, legs);
    }

    private async Task<List<(string Id, float[] Vec)>> GetVectorCacheAsync(BridgeDbContext db, string soulId, string bank, CancellationToken ct)
    {
        var key = CacheKey(soulId, bank);
        if (_vectorCache.TryGetValue(key, out var cached)) return cached;

        var rows = await db.Engrams.AsNoTracking()
            .Where(e => e.SoulId == soulId && e.Bank == bank && e.Embedding != null)
            .Select(e => new { e.Id, e.Embedding })
            .ToListAsync(ct);

        var list = rows.Select(r => (r.Id, NoosphereEmbedder.Decode(r.Embedding!))).ToList();
        _vectorCache[key] = list;
        return list;
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    // ── Contemplate ───────────────────────────────────────────────────────────

    public async Task<(string Text, ProbeLegs Legs)> ContemplateAsync(string query, string? bank, CancellationToken ct)
    {
        var probe = await ProbeAsync(query, bank, maxTokens: 3000, ct);
        if (probe.Results.Count == 0)
            return ("// THE ARCHIVUM IS SILENT — no engrams found relevant to that query.", probe.Legs);

        var blob = string.Join("\n\n", probe.Results.Select(r => $"- {r.Text}"));
        var text = await extractor.ContemplateSynthesisAsync(query, blob, ct);
        return (text ?? ContemplationFallbackMessage(), probe.Legs);
    }

    /// <summary>Synthesis-only half of Contemplate: reason over an already-gathered engram blob without
    /// re-probing. Used for cross-node recall (<c>RecallScope.AllNodes</c>) — the caller fans the probe
    /// out across nodes, merges, then synthesises once here on the LLM node.</summary>
    public async Task<string> SynthesizeAsync(string query, string blob, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blob))
            return "// THE ARCHIVUM IS SILENT — no engrams supplied for contemplation.";
        var text = await extractor.ContemplateSynthesisAsync(query, blob, ct);
        return text ?? ContemplationFallbackMessage();
    }

    // Called immediately after a failed ContemplateSynthesisAsync, so extractor.LastError (if any)
    // reflects THIS call's own failure — distinguishes "no channel configured" from "channel configured
    // but the call actually failed" (e.g. the model was removed from the local server).
    private string ContemplationFallbackMessage() =>
        extractor.LastError != null
            ? $"// CONTEMPLATION COGITATOR FAULT — {extractor.LastError}"
            : "// CONTEMPLATION COGITATOR OFFLINE — no extraction channel configured on this node.";

    // ── Listing / management (nav panel) ─────────────────────────────────────

    public record EngramListItem(string Id, string Content, string? TimeAnchor, DateTime CreatedAt, List<string> Entities, bool HasEmbedding);

    public async Task<List<EngramListItem>> ListEngramsAsync(int offset, int limit, string? entityId, string? q, string? bank, CancellationToken ct)
    {
        bank = NormalizeBank(bank);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var soulId = await GetActiveSoulIdAsync(db, ct);

        var query = db.Engrams.AsNoTracking().Where(e => e.SoulId == soulId && e.Bank == bank);
        if (!string.IsNullOrWhiteSpace(entityId))
            query = query.Where(e => db.EngramEntities.Any(x => x.EngramId == e.Id && x.EntityId == entityId));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var lower = q.Trim().ToLower();
            query = query.Where(e => e.Content.ToLower().Contains(lower));
        }

        var page = await query.OrderByDescending(e => e.CreatedAt).Skip(offset).Take(limit).ToListAsync(ct);
        var ids = page.Select(e => e.Id).ToList();
        var entityNames = await (
            from ee in db.EngramEntities.AsNoTracking()
            join me in db.MemoryEntities.AsNoTracking() on ee.EntityId equals me.Id
            where ids.Contains(ee.EngramId)
            select new { ee.EngramId, me.Name }
        ).ToListAsync(ct);
        var lookup = entityNames.GroupBy(x => x.EngramId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).ToList());

        return page.Select(e => new EngramListItem(e.Id, e.Content, e.TimeAnchor, e.CreatedAt, lookup.GetValueOrDefault(e.Id, []), e.Embedding != null)).ToList();
    }

    public async Task<bool> DeleteEngramAsync(string id, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var engram = await db.Engrams.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (engram == null) return false;

        var linkedEntityIds = await db.EngramEntities.Where(x => x.EngramId == id).Select(x => x.EntityId).ToListAsync(ct);
        db.Engrams.Remove(engram); // cascades EngramEntities via FK
        await db.SaveChangesAsync(ct);

        foreach (var entityId in linkedEntityIds)
        {
            var stillUsed = await db.EngramEntities.AnyAsync(x => x.EntityId == entityId, ct);
            if (stillUsed) continue;
            await db.EntityLinks.Where(l => l.FromEntityId == entityId || l.ToEntityId == entityId).ExecuteDeleteAsync(ct);
            await db.MemoryEntities.Where(e => e.Id == entityId).ExecuteDeleteAsync(ct);
        }

        InvalidateVectorCache(engram.SoulId, engram.Bank);
        return true;
    }

    public record MemoryEntityListItem(string Id, string Name, string? Kind, int EngramCount);

    public async Task<List<MemoryEntityListItem>> ListEntitiesAsync(int limit, string? bank, CancellationToken ct)
    {
        bank = NormalizeBank(bank);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var soulId = await GetActiveSoulIdAsync(db, ct);

        var counts = await (
            from me in db.MemoryEntities.AsNoTracking()
            where me.SoulId == soulId && me.Bank == bank
            select new
            {
                me.Id, me.Name, me.Kind,
                EngramCount = db.EngramEntities.Count(x => x.EntityId == me.Id)
            }
        ).OrderByDescending(x => x.EngramCount).Take(limit).ToListAsync(ct);

        return counts.Select(x => new MemoryEntityListItem(x.Id, x.Name, x.Kind, x.EngramCount)).ToList();
    }

    // ── Anchors (lead extraction toward known groupings, e.g. Terminal projects) ─────────────

    public record AnchorItem(string Name, string Description);

    public async Task<List<AnchorItem>> GetAnchorsAsync(string? bank, string source, CancellationToken ct)
    {
        bank = NormalizeBank(bank);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var soulId = await GetActiveSoulIdAsync(db, ct);

        return await db.MemoryAnchors.AsNoTracking()
            .Where(a => a.SoulId == soulId && a.Bank == bank && a.Source == source)
            .OrderBy(a => a.Name)
            .Select(a => new AnchorItem(a.Name, a.Description))
            .ToListAsync(ct);
    }

    /// <summary>Replace-all sync for one anchor source (e.g. Terminal projects) — called from Aria.Web
    /// whenever the source config changes, so renamed/removed projects don't linger as stale anchors.</summary>
    public async Task SyncAnchorsAsync(List<AnchorItem> anchors, string? bank, string source, CancellationToken ct)
    {
        bank = NormalizeBank(bank);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var soulId = await GetActiveSoulIdAsync(db, ct);

        var existing = await db.MemoryAnchors
            .Where(a => a.SoulId == soulId && a.Bank == bank && a.Source == source)
            .ToListAsync(ct);

        var incomingNames = anchors.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        db.MemoryAnchors.RemoveRange(existing.Where(e => !incomingNames.Contains(e.Name)));

        foreach (var a in anchors)
        {
            if (string.IsNullOrWhiteSpace(a.Name)) continue;
            var row = existing.FirstOrDefault(e => string.Equals(e.Name, a.Name, StringComparison.OrdinalIgnoreCase));
            if (row == null)
            {
                db.MemoryAnchors.Add(new MemoryAnchor
                {
                    SoulId = soulId, Bank = bank, Name = a.Name.Trim(), Description = a.Description ?? "", Source = source
                });
            }
            else if (row.Description != a.Description)
            {
                row.Description = a.Description ?? "";
                row.UpdatedAt = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Entity merge (duplicate cleanup) ─────────────────────────────────────────────────────

    public async Task<bool> MergeEntityAsync(string sourceId, string targetId, CancellationToken ct)
    {
        if (sourceId == targetId) return false;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();

        var source = await db.MemoryEntities.FirstOrDefaultAsync(e => e.Id == sourceId, ct);
        var target = await db.MemoryEntities.FirstOrDefaultAsync(e => e.Id == targetId, ct);
        if (source == null || target == null || source.SoulId != target.SoulId || source.Bank != target.Bank)
            return false;

        // Re-point engram links. EntityId is part of EngramEntity's composite key, so EF forbids
        // mutating it in place — delete the old row and add a new one instead (skip if the engram
        // already links to the target, to avoid a duplicate key).
        var sourceLinks = await db.EngramEntities.Where(x => x.EntityId == sourceId).ToListAsync(ct);
        foreach (var link in sourceLinks)
        {
            var dup = await db.EngramEntities.AnyAsync(x => x.EngramId == link.EngramId && x.EntityId == targetId, ct);
            db.EngramEntities.Remove(link);
            if (!dup) db.EngramEntities.Add(new EngramEntity { EngramId = link.EngramId, EntityId = targetId });
        }

        // Re-point relation endpoints, drop links that became self-loops or duplicates of an
        // existing (From,To,Relation) triple.
        var relLinks = await db.EntityLinks
            .Where(l => l.FromEntityId == sourceId || l.ToEntityId == sourceId)
            .ToListAsync(ct);
        var keptTriples = await db.EntityLinks
            .Where(l => (l.FromEntityId == targetId || l.ToEntityId == targetId) && l.FromEntityId != sourceId && l.ToEntityId != sourceId)
            .Select(l => new { l.FromEntityId, l.ToEntityId, l.Relation })
            .ToListAsync(ct);
        var seen = keptTriples.Select(t => (t.FromEntityId, t.ToEntityId, t.Relation)).ToHashSet();

        foreach (var link in relLinks)
        {
            var from = link.FromEntityId == sourceId ? targetId : link.FromEntityId;
            var to = link.ToEntityId == sourceId ? targetId : link.ToEntityId;
            if (from == to || !seen.Add((from, to, link.Relation))) { db.EntityLinks.Remove(link); continue; }
            link.FromEntityId = from;
            link.ToEntityId = to;
        }

        db.MemoryEntities.Remove(source);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Wipe (Data tab "Wipe Noosphere") ──────────────────────────────────────────────────────

    public void ClearAllCaches() => _vectorCache.Clear();

    public record GraphNode(string Id, string Name, string? Kind, int EngramCount, int Group);
    public record GraphEdge(string From, string To, string Relation);
    public record GraphResult(List<GraphNode> Nodes, List<GraphEdge> Edges);

    /// <summary>Full entity graph for the full-page Memory canvas — every entity as a node (sized by
    /// engram count) and every EntityLink as an edge. Unlike ListEntitiesAsync, not capped to a top-N.
    /// Nodes are also assigned a topic <c>Group</c> (0-based, largest topic first) via greedy-modularity
    /// community detection over explicit EntityLinks (weight 2) plus co-mention pairs (weight 1 per
    /// shared engram). Unlike plain connected components, dense sub-topics stay separate groups even
    /// when a single relation bridges them — those bridging relations render as cross-cluster edges.</summary>
    public async Task<GraphResult> GetGraphAsync(string? bank, CancellationToken ct)
    {
        bank = NormalizeBank(bank);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var soulId = await GetActiveSoulIdAsync(db, ct);

        var entities = await db.MemoryEntities.AsNoTracking()
            .Where(e => e.SoulId == soulId && e.Bank == bank)
            .Select(e => new { e.Id, e.Name, e.Kind })
            .ToListAsync(ct);

        var entityIds = entities.Select(e => e.Id).ToList();
        var counts = await db.EngramEntities.AsNoTracking()
            .Where(x => entityIds.Contains(x.EntityId))
            .GroupBy(x => x.EntityId)
            .Select(g => new { EntityId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.EntityId, x => x.Count, ct);

        var edges = await db.EntityLinks.AsNoTracking()
            .Where(l => l.SoulId == soulId && l.Bank == bank)
            .Select(l => new GraphEdge(l.FromEntityId, l.ToEntityId, l.Relation))
            .ToListAsync(ct);

        var comentions = await db.EngramEntities.AsNoTracking()
            .Where(x => entityIds.Contains(x.EntityId))
            .GroupBy(x => x.EngramId)
            .Select(g => g.Select(x => x.EntityId).ToList())
            .ToListAsync(ct);

        // Accumulate pair weights: explicit relations count double a co-mention (a named relation is
        // a stronger topical signal than merely appearing in the same engram).
        var pairWeights = new Dictionary<(string, string), double>();
        void AddWeight(string a, string b, double w)
        {
            if (a == b) return;
            var key = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
            pairWeights[key] = pairWeights.GetValueOrDefault(key) + w;
        }

        foreach (var edge in edges) AddWeight(edge.From, edge.To, 2.0);
        foreach (var group in comentions)
        {
            // All pairs for typical engrams; star from the first entity for pathological ones so a
            // single entity-stuffed engram can't inject O(k²) pairs.
            if (group.Count <= 12)
                for (var i = 0; i < group.Count; i++)
                    for (var j = i + 1; j < group.Count; j++) AddWeight(group[i], group[j], 1.0);
            else
                for (var i = 1; i < group.Count; i++) AddWeight(group[0], group[i], 1.0);
        }

        var nameOf = entities.ToDictionary(e => e.Id, e => e.Name);
        var groupIndex = ComputeTopicGroups(entityIds, pairWeights, nameOf);

        var nodes = entities
            .Select(e => new GraphNode(e.Id, e.Name, e.Kind, counts.GetValueOrDefault(e.Id), groupIndex.GetValueOrDefault(e.Id)))
            .ToList();

        return new GraphResult(nodes, edges);
    }

    // Greedy modularity maximization (Clauset-Newman-Moore style): every entity starts as its own
    // community; repeatedly merge the community pair with the highest positive modularity gain
    // ΔQ = W_ab/m − tot_a·tot_b/(2m²). Deterministic (stable pair ordering, name tie-breaks) and
    // easily fast enough at personal-memory scale (hundreds of entities).
    private static Dictionary<string, int> ComputeTopicGroups(
        List<string> entityIds, Dictionary<(string, string), double> pairWeights, Dictionary<string, string> nameOf)
    {
        var result = new Dictionary<string, int>();
        if (entityIds.Count == 0) return result;

        var m = pairWeights.Values.Sum();
        var members = entityIds.OrderBy(id => nameOf.GetValueOrDefault(id), StringComparer.Ordinal)
            .Select(id => new List<string> { id }).ToList();

        if (m > 0)
        {
            var commOf = new Dictionary<string, int>();
            for (var i = 0; i < members.Count; i++) commOf[members[i][0]] = i;

            var degree = entityIds.ToDictionary(id => id, _ => 0.0);
            foreach (var ((a, b), w) in pairWeights) { degree[a] += w; degree[b] += w; }
            var tot = members.Select(c => degree[c[0]]).ToList();

            // Inter-community weights, keyed (lo, hi) on community index.
            var between = new Dictionary<(int, int), double>();
            foreach (var ((a, b), w) in pairWeights)
            {
                var ca = commOf[a]; var cb = commOf[b];
                var key = ca < cb ? (ca, cb) : (cb, ca);
                between[key] = between.GetValueOrDefault(key) + w;
            }

            while (true)
            {
                var bestGain = 0.0;
                (int, int)? bestPair = null;
                foreach (var ((ca, cb), w) in between.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2))
                {
                    var gain = w / m - tot[ca] * tot[cb] / (2 * m * m);
                    if (gain > bestGain + 1e-12) { bestGain = gain; bestPair = (ca, cb); }
                }
                if (bestPair == null) break;

                var (keep, drop) = bestPair.Value;
                members[keep].AddRange(members[drop]);
                foreach (var id in members[drop]) commOf[id] = keep;
                members[drop] = [];
                tot[keep] += tot[drop];
                tot[drop] = 0;

                foreach (var key in between.Keys.Where(k => k.Item1 == drop || k.Item2 == drop).ToList())
                {
                    var w = between[key];
                    between.Remove(key);
                    var other = key.Item1 == drop ? key.Item2 : key.Item1;
                    if (other == keep) continue; // internal now
                    var merged = keep < other ? (keep, other) : (other, keep);
                    between[merged] = between.GetValueOrDefault(merged) + w;
                }
            }
        }

        var ordered = members.Where(c => c.Count > 0)
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Min(id => nameOf.GetValueOrDefault(id)), StringComparer.Ordinal)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
            foreach (var id in ordered[i])
                result[id] = i;
        return result;
    }

    public record NoosphereStats(int Engrams, int Entities, int Links, int PendingIngests, int EmbeddedCount, bool EmbeddingsConfigured, bool ExtractionConfigured, int RawIngests);

    public async Task<NoosphereStats> StatsAsync(string? bank, CancellationToken ct)
    {
        bank = NormalizeBank(bank);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var soulId = await GetActiveSoulIdAsync(db, ct);

        var engramsCount = await db.Engrams.CountAsync(e => e.SoulId == soulId && e.Bank == bank, ct);
        var embeddedCount = await db.Engrams.CountAsync(e => e.SoulId == soulId && e.Bank == bank && e.Embedding != null, ct);
        var entitiesCount = await db.MemoryEntities.CountAsync(e => e.SoulId == soulId && e.Bank == bank, ct);
        var linksCount = await db.EntityLinks.CountAsync(l => l.SoulId == soulId && l.Bank == bank, ct);
        var pendingCount = await db.MemoryIngests.CountAsync(i => i.SoulId == soulId && i.Bank == bank && (i.Status == "pending" || i.Status == "error"), ct);
        // Ingests that fell back to raw/unstructured storage — eligible for ReprocessRawIngestsAsync.
        var rawCount = await db.MemoryIngests.CountAsync(i => i.SoulId == soulId && i.Bank == bank && i.Status == "raw", ct);

        var builtinActive = await configService.IsBuiltinActiveAsync(builtinRuntime, ct);
        var embeddingsConfigured = embedder.Enabled && (builtinActive
            || await NoosphereChannelResolver.ResolveAsync(await configService.GetEmbeddingOptionsAsync(ct), db, ct) is not null);
        var extractionConfigured = builtinActive
            || await NoosphereChannelResolver.ResolveAsync(await configService.GetExtractionOptionsAsync(ct), db, ct) is not null;

        return new NoosphereStats(engramsCount, entitiesCount, linksCount, pendingCount, embeddedCount,
            embeddingsConfigured, extractionConfigured, rawCount);
    }
}
