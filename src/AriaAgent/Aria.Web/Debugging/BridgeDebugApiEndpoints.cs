#if DEBUG
using System.Text;
using Aria.Agent;
using Aria.Harness.Formats;
using Aria.Web.Services;

namespace Aria.Web.Debug;

// Debug-only API for verifying the model bridge end-to-end (daemon direct tunnel).
//
// Prerequisites:
//   1. Run the app: dotnet run --project Aria.Web
//   2. Run the daemon: dotnet run --project Aria.Bridge
//   3. Link a soul via http://localhost:5741
//
// Then curl:
//   # Check who is registered (userId + SignalR connectionId)
//   curl -s http://localhost:5129/api/debug/bridge/status | jq
//
//   # Send a test message through the daemon bridge
//   curl -s -X POST "http://localhost:5129/api/debug/bridge/send?userId=1&source=Local+LLM+-+Mac+(localhost)&model=qwen3.6-35b-a3b-claude-4.7-opus-distilled-mlx-oq4" | jq
//
//   # Watch the log
//   curl -s "http://localhost:5129/api/debug/bridge/logs?tail=40" | jq -r '.lines[]'

public static class BridgeDebugApiEndpoints
{
    public static readonly string LogFile =
        Path.Combine(AppContext.BaseDirectory, "bridge-debug.log");

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); }
        catch { }
    }

    public static void Register(WebApplication app)
    {
        var grp = app.MapGroup("/api/debug/bridge");

        // ── Registry state ────────────────────────────────────────────────────
        grp.MapGet("/status", (ModelBridgeRegistry registry) =>
            Results.Ok(registry.GetStatus()));

        // ── Format detection diagnostic ───────────────────────────────────────
        // curl "http://localhost:5129/api/debug/bridge/detect-format?userId=GUID&source=mac&model=MODEL"
        grp.MapGet("/detect-format", async (
            string userId, string source, string model,
            AgentService agentSvc, ModelBridgeRegistry registry,
            CancellationToken ct) =>
        {
            var hasBridge = registry.HasBridge(userId);
            var srcObj    = agentSvc.GetSourcesForUser(userId)
                            .FirstOrDefault(s => s.Name == source)
                            ?? agentSvc.AvailableModelSources.FirstOrDefault(s => s.Name == source);

            // Flush only this key from the in-memory cache so DetectThinkingFormatAsync re-probes
            agentSvc.EvictFormatCache(source, model);

            ThinkingFormat detected;
            string? probeError = null;
            try   { detected = await agentSvc.DetectThinkingFormatAsync(source, model, ct, userId); }
            catch (Exception ex) { detected = ThinkingFormat.None; probeError = ex.Message; }

            return Results.Ok(new
            {
                userId,
                source,
                model,
                hasBridge,
                sourceFound    = srcObj != null,
                isBridged      = srcObj?.IsBridged == true,
                detectedFormat = detected.ToString(),
                probeError,
            });
        });

        // ── Send a test message through the bridge ────────────────────────────
        grp.MapPost("/send", async (
            string userId, string source, string model,
            string? message,
            AgentService agentSvc, ModelBridgeRegistry registry,
            CancellationToken ct) =>
        {
            var testMessage = message ?? "Briefly think through: what is the capital of France? Then answer.";

            var srcObj = agentSvc.AvailableModelSources.FirstOrDefault(s => s.Name == source);
            if (srcObj == null)
                return Results.NotFound($"Source '{source}' not found");
            if (!srcObj.IsBridged)
                return Results.BadRequest($"Source '{source}' is not marked IsBridged. Use /api/debug/chat/send instead.");
            if (!registry.HasBridge(userId))
                return Results.BadRequest(
                    $"No bridge registered for userId='{userId}'. " +
                    $"Open http://localhost:5129 in a browser, select that soul and a bridged source.");

            Log($"SEND userId={userId} source={source} model={model}");
            Log($"MESSAGE: {testMessage}");

            var thinkingSb = new StringBuilder();
            var contentSb  = new StringBuilder();
            var chunkCount = 0;

            var format = await agentSvc.DetectThinkingFormatAsync(source, model, ct);
            Log($"FORMAT: {format}");

            var handler = new UniversalReasoningHandler
            {
                InnerHandler       = new ModelBridgeHandler(registry, userId),
                OnReasoningContent = t => thinkingSb.Append(t),
                StartsInThinkMode  = format == ThinkingFormat.StartsInThinkMode
            };

            try
            {
                var sw     = System.Diagnostics.Stopwatch.StartNew();
                var client  = ChatClientFactory.Build(srcObj, model, handler);
                var options = new OpenAI.Chat.ChatCompletionOptions
                    { MaxOutputTokenCount = 512, Temperature = 0.7f };

                await foreach (var update in client.CompleteChatStreamingAsync(
                    [new OpenAI.Chat.UserChatMessage(testMessage)], options, ct))
                    foreach (var part in update.ContentUpdate)
                    {
                        contentSb.Append(part.Text);
                        chunkCount++;
                    }
                sw.Stop();

                var thinking = thinkingSb.ToString().Trim();
                var content  = contentSb.ToString().Trim();

                Log($"DONE {sw.ElapsedMilliseconds}ms  chunks={chunkCount}  thinking={thinking.Length}  content={content.Length}");
                if (content.Length > 0)
                    Log($"CONTENT: {content[..Math.Min(300, content.Length)]}");

                return Results.Ok(new
                {
                    userId, source, model,
                    detectedFormat   = format.ToString(),
                    elapsedMs        = sw.ElapsedMilliseconds,
                    chunkCount,
                    thinkingCaptured = thinking.Length > 0,
                    thinkingLength   = thinking.Length,
                    thinkingPreview  = thinking.Length > 0 ? thinking[..Math.Min(300, thinking.Length)] : "(none)",
                    content          = content.Length > 0 ? content[..Math.Min(500, content.Length)] : "(empty)",
                    logFile          = LogFile
                });
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex}");
                return Results.Problem(ex.Message);
            }
        });

        // ── Error reports (called by bridge clients on init failure) ─
        grp.MapPost("/report-error", async (HttpRequest req) =>
        {
            var body = await new System.IO.StreamReader(req.Body).ReadToEndAsync();
            Log($"BRIDGE ERROR: {body}");
            Console.Error.WriteLine($"[Bridge Error] {body}");
            return Results.Ok();
        });

        // ── Log tailing ───────────────────────────────────────────────────────
        grp.MapGet("/logs", (int? tail) =>
        {
            if (!File.Exists(LogFile)) return Results.Ok(new { lines = Array.Empty<string>() });
            var lines  = File.ReadAllLines(LogFile);
            var result = tail.HasValue ? lines.TakeLast(tail.Value) : lines;
            return Results.Ok(new { totalLines = lines.Length, lines = result });
        });

        grp.MapDelete("/logs", () =>
        {
            try { File.WriteAllText(LogFile, ""); } catch { }
            return Results.Ok("Log cleared");
        });
    }
}
#endif
