using System.Text.Json;
using Aria.Harness.Formats;
using Aria.Web.Services.Llm;
using Aria.Web.Services.ModelBridge;

namespace Aria.Web.Endpoints;

/// <summary>
/// Small maintenance operations that must be available in Production (the debug API is compiled
/// out of Release builds and the fly.io image ships no sqlite3 CLI).
/// </summary>
public static class MaintenanceEndpoints
{
    // These endpoints take a `userId` and reach into that soul's node — reading channel bindings,
    // provider-key names, LLM-egress logs, and driving LLM calls / vault mutations on it. The access
    // gate (IP / guest code) is coarse and NOT per-soul, so it cannot be the only guard: anyone past
    // the gate could otherwise pass an arbitrary userId and act across souls. Require the same
    // node-signed soul verification the UI uses (`direct-{userId}`) — a live, key-proven bridge for
    // exactly that soul must be connected.
    private static bool SoulVerified(ModelBridgeRegistry registry, string? userId) =>
        !string.IsNullOrWhiteSpace(userId) && registry.IsSoulVerified(userId);

    private static IResult Unverified() =>
        Results.Json(new { ok = false, error = "Soul not verified — connect the owning bridge for this userId." },
            statusCode: StatusCodes.Status403Forbidden);

    public static WebApplication MapMaintenanceEndpoints(this WebApplication app)
    {
        // Purge persisted model-format detections whose model id contains the fragment, so the next
        // session re-probes. Recovers from a wrong verdict that was cached (e.g. a heuristic
        // misclassification, or a probe that ran against the wrong bridge node). Harmless to call:
        // the cache is rebuilt automatically.
        //   curl -X DELETE "https://<host>/api/maintenance/format-cache?model=gemma"
        app.MapDelete("/api/maintenance/format-cache", async (
            string model, IFormatCache cache, ILogger<WebFormatCache> log, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(model) || model.Trim().Length < 3)
                return Results.BadRequest("Pass ?model= with at least 3 characters of the model id.");
            if (cache is not WebFormatCache web)
                return Results.Problem("Format cache implementation is not purgeable.");

            var purged = await web.PurgeAsync(model.Trim(), ct);
            log.LogInformation("[Maintenance] Purged {Count} format-cache entries matching '{Model}'", purged, model);
            return Results.Ok(new { purged, model });
        });

        // ── Multi-node diagnostics ────────────────────────────────────────────
        // These let a connected soul's cross-machine plumbing be inspected with curl (behind the
        // access gate) instead of shell access to each machine.

        // Connected nodes for a soul.
        app.MapGet("/api/maintenance/nodes", (string userId, ModelBridgeRegistry registry) =>
            !SoulVerified(registry, userId) ? Unverified()
                : Results.Ok(registry.GetNodes(userId).Select(n => new { n.NodeId, n.Label, n.Platform })));

        // A soul's configured local-LLM channels and which bridge node each is bound to — the piece
        // GetForUserAsync doesn't show is whether that BridgeNodeId still matches a live connection.
        app.MapGet("/api/maintenance/local-sources", async (
            string userId, UserLocalSourceService sources, ModelBridgeRegistry registry) =>
        {
            if (!SoulVerified(registry, userId)) return Unverified();
            var online = registry.GetNodes(userId).ToDictionary(n => n.NodeId, n => n.Label);
            var result = (await sources.GetForUserAsync(userId)).Select(s => new
            {
                s.Id,
                s.Name,
                s.Url,
                s.IsBridged,
                s.BridgeNodeId,
                boundNodeLabel = s.BridgeNodeId != null && online.TryGetValue(s.BridgeNodeId, out var label)
                    ? label : null,
                boundNodeOnline = s.BridgeNodeId != null && online.ContainsKey(s.BridgeNodeId),
            });
            return Results.Ok(result);
        });

        // Which provider keys each connected node holds (names only — never the keys).
        app.MapGet("/api/maintenance/node-keys", async (string userId, ModelBridgeRegistry registry) =>
        {
            if (!SoulVerified(registry, userId)) return Unverified();
            var result = new Dictionary<string, object?>();
            foreach (var n in registry.GetNodes(userId))
            {
                var resp = await registry.SendLocalRestAsync(userId, "GET", "/keys", null, n.NodeId);
                result[$"{n.Label} ({n.NodeId})"] = resp is { StatusCode: 200, Body: { } b }
                    ? JsonSerializer.Deserialize<JsonElement>(b) : (object?)$"status={resp?.StatusCode}";
            }
            return Results.Ok(result);
        });

