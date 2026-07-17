#if DEBUG
using System.Text.Json;

namespace Aria.Web.Debug;

// Debug-only API for verifying Aria.Bridge end-to-end — calls the bridge directly
// from the server (no WASM tunnel needed) so you can test without a browser open.
//
// Prerequisites:
//   1. Start Aria.Bridge:  dotnet run --project Aria.Bridge
//   2. Start Aria.Web:        dotnet run --project Aria.Web
//
// Ollama MCP server example (substitute your paths):
//
//   # Health
//   curl -s http://localhost:5129/api/debug/mcp-bridge/health | jq
//
//   # List tools from a local stdio MCP server via the bridge
//   curl -s -X POST http://localhost:5129/api/debug/mcp-bridge/tools \
//     -H "Content-Type: application/json" \
//     -d '{
//       "command": "/usr/local/share/dotnet/dotnet",
//       "arguments": ["run","--project",
//         "/path/to/ollama-web-mcpserver/src/OllamaMCPServer/OllamaMCPServer/OllamaMCPServer.csproj"],
//       "environment": null
//     }' | jq
//
//   # Call a tool (replace toolName + toolArguments as needed)
//   curl -s -X POST http://localhost:5129/api/debug/mcp-bridge/call \
//     -H "Content-Type: application/json" \
//     -d '{
//       "command": "/usr/local/share/dotnet/dotnet",
//       "arguments": ["run","--project",
//         "/path/to/ollama-web-mcpserver/src/OllamaMCPServer/OllamaMCPServer/OllamaMCPServer.csproj"],
//       "environment": null,
//       "toolName": "web_search",
//       "toolArguments": {"query": "Warhammer 40K Mechanicus"}
//     }' | jq

public static class McpBridgeDebugApiEndpoints
{
    private const string BridgeBase = "http://localhost:5741";
    private static readonly string LogFile =
        Path.Combine(AppContext.BaseDirectory, "mcp-bridge-debug.log");

