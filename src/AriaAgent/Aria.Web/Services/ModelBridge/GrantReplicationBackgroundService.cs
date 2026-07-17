namespace Aria.Web.Services.ModelBridge;

/// <summary>
/// Auto-replicates Layer B context grants across a soul's connected nodes (defense-in-depth plan §4).
/// A human approves sensitive operations on ONE node (e.g. their local machine); this loop relays the
/// resulting node-signed grant to the soul's other nodes (e.g. a headless remote server) so they stop
/// blocking without a second approval. The server only relays opaque signed blobs it can neither read
/// nor forge — the importing node re-verifies the signature.
///
/// Runs only while a soul has ≥2 nodes connected; the ~60s cadence is fine for an 8h grant and avoids
/// needing a node→server notification or a browser round-trip to trigger replication.
/// </summary>
public sealed class GrantReplicationBackgroundService(
    ModelBridgeRegistry registry,
    GrantReplicationService replication,
    ILogger<GrantReplicationBackgroundService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var userId in registry.UsersWithMultipleNodes())
                    await replication.ReplicateAsync(userId);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "[GrantSync] background replication tick failed");
            }
        }
    }
}
