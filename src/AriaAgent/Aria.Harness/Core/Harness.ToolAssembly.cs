using System.Text.Json;
using Aria.Harness.Bridge;
using Aria.Harness.Formats;
using Aria.Harness.Tools;
using Aria.Shared;
using Aria.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aria.Harness.Core;

public sealed partial class Harness
{
    private async Task<(
        List<AITool> Tools,
        bool HasTerminal,
        bool HasMemory,
        (string Name, string Path, string Description, string? NodeId, string? Platform)[] Projects,
        Dictionary<string?, string> NodePlatforms)> AssembleToolsAsync(
        HarnessOptions options,
        HarnessContext context,
        string? llmNodeId,
        CancellationToken ct)
    {
        var tools            = new List<AITool>();
        var hasTerminalTools = false;
        var hasMemoryTools   = false;
        (string Name, string Path, string Description, string? NodeId, string? Platform)[] terminalProjects = [];
        var terminalNodePlatforms = new Dictionary<string?, string>();

        // Node-side builtins (datetime, web search) run on the bridge so secrets stay on the node
        // and they render as live tool blocks; fall back to in-process when no bridge is connected.
        var bridgeUp       = await _runtime.IsBridgeAvailableAsync(context, ct);
        var nodeBuiltinSrv = new McpServerConfig("Builtin", "__aria_builtin__", [], Transport: McpTransport.LocalBridge);
        AITool BuiltinBridgeTool(string name, string description, string schema, string? nodeId = null, bool supportsVisionResult = false) =>
            new BridgeMcpTool(name, description, JsonDocument.Parse(schema).RootElement,
                nodeBuiltinSrv, _runtime, context, nodeId, supportsVisionResult);

        // DateTime is always injected. Run on the LLM node so it has the same clock as the session.
        if (bridgeUp)
            tools.Add(BuiltinBridgeTool("GetCurrentDateTime",
                "Report the current temporal datum and time.", """{"type":"object","properties":{}}""", llmNodeId));
        else
            tools.Add(AIFunctionFactory.Create(DateTimeTools.GetCurrentDateTime));

        // Task manifest (todo list) — always-on, in-process UI-coordination tool.
        if (options.OnTodoUpdate != null)
            tools.Add(TodoTools.Create(options.OnTodoUpdate));

        // Structured user question — always-on when the host wires an ask-and-wait callback
        // (interactive chat only; headless runs leave it null and the tool is absent). The
        // callback pauses the call until the user answers, times out, or skips.
        if (options.OnAskUser != null)
            tools.Add(AskUserTools.Create(options.OnAskUser));

        // Context pressure self-report — always-on when the host wires a snapshot provider.
        if (options.ContextStatusProvider != null)
            tools.Add(ContextStatusTools.Create(options.ContextStatusProvider));

        // Fleet overview — always-on when the host wires a fleet snapshot provider (Web's
        // FleetRegistry). Read-only; the cross-node EXECUTION gate is separate (governance).
        if (options.FleetStatusProvider != null)
            tools.Add(FleetStatusTools.Create(options.FleetStatusProvider));

        // Chat capabilities index — always-on, in-process, Web-only-when-wired (Console never
        // sets this, so the tool is simply absent there).
        if (!string.IsNullOrWhiteSpace(options.ChatCapabilitiesText))
            tools.Add(ChatCapabilitiesTools.Create(options.ChatCapabilitiesText));

        // Sub-agent delegation — always-on when the host wires a session-bound spawner. Headless
        // child runs never get one, so a spawned agent cannot itself spawn (one level only).
        if (options.SubAgentSpawner != null)
        {
            tools.Add(SpawnAgentTools.CreateSpawnTool(options.SubAgentSpawner));
            tools.Add(SpawnAgentTools.CreateResultTool(options.SubAgentSpawner));
        }

        foreach (var tool in options.EnabledTools)
        {
            var cfg = tool.Config;
            switch (tool.ToolId)
            {
                case "websearch":
                    if (bridgeUp)
                        tools.Add(BuiltinBridgeTool("SearchWeb",
                            "Performs a web search using the Emperor's Codex Archive.",
                            """{"type":"object","properties":{"query":{"type":"string","description":"The query string for the web search."}},"required":["query"]}""", llmNodeId));
                    else
                        tools.Add(AIFunctionFactory.Create(WebSearchTools.SearchWeb));
                    break;

                case "webfetch":
                    tools.Add(AIFunctionFactory.Create(WebPageTools.FetchWebPage));
                    break;

                case "graph_email":
                {
                    var msToken = await _runtime.GetOAuthTokenAsync("microsoft", context, ct);
                    if (!string.IsNullOrEmpty(msToken))
                    {
                        GraphTools.SetTokenOverride(() => Task.FromResult<string?>(msToken));
                        tools.Add(AIFunctionFactory.Create(GraphTools.GetFirstEmail));
                        tools.Add(AIFunctionFactory.Create(GraphTools.GetEmailsWithFilters));
                        tools.Add(AIFunctionFactory.Create(GraphTools.ListMailboxFolders));
                    }
                    break;
                }

                case "graph_calendar":
                {
                    var msToken = await _runtime.GetOAuthTokenAsync("microsoft", context, ct);
                    if (!string.IsNullOrEmpty(msToken))
                    {
                        GraphTools.SetTokenOverride(() => Task.FromResult<string?>(msToken));
                        tools.Add(AIFunctionFactory.Create(GraphTools.GetCalendarEvents));
                    }
                    break;
                }

                case "google_email":
                {
                    var gToken = await _runtime.GetOAuthTokenAsync("google", context, ct);
                    if (!string.IsNullOrEmpty(gToken))
                    {
                        GoogleTools.SetTokenOverride(() => Task.FromResult<string?>(gToken));
                        tools.Add(AIFunctionFactory.Create(GoogleTools.GetGmailEmails));
                        tools.Add(AIFunctionFactory.Create(GoogleTools.ListGmailLabels));
                    }
                    break;
                }

                case "google_calendar":
                {
                    var gToken = await _runtime.GetOAuthTokenAsync("google", context, ct);
                    if (!string.IsNullOrEmpty(gToken))
                    {
                        GoogleTools.SetTokenOverride(() => Task.FromResult<string?>(gToken));
                        tools.Add(AIFunctionFactory.Create(GoogleTools.GetGoogleCalendarEvents));
                        tools.Add(AIFunctionFactory.Create(GoogleTools.ListGoogleCalendars));
                    }
                    break;
                }

                case "memory":
                {
                    // Noosphere lives on the bridge's local vault — no in-process fallback: without a
                    // bridge there is nowhere on this machine to keep memories.
                    if (bridgeUp)
                    {
                        tools.Add(BuiltinBridgeTool("Inscribe",
                            "Commit a fact, preference, decision, or observation to persistent memory for future sessions. Use proactively — without being asked — whenever the user states a preference or constraint, makes or defers a decision (\"we'll do that later\"), corrects you, or reveals a durable fact about their projects, machines, or accounts. The archive merges duplicates automatically, so inscribe even when unsure the fact is new. Do NOT use for ephemeral task progress, secrets, or small talk. Writes only to the LLM node's local vault (not replicated to other bridges). If the tool returns an error / INSCRIBE DEGRADED, the extraction model on that node is broken (e.g. LM Studio down) — tell the user plainly and do NOT claim the memory was preserved.",
                            """{"type":"object","properties":{"content":{"type":"string","description":"The information to preserve, with enough context to stand alone in a future session (who/what/why). For deferred work, include what was deferred and the reason."}},"required":["content"]}""",
                            llmNodeId));
                        hasMemoryTools = true;
                        // Recall (Probe/Contemplate): single LLM node, or fan-out across all connected
                        // nodes when RecallScope.AllNodes — memory stores stay node-local either way.
                        const string probeSchema = """{"type":"object","properties":{"query":{"type":"string","description":"The query in natural language describing what intelligence you seek (e.g., 'What are this soul's preferences?', 'What happened in their timeline?')"}},"required":["query"]}""";
                        const string probeDesc = "Consults the collective memory of the Noosphere. Use when seeking information previously recorded — querying what is known about a person, event, faction, or matter. Returns extracted facts and intelligence from past observations.";
                        const string contemplateSchema = """{"type":"object","properties":{"query":{"type":"string","description":"The question or topic upon which you seek deep thought and synthesized wisdom (e.g., 'What should I do about this suspected heretic?', 'Summarize what we know about Mars')"}},"required":["query"]}""";
                        const string contemplateDesc = "Engages deep deliberation on a matter of great import. Use when you need a synthesized answer that draws upon everything known across memory — not just raw facts, but reasoned judgment informed by past experiences and observations.";

                        // Recall (Probe/Contemplate) always goes through FanOutMemoryTool, which hits the
                        // Benign /memory/probe endpoint — so it never trips the Layer B seal gate (the old
                        // single-node /tools/call path was classified Sensitive and demanded a node seal,
                        // fatal when memory lives on a different node than the LLM/approval node). AllNodes
                        // fans out to every connected node; ThisNode restricts to the LLM node. Inscribe
                        // (a write) stays a gated built-in tool.
                        var allNodes = options.RecallScope == RecallScope.AllNodes;
                        tools.Add(new FanOutMemoryTool("Probe", probeDesc, JsonDocument.Parse(probeSchema).RootElement,
                            _runtime, context, llmNodeId, synthesize: false, allNodes: allNodes));
                        tools.Add(new FanOutMemoryTool("Contemplate", contemplateDesc, JsonDocument.Parse(contemplateSchema).RootElement,
                            _runtime, context, llmNodeId, synthesize: true, allNodes: allNodes));
                    }
                    break;
                }

                case "wargame":
                    tools.Add(AIFunctionFactory.Create(WargameTools.GetWarSituationReport));
                    break;

                case "screenshot":
                {
                    if (!bridgeUp) break;

                    // Probed only when this tool is actually enabled — avoids a needless round trip
                    // (up to 20s) for sessions that never touch it.
                    var vision = await DetectVisionSupportAsync(options.SelectedSourceName, options.SelectedModel, context, ct);
                    options.OnProgress?.Invoke($"// VISION:  {(vision == VisionSupport.Supported ? "YES" : "NO")}");

                    tools.Add(BuiltinBridgeTool("TakeScreenshot",
                        "Takes a screenshot of a page running on localhost (e.g. the user's own dev server) using a " +
                        "headless browser on the user's machine. Only localhost/127.0.0.1 URLs are allowed. " +
                        (vision == VisionSupport.Supported
                            ? "The captured image is shown to you directly, so you can visually verify layout, styling, and rendered content."
                            : "You do not have vision on this channel: the result is a text description only (URL, dimensions) — describe what you expected and ask the user to confirm how it actually looks."),
                        """{"type":"object","properties":{"url":{"type":"string","description":"The localhost URL to capture, e.g. http://localhost:5129/chat. Must be localhost or 127.0.0.1 — other hosts are rejected."}},"required":["url"]}""",
                        llmNodeId, vision == VisionSupport.Supported));
                    break;
                }

                case "http_request":
                {
                    if (!bridgeUp) break;
                    tools.Add(BuiltinBridgeTool("http_request",
                        "Performs an HTTP request from the user's machine and returns the raw response: status code, " +
                        "headers, and body (unprocessed — no HTML stripping or text extraction). Useful for API testing " +
                        "against localhost or remote endpoints. Redirects are NOT followed (3xx and Location are reported). " +
                        "http:// and https:// URLs only.",
                        """{"type":"object","properties":{"method":{"type":"string","description":"HTTP method: GET, POST, PUT, PATCH, DELETE, HEAD, or OPTIONS."},"url":{"type":"string","description":"Absolute http:// or https:// URL."},"headers":{"type":"object","description":"Optional request headers as name/value string pairs.","additionalProperties":{"type":"string"}},"body":{"type":"string","description":"Optional request body (sent verbatim as UTF-8)."},"timeout_seconds":{"type":"integer","description":"Request timeout in seconds (1-60, default 30)."}},"required":["method","url"]}""",
                        llmNodeId));
                    break;
                }

                case "read_image":
                {
                    if (!bridgeUp) break;

                    // Same vision probe as TakeScreenshot (cached per channel/model — cheap on repeat).
                    var vision = await DetectVisionSupportAsync(options.SelectedSourceName, options.SelectedModel, context, ct);
                    options.OnProgress?.Invoke($"// VISION:  {(vision == VisionSupport.Supported ? "YES" : "NO")}");

                    tools.Add(BuiltinBridgeTool("read_image",
                        "Reads a local image file (png/jpeg/gif/webp, detected by content, max 10 MB) from the user's machine. " +
                        (vision == VisionSupport.Supported
                            ? "The image is shown to you directly, so you can visually inspect screenshots, diagrams, photos, and renders."
                            : "You do not have vision on this channel: the user sees the image, but you only get a text confirmation (path, format, size) — ask the user to describe what matters."),
                        """{"type":"object","properties":{"path":{"type":"string","description":"Absolute path to the image file (png/jpeg/gif/webp, max 10 MB)."}},"required":["path"]}""",
                        llmNodeId, vision == VisionSupport.Supported));
                    break;
                }

                case "terminal":
                {
                    var (registered, projects) = await TryRegisterTerminalToolsAsync(
                        tools, terminalNodePlatforms, options, context, llmNodeId, cfg, ct);
                    if (registered)
                    {
                        hasTerminalTools = true;
                        terminalProjects = projects;
                    }
                    break;
                }

                case "mcp":
                    try
                    {
                        var userServers = options.UserMcpServers?.Where(s => s.Enabled).ToList() ?? [];

                        // Servers with a caller-supplied command run in-process (SSE or stdio from config).
                        var localServers = userServers
                            .Where(s => !string.IsNullOrEmpty(s.Command) && s.Transport != McpTransport.LocalBridge)
                            .ToList();
                        if (localServers.Count > 0)
                        {
                            var mcpTools = await McpTools.GetTools(localServers);
                            foreach (var t in mcpTools) tools.Add(t);
                        }

                        // Bridge-owned servers (empty command or LocalBridge) are resolved and invoked on the node.
                        if (await _runtime.IsBridgeAvailableAsync(context, ct))
                        {
                            var bridgeServers = userServers
                                .Where(s => string.IsNullOrEmpty(s.Command) || s.Transport == McpTransport.LocalBridge)
                                .ToList();
                            foreach (var srv in bridgeServers)
                            {
                                var bridgeTools = await LoadBridgeToolsAsync(srv, context, llmNodeId);
                                foreach (var bt in bridgeTools) tools.Add(bt);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "MCP tools failed to load");
                    }
                    break;
            }
        }

        return (tools, hasTerminalTools, hasMemoryTools, terminalProjects, terminalNodePlatforms);
    }
}