        // Round-trips a harmless test key through PUT /keys/{provider} on a specific node, then reads
        // it back and deletes it — isolates whether a "key not saved" report is a tunnel/bridge
        // problem or a browser/UI problem, without needing a human to reproduce it live.
        //   curl -X POST "https://<host>/api/maintenance/test-key-roundtrip?userId=U&nodeId=N&provider=P"
        app.MapPost("/api/maintenance/test-key-roundtrip", async (
            string userId, string nodeId, string provider, ModelBridgeRegistry registry) =>
        {
            if (!SoulVerified(registry, userId)) return Unverified();
            var probeValue = $"roundtrip-test-{Guid.NewGuid():N}";
            var putResp = await registry.SendLocalRestAsync(userId, "PUT", $"/keys/{Uri.EscapeDataString(provider)}",
                JsonSerializer.Serialize(new { key = probeValue }), nodeId);

            var getResp = await registry.SendLocalRestAsync(userId, "GET", "/keys", null, nodeId);
            var foundAfterPut = getResp is { StatusCode: 200, Body: { } gb }
                && JsonDocument.Parse(gb).RootElement.TryGetProperty("providers", out var arr)
                && arr.EnumerateArray().Any(el => el.GetString() == provider);

            var delResp = await registry.SendLocalRestAsync(userId, "DELETE", $"/keys/{Uri.EscapeDataString(provider)}", null, nodeId);

            return Results.Ok(new
            {
                putStatus    = putResp?.StatusCode,
                putBody      = putResp?.Body,
                foundAfterPut,
                deleteStatus = delResp?.StatusCode,
            });
        });

        // Trigger Layer B context-grant replication: relay each node's live signed grants to its
        // siblings so a node-local approval satisfies the gate across the soul's machines.
        app.MapPost("/api/maintenance/replicate-grants", async (
            string userId, GrantReplicationService grantSync, ModelBridgeRegistry registry) =>
        {
            if (!SoulVerified(registry, userId)) return Unverified();
            var pushes = await grantSync.ReplicateAsync(userId);
            return Results.Ok(new { pushes });
        });

        // A node's recent outbound LLM calls with response heads (bridge /debug/llm-log, ≥0.9.1).
        app.MapGet("/api/maintenance/node-llm-log", async (string userId, string nodeId, ModelBridgeRegistry registry) =>
        {
            if (!SoulVerified(registry, userId)) return Unverified();
            var resp = await registry.SendLocalRestAsync(userId, "GET", "/debug/llm-log", null, nodeId);
            return resp is { StatusCode: 200, Body: { } b }
                ? Results.Content(b, "application/json")
                : Results.Problem($"node responded {resp?.StatusCode.ToString() ?? "null (offline?)"}");
        });

        // Fire a minimal non-streaming completion through a channel's bound bridge and return the raw
        // outcome — reproduces "chat shows nothing" failures without a human at the browser.
        //   curl -X POST "https://<host>/api/maintenance/test-channel?userId=U&source=NAME&model=ID"
        app.MapPost("/api/maintenance/test-channel", async (
            string userId, string source, string? model,
            ModelBridgeRegistry registry, UserLocalSourceService sources) =>
        {
            if (!SoulVerified(registry, userId)) return Unverified();
            var src = (await sources.GetForUserAsync(userId)).FirstOrDefault(s => s.Name == source);
            if (src == null) return Results.NotFound($"No local source named '{source}' for that user");

            var models = JsonSerializer.Deserialize<List<string>>(src.ModelsJson) ?? [];
            var useModel = model ?? models.FirstOrDefault() ?? "default";
            var nodeId = string.IsNullOrEmpty(src.BridgeNodeId) ? null : src.BridgeNodeId;

            var completions = JsonSerializer.Serialize(new
            {
                model = useModel,
                messages = new[] { new { role = "user", content = "Reply with the single word: ok" } },
                stream = false,
                max_tokens = 10,
            });
            var wrapped = JsonSerializer.Serialize(new
            {
                url = src.Url.TrimEnd('/') + "/chat/completions",
                body = completions,
                keyRef = src.Name,
                requireKey = false,
            });

            var resp = await registry.SendLocalRestAsync(userId, "POST", "/llm/proxy", wrapped, nodeId, timeoutSeconds: 60);
            var body = resp?.Body ?? "";
            return Results.Ok(new
            {
                source,
                model = useModel,
                boundNode = nodeId ?? "(default node)",
                tunnelStatus = resp?.StatusCode,
                responseHead = body.Length > 800 ? body[..800] : body,
            });
        });

        return app;
    }
}
