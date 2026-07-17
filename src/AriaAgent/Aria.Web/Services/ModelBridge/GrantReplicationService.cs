namespace Aria.Web.Services.ModelBridge;

/// <summary>
/// Replicates Layer B context grants between a soul's connected nodes (defense-in-depth plan §4–§5),
/// mirroring <see cref="KeyReplicationService"/>. Each node's live signed grants are exported and
/// imported into every other node; the server relays opaque signed blobs it can neither read for
/// anything sensitive nor forge (the importing node re-verifies the signature). Lets an approval on
/// one node satisfy the gate on its siblings.
/// </summary>
public sealed class GrantReplicationService(ModelBridgeRegistry registry, ILogger<GrantReplicationService> log)
{
    public async Task<int> ReplicateAsync(string userId)
    {
        var pushes = 0;
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
                    if (resp is { StatusCode: 200 }) pushes++;
                    else log.LogInformation("[GrantSync] import into node {Node} failed (status {Status})",
                        target.NodeId, resp?.StatusCode.ToString() ?? "offline/old-bridge");
                }

            log.LogInformation("[GrantSync] replication for {UserId}: {Exports} exports, {Pushes} imports ok",
                userId, exports.Count, pushes);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[GrantSync] replication failed for {UserId}", userId);
        }
        return pushes;
    }
}
