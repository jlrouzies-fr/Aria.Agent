#if DEBUG
using System.Text;
using Aria.Agent;
using Aria.Harness.Core;
using Aria.Harness.Formats;
using Aria.Harness.Models;
using Aria.Web.Data;
using Aria.Web.Services;
using Microsoft.EntityFrameworkCore;
using OpenAI.Chat;

namespace Aria.Web.Debug;

// Debug-only API for verifying thinking-format detection without the browser UI.
//
// Quick test sequence:
//   # 1. List sources and cached formats
//   curl -s http://localhost:5129/api/debug/chat/sources
//
//   # 2. Force re-probe a specific model
//   curl -s -X POST "http://localhost:5129/api/debug/chat/probe?source=Local+LLM+-+Mac+(localhost)&model=qwen3.6-35b-a3b-claude-4.7-opus-distilled-mlx-oq4"
//
//   # 3. Send a test message and see thinking output
//   curl -s -X POST "http://localhost:5129/api/debug/chat/send?source=Local+LLM+-+Mac+(localhost)&model=qwen3.6-35b-a3b-claude-4.7-opus-distilled-mlx-oq4"

public static class ChatDebugApiEndpoints
{
    public static readonly string LogFile =
        Path.Combine(AppContext.BaseDirectory, "chat-debug.log");

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); }
        catch { }
    }

    public static void Register(WebApplication app)
    {
        var grp = app.MapGroup("/api/debug/chat");

        // ── Source list + current cache status ────────────────────────────────
        grp.MapGet("/sources", async (AgentService svc, IDbContextFactory<AppDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var dbEntries = await db.ModelFormatCaches.AsNoTracking().ToListAsync();

            var result = svc.AvailableModelSources.Select(src =>
            {
                var srcEntries = dbEntries.Where(e => e.EndpointUrl == src.Url).ToList();
                return new
                {
                    src.Name,
                    src.Url,
                    models = src.Models,
                    cached = srcEntries.Select(e => new
                    {
                        e.ModelId,
                        e.ThinkingFormat,
                        e.ToolCallFormat,
                        e.DetectedAt
                    })
                };
            });

            return Results.Ok(result);
        });

        // ── Detect (uses cache if available) ──────────────────────────────────
        grp.MapPost("/detect", async (string source, string model, AgentService svc, string? userId) =>
        {
            var tf  = await svc.DetectThinkingFormatAsync(source, model, userId: userId);
            var tcf = await svc.DetectToolCallFormatAsync(source, model, userId: userId);
            Log($"DETECT source={source} model={model} userId={userId} → thinking={tf} toolcall={tcf}");
            return Results.Ok(new { source, model, thinkingFormat = tf.ToString(), toolCallFormat = tcf.ToString() });
        });

        // ── Force re-probe (clears cache, always runs live detection) ─────────
        grp.MapPost("/probe", async (string source, string model, AgentService svc, CancellationToken ct, string? userId) =>
        {
            Log($"PROBE START source={source} model={model} userId={userId}");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var (tf, tcf) = await svc.ForceRedetectAsync(source, model, ct, userId);
            sw.Stop();

            Log($"PROBE END {sw.ElapsedMilliseconds}ms → thinking={tf} toolcall={tcf}");
            return Results.Ok(new
            {
                source, model,
                thinkingFormat  = tf.ToString(),
                toolCallFormat  = tcf.ToString(),
                probeMs         = sw.ElapsedMilliseconds,
                logFile         = LogFile
            });
        });

        // ── Send test message, capture thinking + content ─────────────────────
        grp.MapPost("/send", async (string source, string model, string? message, AgentService svc, IHarnessRuntime runtime, CancellationToken ct, string? userId) =>
        {
            var testMessage = message ?? "Briefly think through: what is the capital of France? Then answer.";

            var srcObj = svc.GetSourcesForUser(userId ?? "").FirstOrDefault(s => s.Name == source)
                ?? svc.AvailableModelSources.FirstOrDefault(s => s.Name == source);
            if (srcObj == null) return Results.NotFound($"Source '{source}' not found");

            var format = await svc.DetectThinkingFormatAsync(source, model, ct, userId: userId);
            Log($"SEND source={source} model={model} format={format}");
            Log($"MESSAGE: {testMessage}");

            var thinkingSb  = new StringBuilder();
            var contentSb   = new StringBuilder();

            HttpMessageHandler innerHandler;
            if (srcObj.IsBridged)
            {
                var ctx = new HarnessContext { UserId = userId, BridgeUserId = userId };
                innerHandler = new BridgeHttpHandler(
                    runtime, ctx,
                    keyRef: srcObj.ChannelName ?? srcObj.Name,
                    requireKey: srcObj.IsPublicProvider,
                    nodeId: srcObj.BridgeNodeId);
            }
            else
            {
                innerHandler = new HttpClientHandler();
            }

            var handler = new UniversalReasoningHandler
            {
                InnerHandler       = innerHandler,
                OnReasoningContent = t => thinkingSb.Append(t),
                StartsInThinkMode  = format == ThinkingFormat.StartsInThinkMode
            };

            try
            {
                var client  = ChatClientFactory.Build(srcObj, model, handler);
                var options = new ChatCompletionOptions { MaxOutputTokenCount = 512, Temperature = 0.7f };

                await foreach (var update in client.CompleteChatStreamingAsync(
                    [new UserChatMessage(testMessage)], options, ct))
                    foreach (var part in update.ContentUpdate)
                        contentSb.Append(part.Text);

                var thinking = thinkingSb.ToString().Trim();
                var content  = contentSb.ToString().Trim();

                Log($"THINKING ({thinking.Length} chars): {thinking[..Math.Min(200, thinking.Length)]}");
                Log($"CONTENT: {content}");

                return Results.Ok(new
                {
                    source, model,
                    detectedFormat   = format.ToString(),
                    thinkingCaptured = thinking.Length > 0,
                    thinkingLength   = thinking.Length,
                    thinkingPreview  = thinking.Length > 0 ? thinking[..Math.Min(300, thinking.Length)] : "(none)",
                    content          = content.Length > 0 ? content[..Math.Min(500, content.Length)] : "(empty)",
                    logFile          = LogFile
                });
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.Message}");
                return Results.Problem(ex.Message);
            }
        });

        // ── Read chat debug log ───────────────────────────────────────────────
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