    private static readonly HttpClient Http = new()
        { Timeout = TimeSpan.FromSeconds(120) };

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); }
        catch { }
    }

    public static void Register(WebApplication app)
    {
        var grp = app.MapGroup("/api/debug/mcp-bridge");

        // ── Health check ─────────────────────────────────────────────────────
        grp.MapGet("/health", async () =>
        {
            try
            {
                var resp = await Http.PostAsync(BridgeBase + "/health",
                    new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
                var body = await resp.Content.ReadAsStringAsync();
                Log($"HEALTH {(int)resp.StatusCode}: {body}");
                return Results.Ok(new { bridgeUrl = BridgeBase, status = (int)resp.StatusCode, body });
            }
            catch (Exception ex)
            {
                Log($"HEALTH ERROR: {ex.Message}");
                return Results.Problem($"Bridge unreachable at {BridgeBase}: {ex.Message}");
            }
        });

        // ── List tools ────────────────────────────────────────────────────────
        grp.MapPost("/tools", async (HttpRequest req) =>
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            Log($"TOOLS REQUEST: {body}");

            try
            {
                var sw   = System.Diagnostics.Stopwatch.StartNew();
                var resp = await Http.PostAsync(BridgeBase + "/tools/list",
                    new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
                var responseBody = await resp.Content.ReadAsStringAsync();
                sw.Stop();

                Log($"TOOLS RESPONSE ({sw.ElapsedMilliseconds}ms) {(int)resp.StatusCode}: {responseBody[..Math.Min(500, responseBody.Length)]}");

                if (!resp.IsSuccessStatusCode)
                    return Results.Problem($"Bridge returned {(int)resp.StatusCode}: {responseBody}");

                using var doc   = JsonDocument.Parse(responseBody);
                var tools = doc.RootElement.EnumerateArray()
                    .Select(t => new
                    {
                        name        = t.TryGetProperty("name",        out var n) ? n.GetString() : null,
                        description = t.TryGetProperty("description", out var d) ? d.GetString() : null,
                    }).ToList();

                return Results.Ok(new { elapsedMs = sw.ElapsedMilliseconds, count = tools.Count, tools, raw = responseBody });
            }
            catch (Exception ex)
            {
                Log($"TOOLS ERROR: {ex}");
                return Results.Problem(ex.Message);
            }
        });

        // ── Call a tool ───────────────────────────────────────────────────────
        grp.MapPost("/call", async (HttpRequest req) =>
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            Log($"CALL REQUEST: {body}");

            try
            {
                var sw   = System.Diagnostics.Stopwatch.StartNew();
                var resp = await Http.PostAsync(BridgeBase + "/tools/call",
                    new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
                var responseBody = await resp.Content.ReadAsStringAsync();
                sw.Stop();

                Log($"CALL RESPONSE ({sw.ElapsedMilliseconds}ms) {(int)resp.StatusCode}: {responseBody[..Math.Min(500, responseBody.Length)]}");

                if (!resp.IsSuccessStatusCode)
                    return Results.Problem($"Bridge returned {(int)resp.StatusCode}: {responseBody}");

                return Results.Ok(new { elapsedMs = sw.ElapsedMilliseconds, raw = responseBody });
            }
            catch (Exception ex)
            {
                Log($"CALL ERROR: {ex}");
                return Results.Problem(ex.Message);
            }
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

        // ── Cogitation direct test (no WASM tunnel) ───────────────────────────
        // Tests POST /cogitations/init, POST /messages, PUT title directly against the bridge.
        // Usage: curl -s "http://localhost:5129/api/debug/mcp-bridge/cogitation-test?serverUserId=1&cogId=sv-test-1"
        grp.MapPost("/cogitation-test", async (int serverUserId, string cogId) =>
        {
            var steps = new List<object>();
            try
            {
                // 1. init
                var initBody = System.Text.Json.JsonSerializer.Serialize(new
                    { id = cogId, serverUserId, ariaAvatarKey = "aria_01", subAgentId = (string?)null });
                var r1 = await Http.PostAsync(BridgeBase + "/cogitations/init",
                    new StringContent(initBody, System.Text.Encoding.UTF8, "application/json"));
                var b1 = await r1.Content.ReadAsStringAsync();
                steps.Add(new { step = "init", status = (int)r1.StatusCode, body = b1 });
                Log($"COG-TEST init {(int)r1.StatusCode}: {b1}");

                // 2. add user message
                var msgBody = System.Text.Json.JsonSerializer.Serialize(new
                    { role = "user", content = "Bridge tunnel test message", thinkingContent = (string?)null });
                var r2 = await Http.PostAsync(BridgeBase + $"/cogitations/{cogId}/messages",
                    new StringContent(msgBody, System.Text.Encoding.UTF8, "application/json"));
                var b2 = await r2.Content.ReadAsStringAsync();
                steps.Add(new { step = "add_message", status = (int)r2.StatusCode, body = b2 });
                Log($"COG-TEST add_msg {(int)r2.StatusCode}: {b2}");

                // 3. update title
                var titleBody = System.Text.Json.JsonSerializer.Serialize(new
                    { title = "Debug test cogitation", ariaAvatarKey = (string?)null });
                var r3 = await Http.PutAsync(BridgeBase + $"/cogitations/{cogId}",
                    new StringContent(titleBody, System.Text.Encoding.UTF8, "application/json"));
                var b3 = await r3.Content.ReadAsStringAsync();
                steps.Add(new { step = "update_title", status = (int)r3.StatusCode, body = b3 });
                Log($"COG-TEST update_title {(int)r3.StatusCode}: {b3}");

                // 4. read messages back
                var r4 = await Http.GetAsync(BridgeBase + $"/cogitations/{cogId}/messages");
                var b4 = await r4.Content.ReadAsStringAsync();
                steps.Add(new { step = "read_messages", status = (int)r4.StatusCode, body = b4 });

                return Results.Ok(new { ok = true, steps });
            }
            catch (Exception ex)
            {
                Log($"COG-TEST ERROR: {ex}");
                return Results.Problem(ex.Message);
            }
        });

        // ── WASM-tunnel cogitation test ───────────────────────────────────────
        // Tests SendLocalRestAsync end-to-end (requires WASM bridge connected for userId).
        // Usage: curl -s -X POST "http://localhost:5129/api/debug/mcp-bridge/tunnel-test?userId=5&serverCogId=8001"
        grp.MapPost("/tunnel-test", async (string userId, int serverCogId,
            Aria.Web.Services.Cogitations.BridgeCogitationClient bridgeCog) =>
        {
            if (!bridgeCog.HasBridge(userId))
                return Results.BadRequest($"No WASM bridge connected for userId='{userId}'. " +
                    "Open the app in a browser, select a soul and bridged source, then retry.");

            var bridgeId = Aria.Web.Services.Cogitations.BridgeCogitationClient.BridgeId(serverCogId);

            await bridgeCog.EnsureCogitationAsync(userId, userId, serverCogId, "aria_01");
            await bridgeCog.AddMessageAsync(userId, serverCogId, "user", "WASM tunnel test message");
            await bridgeCog.UpdateTitleAsync(userId, serverCogId, "WASM tunnel debug test");

            // Read result back from bridge directly to confirm
            try
            {
                await Task.Delay(200); // brief pause for any in-flight async
                var cogResp = await Http.GetAsync(BridgeBase + $"/cogitations/{bridgeId}/messages");
                var body    = await cogResp.Content.ReadAsStringAsync();
                return Results.Ok(new { ok = true, bridgeCogId = bridgeId, status = (int)cogResp.StatusCode, messages = body });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = true, bridgeCogId = bridgeId, note = ex.Message });
            }
        });
    }
}
#endif
