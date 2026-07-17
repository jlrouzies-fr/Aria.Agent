using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aria.Bridge.Services.Noosphere;

// Consumes Inscribe requests off NoosphereService's channel: extraction → entity/link upsert →
// embedding. On startup, recovers ingests interrupted by a crash/restart and backfills embeddings
// for engrams that predate the embedder being configured (or after a model change).
public class NoosphereIngestWorker(NoosphereService service, ILogger<NoosphereIngestWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            foreach (var id in await service.GetPendingIngestIdsAsync(stoppingToken))
                await service.RequeueIngestAsync(id, stoppingToken);

            foreach (var id in await service.GetEngramIdsNeedingEmbeddingAsync(stoppingToken))
            {
                try { await service.BackfillEmbeddingAsync(id, stoppingToken); }
                catch (Exception ex) { logger.LogWarning(ex, "[Noosphere] Embedding backfill failed for engram {Id}", id); }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Noosphere] Startup sweep failed");
        }

        await foreach (var ingestId in service.IngestReader.ReadAllAsync(stoppingToken))
        {
            try { await service.ProcessIngestAsync(ingestId, stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "[Noosphere] Failed processing ingest {Id}", ingestId); }
        }
    }
}
