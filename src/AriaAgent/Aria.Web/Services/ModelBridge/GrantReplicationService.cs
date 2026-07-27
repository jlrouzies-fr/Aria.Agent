namespace Aria.Web.Services.ModelBridge;

/// <summary>
/// Replicates Layer B context grants between a soul's connected nodes (defense-in-depth plan §4–§5),
/// mirroring <see cref="KeyReplicationService"/>. Each node's live signed grants — and signed
/// revocation tombstones — are exported and imported into every other node; the server relays opaque
/// signed blobs it can neither read for anything sensitive nor forge (the importing node re-verifies
/// the signature). Lets an approval — or a revocation — on one node take effect on its siblings.
/// </summary>
public sealed class GrantReplicationService(ModelBridgeRegistry registry, ILogger<GrantReplicationService> log)
{
    public async Task<int> ReplicateAsync(string userId)
    {
        var pushes = 0;
        var totalImported = 0;
        try
        {
            var nodes = registry.GetNodes(userId).ToList();
            if (nodes.Count < 2) return 0;

            var exports = new List<(string NodeId, string Body)>();
            foreach (var n in nodes)
            {
                var resp = await registry.SendLocalRestAsync(userId, "GET", "/context/grants/export", null, n.NodeId);
                if (resp is { StatusCode: 200, Body: { } body } && body != "[]")
                    exports.Add((n.NodeId, body));
            }

            foreach (var (src, body) in exports)
                foreach (var target in nodes.Where(x => x.NodeId != src))
                {
                    var resp = await registry.SendLocalRestAsync(userId, "POST", "/context/grants/import", body, target.NodeId);
                    if (resp is { StatusCode: 200, Body: var importBody })
                    {
                        pushes++;
                        // A 200 only means the endpoint accepted the payload — the bridge drops
                        // grants whose signatures it cannot verify (missing sibling trust), so the
                        // {imported: n} body is the truth. imported:0 is exactly the failure mode
                        // that used to be invisible here.
                        var imported = ParseImportedCount(importBody);
                        totalImported += imported;
                        if (imported == 0)
                            log.LogWarning("[GrantSync] node {Node} accepted the push but imported 0 grants " +
                                "(signature verification failed — sibling trust missing?)", target.NodeId);
                    }
                    else log.LogInformation("[GrantSync] import into node {Node} failed (status {Status})",
                        target.NodeId, resp?.StatusCode.ToString() ?? "offline/old-bridge");
                }

            log.LogInformation("[GrantSync] replication for {UserId}: {Exports} exports, {Pushes} pushes ok, {Imported} grants imported",
                userId, exports.Count, pushes, totalImported);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[GrantSync] replication failed for {UserId}", userId);
        }
        return pushes;
    }

    private static int ParseImportedCount(string? body)
    {
        if (string.IsNullOrEmpty(body)) return 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("imported", out var n) && n.TryGetInt32(out var v) ? v : 0;
        }
        catch { return 0; }
    }
}
