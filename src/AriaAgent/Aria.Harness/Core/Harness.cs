using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aria.Agent;
using Aria.Harness.Bridge;
using Aria.Harness.Formats;
using Aria.Harness.Governance;
using Aria.Harness.Models;
using Aria.Harness.Tools;
using Aria.Shared;
using Aria.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Aria.Harness.Core;

public sealed class Harness : IHarness
{
    private readonly ILogger<Harness> _logger;
    private readonly IHarnessRuntime _runtime;
    private readonly ConditionalWeakTable<AIAgent, UniversalReasoningHandler> _reasoningHandlers = new();
    private readonly ConditionalWeakTable<AIAgent, GovernanceContext> _governanceContexts = new();

    public Harness(ILogger<Harness> logger, IHarnessRuntime runtime)
    {
        _logger  = logger;
        _runtime = runtime;
    }

    public async Task<(AIAgent Agent, AgentSession Session)> CreateSessionAsync(
        HarnessOptions options,
        HarnessContext context,
        CancellationToken ct = default)
    {
        options.OnProgress?.Invoke("// LINK:    Locating channel...");

        var toolFmt = await DetectToolCallFormatAsync(
            options.SelectedSourceName, options.SelectedModel, context, ct);
        options.ToolCallFormat = toolFmt;   // carried into BuildChatClient so a forced Functionary parse activates
        options.OnProgress?.Invoke($"// TOOLS:   {(toolFmt == ToolCallFormat.None ? "NATIVE OPENAI" : toolFmt.ToString().ToUpper())}");

        // Resolve the bridge node for LLM calls: explicit override, then the selected source's binding.
        var sourceForLlm = _runtime.FindSource(options.SelectedSourceName, context);
        var llmNodeId = options.BridgeNodeId
                     ?? (sourceForLlm?.IsBridged == true ? sourceForLlm.BridgeNodeId : null)
                     ?? context.BridgeNodeId;
        context.BridgeNodeId = llmNodeId;

        var chatClientPair = BuildChatClient(options, context, llmNodeId);
        if (chatClientPair == null)
            throw new InvalidOperationException("No channel selected. Select a channel in the sidebar.");

        var (chatClient, reasoningHandler) = chatClientPair.Value;

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
                            "Commit a fact, preference, decision, or observation to persistent memory for future sessions. Use proactively — without being asked — whenever the user states a preference or constraint, makes or defers a decision (\"we'll do that later\"), corrects you, or reveals a durable fact about their projects, machines, or accounts. The archive merges duplicates automatically, so inscribe even when unsure the fact is new. Do NOT use for ephemeral task progress, secrets, or small talk.",
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
                    if (!await _runtime.IsBridgeAvailableAsync(context, ct)) break;

                    // Bridge-authoritative project list takes precedence; fall back to the legacy
                    // tool-config AllowedPaths only when the host did not supply it.
                    var allNamedPaths = options.TerminalProjects is { Count: > 0 }
                        ? options.TerminalProjects.Select(p => (p.Name, p.Path, p.Description, p.NodeId, p.Platform)).ToArray()
                        : ParseNamedPaths(cfg.GetValueOrDefault("AllowedPaths", ""));
                    var scopedNamedPaths = allNamedPaths;
                    var blockedCmds      = ParseConfigLines(cfg.GetValueOrDefault("BlockedCommands", ""));

                    // Active-project scope: when the chat has a project selected, restrict the Terminal
                    // tool to just that project so the bridge's path enforcement blocks every other
                    // declared project. Only narrows when the selection actually matches a declared
                    // project — an unmatched/stale path leaves all projects accessible rather than
                    // locking the agent out entirely.
                    if (!string.IsNullOrWhiteSpace(options.ActiveProjectPath))
                    {
                        static string Norm(string p)
                        {
                            try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar); }
                            catch { return p; }
                        }
                        var target = Norm(options.ActiveProjectPath);
                        var scoped = allNamedPaths.Where(p => Norm(p.Path) == target).ToArray();
                        if (scoped.Length > 0) scopedNamedPaths = scoped;
                    }

                    // Group projects by target bridge node. Null/empty nodeId means "use the LLM node".
                    var projectsByNode = scopedNamedPaths
                        .GroupBy(p => string.IsNullOrEmpty(p.NodeId) ? llmNodeId : p.NodeId)
                        .ToList();

                    var nodePlatforms = new Dictionary<string?, string>();
                    var nodeGroups = new List<(string? NodeId, string[] Paths, IList<AITool> Tools)>();
                    foreach (var group in projectsByNode)
                    {
                        var nodeId = group.Key;
                        var projectsInGroup = group.ToArray();
                        var allowedPaths = projectsInGroup.Select(e => e.Path).ToArray();
                        var builtinSrv = new McpServerConfig(
                            Name:            "Terminal",
                            Command:         "__aria_builtin__",
                            Arguments:       [],
                            Transport:       McpTransport.LocalBridge,
                            AllowedPaths:    allowedPaths.Length > 0 ? allowedPaths : null,
                            BlockedCommands: blockedCmds.Length > 0 ? blockedCmds : null);

                        try
                        {
                            var termTools = await LoadBridgeToolsAsync(builtinSrv, context, nodeId);
                            if (termTools.Count > 0)
                            {
                                nodeGroups.Add((nodeId, allowedPaths, termTools));
                                hasTerminalTools = true;
                            }

                            var platform = projectsInGroup.Select(p => p.Platform).FirstOrDefault(p => !string.IsNullOrEmpty(p));
                            if (!string.IsNullOrEmpty(platform) && !terminalNodePlatforms.ContainsKey(nodeId))
                                terminalNodePlatforms[nodeId] = platform;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Terminal built-in tools failed to load for node {NodeId}", nodeId ?? "(default)");
                        }
                    }

                    if (nodeGroups.Count == 1)
                    {
                        foreach (var t in nodeGroups[0].Tools) tools.Add(t);
                    }
                    else if (nodeGroups.Count > 1)
                    {
                        // Projects live on several machines but the tool NAMES are identical — adding
                        // every node's set verbatim would collide and strand all calls on the first
                        // node (a Windows path then hits the Mac bridge and is blocked). Merge into
                        // one path-routed dispatcher per tool name.
                        var defaultIdx = Math.Max(0, nodeGroups.FindIndex(g => g.NodeId == llmNodeId));
                        foreach (var name in nodeGroups.SelectMany(g => g.Tools.Select(t => t.Name)).Distinct())
                        {
                            var candidates = new List<PathRoutedTerminalTool.Candidate>();
                            var defCandidate = 0;
                            for (var i = 0; i < nodeGroups.Count; i++)
                            {
                                var fn = nodeGroups[i].Tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == name);
                                if (fn == null) continue;
                                if (i == defaultIdx) defCandidate = candidates.Count;
                                candidates.Add(new PathRoutedTerminalTool.Candidate(fn, nodeGroups[i].Paths));
                            }
                            if (candidates.Count > 0)
                                tools.Add(new PathRoutedTerminalTool(candidates, defCandidate));
                        }
                    }

                    if (hasTerminalTools)
                        terminalProjects = allNamedPaths;
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

        var baseInstructions = options.InstructionsOverride ?? AgentDefaults.SystemMessage;
        if (hasTerminalTools)
            baseInstructions += BuildTerminalAddendum(terminalProjects, terminalNodePlatforms, options.ActiveProjectPath);
        // Gated on actual tool registration (not on the user's toggle): the memory tools also vanish
        // when the bridge is down, and the prompt must never reference absent tools.
        if (hasMemoryTools)
            baseInstructions += BuildMemoryAddendum();

        // Always wrap every tool in a governance decorator (even when the initial mode is Off) so a
        // later mode change applies to this existing session — the per-turn policy is refreshed in
        // StreamAsync. The wrapper is invisible to the model (name/description/schema delegate to the
        // inner tool) and only intervenes to block, gate, or seal a call at invocation time.
        var govCtx = new GovernanceContext(options.Governance ?? GovernancePolicy.FromMode(GovernanceMode.Off));
        tools = tools
            .Select(t => t is AIFunction f
                ? (AITool)new GovernedTool(f, govCtx, options.OnApprovalRequested,
                      options.OnToolStart, options.OnToolComplete)
                : t)
            .ToList();

        // Deduplicate by function name (first wins). EnabledTools can carry the same tool twice
        // (e.g. globally enabled + sub-agent set), and duplicate function declarations are rejected
        // by OpenAI and confuse local servers' chat templates.
        var seenToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        tools = tools.Where(t => seenToolNames.Add(t.Name)).ToList();

        var agent = chatClient.AsAIAgent(
            name: null,
            instructions: baseInstructions,
            tools: tools);

        _reasoningHandlers.GetValue(agent, _ => reasoningHandler);
        _governanceContexts.GetValue(agent, _ => govCtx);

        var session = await agent.CreateSessionAsync();
        options.OnProgress?.Invoke("Session active");
        return (agent, session);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string userMessage,
        AIAgent agent,
        AgentSession session,
        HarnessContext context,
        IReadOnlyList<string>? turnScopePaths = null,
        GovernancePolicy? turnPolicy = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        Action<ChatTokenUsage>? onUsage = null)
    {
        var message = new UserChatMessage(userMessage);
        _reasoningHandlers.TryGetValue(agent, out var handler);

        // Reset per-turn governance budgets/loop history, set this turn's allowed scope, and refresh
        // the policy so a live mode change (from the Tools panel) applies without rebuilding the session.
        _governanceContexts.TryGetValue(agent, out var govCtx);
        govCtx?.BeginTurn(turnScopePaths, turnPolicy);

        await foreach (var update in agent.RunStreamingAsync([message], session, cancellationToken: ct).WithCancellation(ct))
        {
            if (update.ContentUpdate.Count > 0 && !string.IsNullOrEmpty(update.ContentUpdate[0].Text))
                yield return update.ContentUpdate[0].Text;
            if (update.Usage != null)
                onUsage?.Invoke(update.Usage);
        }

        // Layer B seal pause: a sensitive tool hit the node gate with no live grant and terminated the
        // function-calling loop (it couldn't throw — the framework would swallow it into a retry). Re-raise
        // here, above the function-invocation layer, so the turn halts and the approval ceremony runs; on
        // approval the whole turn is retried and the tool succeeds under the fresh 8h grant.
        if (govCtx?.ContextApprovalPending == true)
            throw new Aria.Shared.ContextApprovalRequiredException(
                govCtx.ContextApprovalSessionId,
                "Context approval required — approve sensitive operations at your node.");

        // Some thinking models (e.g. Qwen3 StartsInThinkMode) stop inside their think block on
        // tool-continuation turns, producing only internal monologue. The SSE layer discards that
        // monologue so it cannot poison history. Nudge the model once for a proper final answer.
        if (handler?.LastStreamHadUnresolvedThinking == true)
        {
            _logger.LogInformation("Stream ended with unresolved thinking; re-prompting for final answer");
            var nudge = new UserChatMessage("Provide your final answer to the user now.");
            await foreach (var update in agent.RunStreamingAsync([nudge], session, cancellationToken: ct).WithCancellation(ct))
            {
                if (update.ContentUpdate.Count > 0 && !string.IsNullOrEmpty(update.ContentUpdate[0].Text))
                    yield return update.ContentUpdate[0].Text;
                if (update.Usage != null)
                    onUsage?.Invoke(update.Usage);
            }
        }
    }

    // ── Chat client construction ──────────────────────────────────────────────

    private (ChatClient Client, UniversalReasoningHandler Handler)? BuildChatClient(HarnessOptions options, HarnessContext context, string? bridgeNodeId = null)
    {
        var source = _runtime.FindSource(options.SelectedSourceName, context);
        if (source == null) return null;

        var resolvedModel = options.SelectedModel ?? source.Models.FirstOrDefault() ?? "default";

        var routeViaBridge = source.IsBridged || source.IsPublicProvider;
        var keyRef         = routeViaBridge ? (source.ChannelName ?? source.Name) : null;
        var requireKey     = source.IsPublicProvider;

        HttpMessageHandler innerHandler = routeViaBridge
            ? new BridgeHttpHandler(_runtime, context, keyRef, requireKey, bridgeNodeId)
            : new HttpClientHandler();

        var handler = new UniversalReasoningHandler
        {
            InnerHandler       = innerHandler,
            OnReasoningContent = options.OnThinkingToken,
            StartsInThinkMode  = options.ThinkingFormat == ThinkingFormat.StartsInThinkMode,
            StreamThinkingLive = options.ThinkingFormat is ThinkingFormat.ReasoningContent
                                       or ThinkingFormat.ThinkTags
                                       or ThinkingFormat.StartsInThinkMode
                                       or ThinkingFormat.ChannelThought
                                       or ThinkingFormat.Harmony,
            // Only Functionary changes runtime tool parsing; every other format is marker-auto-detected.
            ForcedToolFormat   = options.ToolCallFormat
        };

        var client = ChatClientFactory.Build(source, resolvedModel, handler);
        return (client, handler);
    }

    // ── Bridge tool loading ───────────────────────────────────────────────────

    private async Task<IList<AITool>> LoadBridgeToolsAsync(
        McpServerConfig server,
        HarnessContext context,
        string? nodeId = null)
    {
        var listBody = JsonSerializer.Serialize(new
        {
            command     = server.Command,
            arguments   = server.Arguments,
            environment = server.Environment,
            serverName  = server.Name,
            policy      = server.AllowedPaths?.Length > 0 || server.BlockedCommands?.Length > 0
                ? new { allowedPaths = server.AllowedPaths ?? [], blockedCommands = server.BlockedCommands ?? [] }
                : (object?)null,
        });

        string responseJson;
        try
        {
            responseJson = await _runtime.BridgePostAsync("http://localhost:5741/tools/list", listBody, context, context.CancellationToken, nodeId: nodeId);
        }
        catch (Exception ex)
        {
            _logger.LogInformation("LocalBridge: tool list unavailable for '{Server}': {Message}", server.Name, ex.Message);
            return [];
        }

        if (!responseJson.TrimStart().StartsWith('['))
        {
            _logger.LogWarning("LocalBridge: tool list error for '{Server}': {Response}", server.Name, responseJson);
            return [];
        }

        try
        {
            var toolInfos = JsonSerializer.Deserialize<List<BridgeToolInfo>>(responseJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (toolInfos == null) return [];

            return toolInfos
                .Select(t => (AITool)new BridgeMcpTool(
                    t.Name, t.Description, t.JsonSchema, server, _runtime, context, nodeId))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalBridge: failed to parse tool list for '{Server}'", server.Name);
            return [];
        }
    }

    // ── Memory addendum ───────────────────────────────────────────────────────

    // Injected only when the memory tools were actually registered this session (see the hasMemoryTools
    // gate at the assembly site). Gives the model concrete save/recall triggers — the tool descriptions
    // alone proved too weak, especially for small local models, and the Minimal Action Principle
    // otherwise suppresses self-initiated Inscribe calls.
    private static string BuildMemoryAddendum() => """


        ## Memory (Noosphere)

        You have persistent memory that survives across sessions. Saving to it is ALWAYS permitted — it is exempt from the Minimal Action Principle and needs no explicit request from the user.

        Inscribe proactively when the user reveals something with value beyond this session:
        - Preferences, constraints, or standing rules ("I prefer X", "never do Y", "from now on…")
        - Decisions and deferrals ("let's do Z later", "we'll go with option B") — record what was decided or deferred, and why, so a future session can resume it
        - Corrections ("no, use W not V") — record the corrected fact
        - Durable facts about the user's machines, projects, servers, accounts, or environment quirks
        - Named tools, technologies, or people the user clearly intends to revisit

        Do NOT inscribe: ephemeral task progress, anything already recorded in the project/repo itself, secrets or credentials, and small talk.

        The archive merges duplicates and links entities automatically — never hold back an Inscribe because the fact might already exist. When a fact changes, simply inscribe the new version.

        Probe memory whenever a request may depend on earlier sessions — recurring project or person names, "what did we decide about…", or any task that resumes prior work.
        """;

    // ── Terminal addendum ─────────────────────────────────────────────────────

    private static string BuildTerminalAddendum(
        (string Name, string Path, string Description, string? NodeId, string? Platform)[] projects,
        IReadOnlyDictionary<string?, string> nodePlatforms,
        string? activeProjectPath = null)
    {
        static string Norm(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return p; }
        }

        var distinctPlatforms = projects.Select(p => p.Platform).Concat(nodePlatforms.Values)
            .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mixedPlatforms = distinctPlatforms.Count > 1;

        var platform = nodePlatforms.Values.FirstOrDefault(p => !string.IsNullOrEmpty(p))
                    ?? projects.Select(p => p.Platform).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        var isWindows = platform?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true;

        var shellName   = mixedPlatforms ? "per project — see Allowed Projects below" : (isWindows ? "cmd.exe / PowerShell" : "bash");
        var homeMacro   = "`~` expands to the home directory";
        var sepHint     = mixedPlatforms
            ? "Path style follows each project's OS: `\\` and drive letters on Windows, `/` elsewhere."
            : (isWindows ? "Paths use `\\\\` separators." : "Paths use `/` separators.");
        var homeExample = isWindows ? "`C:\\Users\\<user>`" : "`/home/<user>`";

        // Tag each project with its OS when projects span machines, so the model keeps Windows
        // path syntax for Windows projects and POSIX syntax for the rest — and never blends them.
        string ProjectLabel((string Name, string Path, string Description, string? NodeId, string? Platform) p)
        {
            var os = mixedPlatforms && !string.IsNullOrEmpty(p.Platform) ? $" [{p.Platform}]" : "";
            return string.IsNullOrWhiteSpace(p.Description)
                ? $"- **{p.Name}**{os}: `{p.Path}`"
                : $"- **{p.Name}**{os} (`{p.Path}`): {p.Description}";
        }

        var projectsSection = projects.Length > 0
            ? "\n\n### Allowed Projects\n" +
              string.Join("\n", projects.Select(ProjectLabel)) +
              "\n\nYou may only access files and run commands within these paths. Use these exact absolute paths — do not guess or infer other locations. " +
              "If the user names a project by its name or path above, use it directly without asking for clarification. " +
              "Project names may be partial, lowercase, or abbreviated — match them case-insensitively and by prefix/substring (e.g. 'spectra' → 'Spectra.MLX'). " +
              "The user can switch the active project at any time by typing `/project`." +
              (mixedPlatforms
                  ? " Projects live on DIFFERENT machines: every tool call is executed on the machine that owns the path you pass, so always copy the project's path prefix verbatim (drive letter and separators included) and never rewrite it into another OS's style."
                  : "")
            : "\n\n### Allowed Projects\nNo terminal projects are currently available. Do not access the filesystem.";

        var otherProjects = activeProjectPath is null
            ? []
            : projects.Where(p => !string.Equals(
                Norm(p.Path), Norm(activeProjectPath), StringComparison.OrdinalIgnoreCase)).ToArray();
        var otherSection = otherProjects.Length > 0
            ? "\n\n### Other known projects (not currently active)\n" +
              string.Join("\n", otherProjects.Select(ProjectLabel)) +
              "\n\nIf the user asks about one of these, tell them which project is currently active and that they can switch to it with `/project` — do not attempt to read or list files outside the Allowed Projects list."
            : "";

        return $"""


        ## Terminal Access

        You have direct shell and filesystem access on the user's machine via built-in tools. The target environment is **{(mixedPlatforms ? "mixed — each project lists its OS" : platform ?? "unknown OS")}**; adapt your commands accordingly:
        - Shell: **{shellName}**
        - {sepHint} {homeMacro} (e.g., {homeExample}).
        - **bash_exec** — run any shell command (returns JSON with exit_code, stdout, stderr)
        - **run_background** — start a long-running command detached (dev server, watcher, etc.)
        - **wait_for** — wait for a port, URL, or log pattern to become ready
        - **process_output** — read the log of a tracked background job
        - **process_kill** — stop a tracked background job
        - **read_file** — read file contents (supports line ranges; returns numbered lines)
        - **write_file** — write/create a file (creates parent directories automatically)
        - **edit_file** — replace an exact string in a file (old_string must appear exactly once; widen context if ambiguous)
        - **list_dir** — list directory entries with types and sizes
        - **glob** — find files by pattern (supports ** recursion, e.g. `**/*.cs`, `src/**/*.ts`)
        - **commands_index** — get build/run/test commands for any language or framework{projectsSection}{otherSection}

        ### Workflow guidelines
        - **Act minimally**: take only the steps the user explicitly requested. If asked to read one file, read that one file — do not explore directories or read other files unless required.
        - Use absolute paths.
        - **Before editing (not reading)**: use `list_dir` or `glob` to locate a file if its exact path is unknown, and `read_file` to confirm exact content before calling `edit_file`.
        - **edit_file requires uniqueness**: if `old_string` is not found or appears multiple times, the call will fail — add more surrounding lines to make it unique.
        - **Check exit codes**: `bash_exec` returns `exit_code`; treat non-zero as an error and inspect `stderr`.
        - **Long-running process loop**: for dev servers, watchers, and similar, use `run_background`; wait for readiness with `wait_for` (port, URL, or log pattern); stream logs with `process_output`; stop with `process_kill`. If a foreground `bash_exec` exceeds `timeout_seconds`, it is converted to a background job instead of being killed.
        - Call `commands_index(topic="rust")` (or python, go, dotnet, docker, git, etc.) before running unfamiliar build commands.
        """;
    }

    private static string[] ParseConfigLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
            .ToArray();

    private static (string Name, string Path, string Description, string? NodeId, string? Platform)[] ParseNamedPaths(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Select(e => (
                    Name:        e.TryGetProperty("name",        out var n) ? n.GetString() ?? "" : "",
                    // Users paste paths with surrounding quotes ("C:\...") — strip them, or the
                    // bridge's allowed-path prefix check can never match.
                    Path:        (e.TryGetProperty("path",       out var p) ? p.GetString() ?? "" : "").Trim().Trim('"', '\''),
                    Description: e.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    NodeId:      e.TryGetProperty("nodeId",      out var nid) ? nid.GetString() : null,
                    Platform:    e.TryGetProperty("platform",    out var plf) ? plf.GetString() : null))
                .Where(e => !string.IsNullOrWhiteSpace(e.Path))
                .ToArray();
        }
        catch { return []; }
    }

    // ── Format detection ──────────────────────────────────────────────────────

    public async Task<ToolCallFormat> DetectToolCallFormatAsync(
        string? selectedSourceName,
        string? modelId,
        HarnessContext context,
        CancellationToken ct = default)
    {
        var source = _runtime.FindSource(selectedSourceName, context);

        if (source?.IsPublicProvider == true)
            return ToolCallFormat.None;

        if (source != null)
        {
            var cached = await _runtime.FormatCache.GetToolCallFormatAsync(source.Url,
                modelId ?? source.Models.FirstOrDefault() ?? "", ct);
            if (cached.HasValue && cached.Value != ToolCallFormat.Unknown) return cached.Value;
        }

        if (source?.IsBridged == true)
        {
            // Bridge detection happens inside thinking detection; if not available, assume native.
            var thinkFmt = await DetectThinkingFormatAsync(selectedSourceName, modelId, context, ct);
            var cached = await _runtime.FormatCache.GetToolCallFormatAsync(source.Url,
                modelId ?? source.Models.FirstOrDefault() ?? "", ct);
            return cached ?? ToolCallFormat.None;
        }

        var format = await RunToolCallDetectionAsync(selectedSourceName, modelId, context, ct);
        _logger.LogInformation("Tool-call format for {Source}/{Model}: {Format}", selectedSourceName, modelId, format);

        if (source != null && format != ToolCallFormat.Unknown)
            await _runtime.FormatCache.SetToolCallFormatAsync(source.Url,
                modelId ?? source.Models.FirstOrDefault() ?? "", format, ct);

        return format;
    }

    public async Task<ThinkingFormat> DetectThinkingFormatAsync(
        string? selectedSourceName,
        string? modelId,
        HarnessContext context,
        CancellationToken ct = default)
    {
        var source = _runtime.FindSource(selectedSourceName, context);

        if (source?.IsPublicProvider == true)
            return ThinkingFormat.None;

        if (source != null)
        {
            var cached = await _runtime.FormatCache.GetThinkingFormatAsync(source.Url,
                modelId ?? source.Models.FirstOrDefault() ?? "", ct);
            if (cached.HasValue && cached.Value != ThinkingFormat.Unknown) return cached.Value;
        }

        ThinkingFormat format;
        if (source?.IsBridged == true)
        {
            format = await _runtime.IsBridgeAvailableAsync(context, ct)
                ? await RunBridgeDetectionAsync(source, modelId, context, ct)
                : ThinkingFormat.None;
        }
        else
        {
            format = await RunDetectionAsync(selectedSourceName, modelId, context, ct);
        }

        // NOTE: a model-name heuristic used to force StartsInThinkMode here when the probe returned
        // None. Removed: any probe failure (endpoint auth, server down) also yields no markers, and
        // forcing think-mode on a non-thinking stream swallows the ENTIRE answer into the thinking
        // block. The probe detects genuine start-in-think models via their closing tag, and
        // reasoning_content is handled dynamically regardless of the detected format.

        _logger.LogInformation("Thinking format for {Source}/{Model}: {Format}", selectedSourceName, modelId, format);

        if (source != null && format != ThinkingFormat.None && format != ThinkingFormat.Unknown)
            await _runtime.FormatCache.SetThinkingFormatAsync(source.Url,
                modelId ?? source.Models.FirstOrDefault() ?? "", format, ct);

        return format;
    }

    // Vision is probed for every source, including public/cloud providers — unlike thinking/tool-call
    // format, it varies per model within the same provider (e.g. gpt-4o vs a text-only variant), so it
    // can't be short-circuited on IsPublicProvider like the others.
    public async Task<VisionSupport> DetectVisionSupportAsync(
        string? selectedSourceName,
        string? modelId,
        HarnessContext context,
        CancellationToken ct = default)
    {
        var source = _runtime.FindSource(selectedSourceName, context);
        if (source == null) return VisionSupport.Unknown;

        var cached = await _runtime.FormatCache.GetVisionSupportAsync(source.Url,
            modelId ?? source.Models.FirstOrDefault() ?? "", ct);
        if (cached.HasValue && cached.Value != VisionSupport.Unknown) return cached.Value;

        VisionSupport support;
        if (source.IsBridged)
        {
            // RunBridgeDetectionAsync's /llm/detect-format round trip already probes + caches vision
            // alongside thinking/tool-call — reuse it instead of a second bridge call.
            if (await _runtime.IsBridgeAvailableAsync(context, ct))
            {
                await RunBridgeDetectionAsync(source, modelId, context, ct);
                var recached = await _runtime.FormatCache.GetVisionSupportAsync(source.Url,
                    modelId ?? source.Models.FirstOrDefault() ?? "", ct);
                support = recached ?? VisionSupport.Unknown;
            }
            else
            {
                support = VisionSupport.Unknown;
            }
        }
        else
        {
            support = await RunVisionDetectionAsync(selectedSourceName, modelId, context, ct);
        }

        _logger.LogInformation("Vision support for {Source}/{Model}: {Support}", selectedSourceName, modelId, support);

        if (support != VisionSupport.Unknown)
            await _runtime.FormatCache.SetVisionSupportAsync(source.Url,
                modelId ?? source.Models.FirstOrDefault() ?? "", support, ct);

        return support;
    }

    private async Task<VisionSupport> RunVisionDetectionAsync(
        string? selectedSourceName, string? modelId, HarnessContext context, CancellationToken ct)
    {
        var (endpoint, model, apiKey) = ResolveEndpoint(selectedSourceName, modelId, context);
        if (endpoint == null) return VisionSupport.Unknown;

        var result = await Aria.Shared.FormatProber.ProbeVisionAsync(endpoint, model, apiKey, ct: ct);
        return Enum.TryParse<VisionSupport>(result, out var parsed) ? parsed : VisionSupport.Unknown;
    }

    public async Task<(ThinkingFormat Thinking, ToolCallFormat ToolCall)> ForceRedetectAsync(
        string sourceName,
        string modelId,
        HarnessContext context,
        CancellationToken ct = default)
    {
        var source = _runtime.FindSource(sourceName, context);
        if (source != null)
        {
            await _runtime.FormatCache.SetThinkingFormatAsync(source.Url, modelId, ThinkingFormat.Unknown, ct);
            await _runtime.FormatCache.SetToolCallFormatAsync(source.Url, modelId, ToolCallFormat.Unknown, ct);
        }

        var tf  = await DetectThinkingFormatAsync(sourceName, modelId, context, ct);
        var tcf = await DetectToolCallFormatAsync(sourceName, modelId, context, ct);
        return (tf, tcf);
    }

    private async Task<ThinkingFormat> RunBridgeDetectionAsync(
        ModelSource source, string? modelId, HarnessContext context, CancellationToken ct)
    {
        var model   = modelId ?? source.Models.FirstOrDefault() ?? "default";
        var chatUrl = source.Url.TrimEnd('/') + "/chat/completions";
        var payload = JsonSerializer.Serialize(new { url = chatUrl, model, keyRef = source.ChannelName ?? source.Name });

        _logger.LogInformation("[FormatDetect] Sending /llm/detect-format to bridge: url={Url} model={Model}", source.Url, model);
        try
        {
            var responseJson = await _runtime.BridgePostAsync(
                "http://localhost:5741/llm/detect-format", payload, context, ct, nodeId: context.BridgeNodeId);

            _logger.LogInformation("[FormatDetect] Bridge responded: {Body}",
                responseJson.Length > 200 ? responseJson[..200] : responseJson);

            using var doc = JsonDocument.Parse(responseJson);
            var thinkFmt = ThinkingFormat.None;
            if (doc.RootElement.TryGetProperty("thinking", out var tf) &&
                Enum.TryParse<ThinkingFormat>(tf.GetString(), out var parsedTf))
                thinkFmt = parsedTf;

            if (doc.RootElement.TryGetProperty("toolCall", out var tc) &&
                Enum.TryParse<ToolCallFormat>(tc.GetString(), out var toolFmt))
            {
                await _runtime.FormatCache.SetToolCallFormatAsync(source.Url, modelId ?? source.Models.FirstOrDefault() ?? "", toolFmt, ct);
                _logger.LogInformation("[FormatDetect] Bridge tool-call probe result: {Format}", toolFmt);
            }

            if (doc.RootElement.TryGetProperty("vision", out var vs) &&
                Enum.TryParse<VisionSupport>(vs.GetString(), out var visionFmt))
            {
                await _runtime.FormatCache.SetVisionSupportAsync(source.Url, modelId ?? source.Models.FirstOrDefault() ?? "", visionFmt, ct);
                _logger.LogInformation("[FormatDetect] Bridge vision probe result: {Format}", visionFmt);
            }

            return thinkFmt;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FormatDetect] Bridge format detection failed for source {Source}", source.Name);
            return ThinkingFormat.None;
        }
    }

    private async Task<ThinkingFormat> RunDetectionAsync(
        string? selectedSourceName, string? modelId, HarnessContext context, CancellationToken ct)
    {
        var (endpoint, model, apiKey) = ResolveEndpoint(selectedSourceName, modelId, context);
        if (endpoint == null) return ThinkingFormat.None;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        bool sawOpenThink  = false;
        bool sawCloseThink = false;
        bool sawReasoning  = false;
        bool sawHarmony    = false;

        try
        {
            using var http = new HttpClient();
            if (!string.IsNullOrEmpty(apiKey))
                http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var body = JsonSerializer.Serialize(new
            {
                model,
                messages = new[] { new { role = "user", content = "What is 3 times 7? Think step by step." } },
                stream     = true,
                // Safety bound — a long-reasoning model given no cap keeps generating for minutes
                // after the probe has its answer; the markers all appear in the earliest deltas.
                max_tokens = 2048
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!resp.IsSuccessStatusCode) return ThinkingFormat.None;

            await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(linked.Token)) != null)
            {
                if (!line.StartsWith("data: ")) continue;
                var json = line["data: ".Length..];
                if (json == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() == 0) continue;
                    var delta = choices[0].GetProperty("delta");

                    // "reasoning_content" (OpenAI o-series/DeepSeek style) and "reasoning" (LM Studio's
                    // field for GPT-OSS — it parses the Harmony <|channel|> envelope server-side and
                    // never puts those raw tokens on the wire, so the literal <|channel|>analysis check
                    // below never fires for it) are the same shape: a separate reasoning stream next to
                    // "content". Treat them as aliases.
                    if (delta.TryGetProperty("reasoning_content", out _) || delta.TryGetProperty("reasoning", out _))
                        sawReasoning = true;

                    if (delta.TryGetProperty("content", out var contentEl))
                    {
                        var text = contentEl.GetString() ?? "";
                        if (text.Contains("<think>",              StringComparison.OrdinalIgnoreCase)) sawOpenThink  = true;
                        if (text.Contains("<thinking>",           StringComparison.OrdinalIgnoreCase)) sawOpenThink  = true;
                        if (text.Contains("</think>",             StringComparison.OrdinalIgnoreCase)) sawCloseThink = true;
                        if (text.Contains("</thinking>",          StringComparison.OrdinalIgnoreCase)) sawCloseThink = true;
                        if (text.Contains("<|channel|>analysis",   StringComparison.OrdinalIgnoreCase)) sawHarmony    = true;
                        if (text.Contains("<|channel|>commentary", StringComparison.OrdinalIgnoreCase)) sawHarmony    = true;
                        if (text.Contains("<|channel|>final",      StringComparison.OrdinalIgnoreCase)) sawHarmony    = true;
                    }

                    // Each of these is a final verdict on its own — stop reading so a slow local
                    // model doesn't burn the probe budget (and its own GPU) for nothing.
                    if (sawReasoning || sawHarmony || sawOpenThink) break;
                }
                catch { /* malformed chunk, skip */ }
            }
        }
        catch (OperationCanceledException) { /* timeout or cancel — use what we detected */ }
        catch (Exception ex) when (ex is HttpIOException || ex.InnerException is HttpIOException)
        {
            _logger.LogDebug("Thinking probe stream closed early for {Endpoint} (expected after early exit)", endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thinking format detection failed for {Endpoint}", endpoint);
        }

        if (sawReasoning) return ThinkingFormat.ReasoningContent;
        if (sawHarmony)   return ThinkingFormat.Harmony;
        if (sawOpenThink)  return ThinkingFormat.ThinkTags;
        if (sawCloseThink) return ThinkingFormat.StartsInThinkMode;
        return ThinkingFormat.None;
    }

    private async Task<ToolCallFormat> RunToolCallDetectionAsync(
        string? selectedSourceName, string? modelId, HarnessContext context, CancellationToken ct)
    {
        var (endpoint, model, apiKey) = ResolveEndpoint(selectedSourceName, modelId, context);
        if (endpoint == null) return ToolCallFormat.Unknown;

        // 45s, not 20: reasoning models think before emitting the tool call, and that thinking
        // alone can outrun a short budget on local hardware.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        var fullContent       = new StringBuilder();
        bool sawNativeToolCall = false;

        try
        {
            using var http = new HttpClient();
            if (!string.IsNullOrEmpty(apiKey))
                http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var body = JsonSerializer.Serialize(new
            {
                model,
                messages = new[] { new { role = "user", content = "Call get_time with no arguments." } },
                tools = new[]
                {
                    new
                    {
                        type = "function",
                        function = new
                        {
                            name        = "get_time",
                            description = "Returns the current time",
                            parameters  = new { type = "object", properties = new { } }
                        }
                    }
                },
                stream     = true,
                // Safety bound — see RunDetectionAsync: never hand a probe an unbounded budget.
                max_tokens = 2048
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!resp.IsSuccessStatusCode) return ToolCallFormat.Unknown;

            await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(linked.Token)) != null)
            {
                if (!line.StartsWith("data: ")) continue;
                var json = line["data: ".Length..];
                if (json == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() == 0) continue;
                    var delta = choices[0].GetProperty("delta");

                    if (delta.TryGetProperty("tool_calls", out _))
                        sawNativeToolCall = true;

                    if (delta.TryGetProperty("content", out var cEl))
                        fullContent.Append(cEl.GetString() ?? "");

                    // Both signals are final verdicts — stop reading as soon as either lands so a
                    // slow local model doesn't burn the probe budget (and its own GPU) for nothing.
                    if (sawNativeToolCall || ClassifyClientToolCallText(fullContent.ToString()) != null) break;
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { /* timeout or cancel — use what we detected */ }
        catch (Exception ex) when (ex is HttpIOException || ex.InnerException is HttpIOException)
        {
            _logger.LogDebug("Tool-call probe stream closed early for {Endpoint} (expected after early exit)", endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool-call format detection failed for {Endpoint}", endpoint);
        }

        if (sawNativeToolCall) return ToolCallFormat.None;

        return ClassifyClientToolCallText(fullContent.ToString()) ?? ToolCallFormat.Unknown;
    }

    // Client-parsed tool-call envelope markers, checked against accumulated content. Returns the
    // format, or null if no marker has appeared (yet).
    private static ToolCallFormat? ClassifyClientToolCallText(string text)
    {
        if (text.Contains("<|channel|>commentary to=functions.", StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.Harmony;
        if (text.Contains("<|channel|>analysis to=functions.",  StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.Harmony;
        if (text.Contains("<tool_call>",                        StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.ToolCallTag;
        if (text.Contains("<start_function_call>",              StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.StartFunctionCall;
        if (text.Contains("[TOOL_CALLS]",                       StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.MistralToolCalls;
        if (text.Contains("<minimax:tool_call>",                StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.MinimaxToolCall;
        if (text.Contains("<|tool_calls_section_begin|>",       StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.KimiK2;
        if (text.Contains("<longcat_tool_call>",                StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.Longcat;
        if (text.Contains("<arg_key>",                          StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.GlmXml;
        return null;
    }

    private (string? endpoint, string model, string? apiKey) ResolveEndpoint(string? sourceName, string? modelId, HarnessContext context)
    {
        var source = _runtime.FindSource(sourceName, context);
        if (source == null) return (null, "", null);

        var endpoint = source.Url.TrimEnd('/') + "/chat/completions";
        var model    = modelId ?? source.Models.FirstOrDefault() ?? "default";
        return (endpoint, model, source.GetApiKey());
    }

}
