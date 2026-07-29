using Aria.Bridge.Data;
using Aria.Bridge.Services.Logging;
using Aria.Tools;
using ModelContextProtocol.Protocol;

namespace Aria.Bridge.Endpoints;

public static class ToolEndpoints
{
    public static void MapToolEndpoints(this WebApplication app)
    {
        // Discover tools from a local MCP server (stdio or SSE).
        // The server process / connection is spawned on first call and kept alive for 10 minutes of idle time.
        app.MapPost("/tools/list", async (ToolsListRequest req, SessionStore store, BridgeDbContext db) =>
        {
            // Built-in tools — no child process needed.
            if (req.Command == "__aria_builtin__")
            {
                var infos = BuiltinTools.GetToolInfos();
                BridgeLogger.Log("INFO", $"Listed {infos.Count} built-in tool(s)");
                return Results.Ok(infos);
            }

            var config = await ResolveConfigAsync(req.Command, req.ServerName, req.Arguments, req.Environment, db);
            if (config == null)
                return Results.BadRequest("MCP server not found");

            var label = config.Name;

            McpSession session;
            try
            {
                session = await store.GetOrCreateAsync(config);
                BridgeLogger.Log("INFO", $"Session ready for '{label}'");
            }
            catch (Exception ex)
            {
                BridgeLogger.Log("ERROR", $"Cannot connect to '{label}': {ex.Message}");
                return Results.Problem($"Cannot connect to '{label}': {ex.Message}");
            }

            try
            {
                session.Touch();
                var tools = await session.Client.ListToolsAsync();
                var result = tools.Select(t => new BridgeToolInfo(t.Name, t.Description, t.JsonSchema)).ToList();
                BridgeLogger.Log("INFO", $"Listed {result.Count} tool(s) for '{label}'");
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                // Remove the session so the next request spawns a fresh process.
                await store.RemoveAsync(config);
                BridgeLogger.Log("ERROR", $"ListTools failed for '{label}': {ex.Message}");
                return Results.Problem($"ListTools failed for '{label}': {ex.Message}");
            }
        });

        // Read-only twin of /tools/call for the built-in file-mutation tools: returns the unified
        // diff the call WOULD produce (same policy resolution and scope enforcement, nothing
        // written). Drives the prospective-diff approval card in the web UI; any other tool (or a
        // proxied MCP server) answers "no-preview" so the caller falls back to the args preview.
        app.MapPost("/tools/preview", async (ToolsCallRequest req, BridgeDbContext db) =>
        {
            if (req.Command != "__aria_builtin__")
                return Results.Ok(new BuiltinTools.ToolPreviewResponse(false, null, false, "no-preview"));

            var policy = await NodeTerminalPolicy.ResolveBuiltinPolicyAsync(db, req.Policy, req.SessionId);
            var result = BuiltinTools.Preview(req.ToolName, req.ToolArguments, policy);
            BridgeLogger.Log(result.Ok ? "INFO" : "WARN",
                $"Previewed built-in '{req.ToolName}' (read-only) → ok={result.Ok}{(result.Truncated ? " (truncated)" : "")}{(result.Reason != null ? $" — {result.Reason}" : "")}");
            return Results.Ok(result);
        });

        // Call a named tool on a local MCP server.
        // ToolArguments are JsonElement values so they round-trip without type loss.
        app.MapPost("/tools/call", async (ToolsCallRequest req, SessionStore store, BridgeDbContext db) =>
        {
            // Built-in tools — execute natively, no child process.
            if (req.Command == "__aria_builtin__")
            {
                BridgeLogger.Log("INFO", $"Calling built-in tool '{req.ToolName}'");
                // Built-ins enforce through the same node-authoritative seam as the project-file
                // and git endpoints: node declared paths (∪ this session's node-signed grants) are
                // the maximum; the server-supplied policy may only narrow them, never widen.
                var policy = await NodeTerminalPolicy.ResolveBuiltinPolicyAsync(db, req.Policy, req.SessionId);
                var result = await BuiltinTools.InvokeAsync(req.ToolName, req.ToolArguments, policy, db, req.ContextWindow, req.Checkpoint);
                BridgeLogger.Log(result.IsError ? "WARN" : "INFO",
                    $"Built-in '{req.ToolName}' returned {result.Text.Length} chars{(result.IsError ? " (isError=True)" : "")}");
                return Results.Ok(result);
            }

            var config = await ResolveConfigAsync(req.Command, req.ServerName, req.Arguments, req.Environment, db);
            if (config == null)
                return Results.BadRequest("MCP server not found");

            var label = config.Name;

            McpSession session;
            try
            {
                session = await store.GetOrCreateAsync(config);
            }
            catch (Exception ex)
            {
                BridgeLogger.Log("ERROR", $"Cannot connect to '{label}': {ex.Message}");
                return Results.Problem($"Cannot connect to '{label}': {ex.Message}");
            }

            try
            {
                session.Touch();
                BridgeLogger.Log("INFO", $"Calling tool '{req.ToolName}' on '{label}'");

                // SDK expects IReadOnlyDictionary<string, object?> — cast JsonElement values directly.
                IReadOnlyDictionary<string, object?>? toolArgs = req.ToolArguments?
                    .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

                var result = await session.Client.CallToolAsync(req.ToolName, toolArgs);

                // Concatenate all text content blocks into a single string response.
                var text = string.Concat(result.Content
                    .OfType<TextContentBlock>()
                    .Select(c => c.Text));

                BridgeLogger.Log(result.IsError == true ? "WARN" : "INFO",
                    $"Tool '{req.ToolName}' returned {text.Length} chars{(result.IsError == true ? " (isError=True)" : "")}");
                return Results.Ok(new ToolCallResponse(text, result.IsError == true));
            }
            catch (Exception ex)
            {
                await store.RemoveAsync(config);
                BridgeLogger.Log("ERROR", $"CallTool '{req.ToolName}' failed: {ex.Message}");
                return Results.Problem($"CallTool '{req.ToolName}' failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Resolves the effective MCP config for a request. If <paramref name="command"/> is empty and
    /// <paramref name="serverName"/> matches a node-authored <see cref="BridgeMcpServer"/>, the stored
    /// config (including env secrets) is used. Otherwise the caller-supplied command/args/env are used.
    /// </summary>
    private static async Task<McpServerConfig?> ResolveConfigAsync(
        string? command,
        string? serverName,
        string[]? arguments,
        Dictionary<string, string>? environment,
        BridgeDbContext db)
    {
        if (!string.IsNullOrEmpty(command))
        {
            return new McpServerConfig(
                Name:        serverName ?? command,
                Command:     command,
                Arguments:   arguments ?? [],
                Environment: environment);
        }

        return await McpEndpoints.ResolveMcpConfigAsync(db, serverName);
    }
}
