using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aria.Agent;
using Aria.Harness.Core;
using Aria.Harness.Context;
using Aria.Harness.Formats;
using Aria.Harness.Models;
using Aria.Harness.Governance;
using Aria.Harness.Tools;
using Aria.Shared;
using Aria.Tools;
using Aria.Web.Data;
using Aria.Web.Services.Chat;
using Aria.Web.Services.ModelBridge;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Aria.Web.Services.AgentServices;

/// <summary>Outcome of <see cref="AgentService.ResolveFormatsAsync"/>: the detected formats plus
/// whether the result is ambiguous enough to ask the human to confirm/refuse it.</summary>
public readonly record struct FormatResolution(
    ThinkingFormat Thinking, ToolCallFormat ToolCall, bool NeedsConfirmation);

/// <summary>
/// Web-facing facade over the shared <see cref="IHarness"/> orchestration layer.
/// Keeps the original public surface so existing Blazor pages and services don't change.
/// Secrets (OAuth tokens and LLM API keys) and channel config live on the user's bridge node, not the
/// server; cloud calls are proxied through the node's /llm/proxy so the server never sees a key.
/// </summary>
public sealed class AgentService
{
    private readonly IConfiguration                       _config;
    private readonly ILogger<AgentService>                _logger;
    private readonly IDbContextFactory<AppDbContext>      _dbFactory;
    private readonly IHarness                             _harness;
    private readonly WebHarnessRuntime                    _runtime;
    private readonly UserLocalSourceService               _localSourceSvc;

    public bool ForceFormatRecheck { get; private set; }

    // Post-mutation verify nudge (Governance:VerifyNudge, default on) — layered onto every
    // governance policy this service builds so the toggle applies to existing sessions too.
    private readonly bool _verifyNudge;

    public AgentService(
        IConfiguration config,
        ILogger<AgentService> logger,
        IDbContextFactory<AppDbContext> dbFactory,
        IHarness harness,
        WebHarnessRuntime runtime,
        UserLocalSourceService localSourceSvc)
    {
        _config         = config;
        _logger         = logger;
        _dbFactory      = dbFactory;
        _harness        = harness;
        _runtime        = runtime;
        _localSourceSvc = localSourceSvc;
        ForceFormatRecheck = config.GetValue<bool>("Debug:ForceFormatRecheck");
        _verifyNudge    = config.GetValue<bool>("Governance:VerifyNudge", true);
    }

    public void SetBridge(ModelBridgeRegistry bridge) => _runtime.SetBridge(bridge);

    // ── Per-user local sources ────────────────────────────────────────────────

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<ModelSource>>
        _userLocalSources = new();

    public void SetUserLocalSources(string userId, IReadOnlyList<ModelSource> sources)
        => _userLocalSources[userId] = sources;

    public IReadOnlyList<ModelSource> GetSourcesForUser(string userId)
    {
        var local = _userLocalSources.TryGetValue(userId, out var ul) ? ul : [];
        return [..local, ..AvailableModelSources];
    }

    public IReadOnlyList<ModelSource> AvailableModelSources => WebHarnessRuntime.PublicProviderCatalog;

    /// <summary>Warms the in-memory local-source cache for a user from the DB if it isn't already loaded.
    /// Needed by background services (e.g. Wargame's turn loop) that have no live Blazor circuit to prime it.</summary>
    public async Task EnsureUserLocalSourcesLoadedAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId) || _userLocalSources.ContainsKey(userId)) return;
        var dbSources = await _localSourceSvc.GetForUserAsync(userId);
        _userLocalSources[userId] = dbSources.Select(UserLocalSourceService.ToModelSource).ToList();
    }

    public string GetModelMode(string? userId = null)
    {
        if (userId != null && _userLocalSources.TryGetValue(userId, out var ul) && ul.Count > 0)
            return "LOCAL LLM";
        return AvailableModelSources.Count > 0 ? "CLOUD" : "OFFLINE";
    }

    public bool HasMcpEnabled =>
        _config.GetSection("MCP").GetChildren().Any(s => s.GetValue("Enabled", true));

    public async Task<bool> CheckMcpBridgeAsync(string userId, CancellationToken ct = default)
    {
        var context = new HarnessContext { UserId = userId, BridgeUserId = userId };
        return await _runtime.IsBridgeAvailableAsync(context, ct);
    }

    // ── Session creation ──────────────────────────────────────────────────────

    public async Task<(AIAgent Agent, AgentSession Session)> CreateSessionAsync(
        List<ActiveToolConfig> enabledTools,
        string? selectedSourceName = null,
        ThinkingFormat thinkingFormat = ThinkingFormat.None,
        Action<string>? onThinkingToken = null,
        IEnumerable<McpServerConfig>? userMcpServers = null,
        string? selectedModel = null,
        Action<string>? onProgress = null,
        string? bridgeUserId = null,
        string? userId = null,
        string? instructionsOverride = null,
        string? agentNameOverride = null,
        Action<string, string>? onToolStart = null,
        Action<string, string, string?, string?, string?>? onToolComplete = null,
        string? bridgeNodeId = null,
        Action<IReadOnlyList<Aria.Tools.TodoItem>>? onTodoUpdate = null,
        GovernanceMode governanceMode = GovernanceMode.Off,
        Func<ActionDescriptor, CancellationToken, Task<bool>>? onApprovalRequested = null,
        string? activeProjectPath = null,
        IReadOnlyList<TerminalProject>? terminalProjects = null,
        string? sessionId = null,
        RecallScope recallScope = RecallScope.AllNodes,
        ISubAgentSpawner? subAgentSpawner = null,
        Func<string, string[]?, CancellationToken, Task<string?>>? onAskUser = null,
        Func<ContextStatusSnapshot>? contextStatusProvider = null,
        bool fleetApprovalRequired = false,
        Func<CancellationToken, Task<string>>? fleetStatusProvider = null,
        IReadOnlyDictionary<string, string>? nodeLabels = null)
    {
        var context = new HarnessContext { UserId = userId, BridgeUserId = bridgeUserId ?? userId, SessionId = sessionId };
        var options = new HarnessOptions
        {
            SelectedSourceName = selectedSourceName,
            SelectedModel      = selectedModel,
            ThinkingFormat     = thinkingFormat,
            EnabledTools       = enabledTools,
            UserMcpServers     = userMcpServers,
            TerminalProjects   = terminalProjects?.Select(p => (p.Name, p.Path, p.Description, p.NodeId, p.Platform)).ToList(),
            InstructionsOverride = instructionsOverride,
            AgentNameOverride  = agentNameOverride,
            BridgeNodeId       = bridgeNodeId,
            OnThinkingToken    = onThinkingToken,
            OnProgress         = onProgress,
            OnToolStart        = onToolStart,
            OnToolComplete     = onToolComplete,
            OnTodoUpdate       = onTodoUpdate,
            Governance         = GovernancePolicy.FromMode(governanceMode) with { ApproveCrossNodeCalls = fleetApprovalRequired, VerifyNudge = _verifyNudge },
            OnApprovalRequested = onApprovalRequested,
            OnAskUser          = onAskUser,
            ContextStatusProvider = contextStatusProvider,
            FleetStatusProvider = fleetStatusProvider,
            NodeLabels         = nodeLabels,
            ActiveProjectPath  = activeProjectPath,
            RecallScope        = recallScope,
            SubAgentSpawner    = subAgentSpawner,
            ChatCapabilitiesText = ChatCatalog.BuildAgentCapabilitiesText()
        };

        // Ensure user local sources are available to the runtime.
        if (!string.IsNullOrEmpty(userId) && !_userLocalSources.ContainsKey(userId))
        {
            var dbSources = await _localSourceSvc.GetForUserAsync(userId);
            _userLocalSources[userId] = dbSources.Select(UserLocalSourceService.ToModelSource).ToList();
        }

        return await _harness.CreateSessionAsync(options, context);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string userMessage,
        AIAgent agent,
        AgentSession session,
        [EnumeratorCancellation] CancellationToken ct = default,
        Action<ChatTokenUsage>? onUsage = null,
        Action<string>? onToolCall = null,
        IReadOnlyList<string>? turnScopePaths = null,
        GovernanceMode? governanceMode = null,
        int? budgetToolCalls = null,
        int? budgetFileReads = null,
        bool fleetApprovalRequired = false)
    {
        var turnPolicy = governanceMode.HasValue
            ? GovernancePolicy.FromMode(governanceMode.Value)
                .WithBudgetOverrides(budgetToolCalls, budgetFileReads) with { ApproveCrossNodeCalls = fleetApprovalRequired, VerifyNudge = _verifyNudge }
            : null;
        var stream = _harness.StreamAsync(userMessage, agent, session, new HarnessContext { CancellationToken = ct }, turnScopePaths, turnPolicy, ct, onUsage);
        await using var enumerator = stream.GetAsyncEnumerator(ct);
        while (true)
        {
            bool moved;
            try { moved = await enumerator.MoveNextAsync(); }
            catch (ContextApprovalRequiredException) { throw; }
            catch (Exception ex) when (IsContextApprovalRefusal(ex, out var sessionId))
            {
                throw new ContextApprovalRequiredException(sessionId, ex.Message);
            }
            if (!moved) break;
            yield return enumerator.Current;
        }
    }

    private static bool IsContextApprovalRefusal(Exception ex, out string? sessionId)
    {
        sessionId = null;
        var msg = ex.Message;
        if (string.IsNullOrEmpty(msg) || !msg.Contains("CONTEXT_APPROVAL_REQUIRED", StringComparison.Ordinal))
            return false;

        // Extract sessionId='...' from the machine-readable prefix.
        var start = msg.IndexOf("sessionId='", StringComparison.Ordinal);
        if (start >= 0)
        {
            start += "sessionId='".Length;
            var end = msg.IndexOf("'", start, StringComparison.Ordinal);
            if (end > start)
                sessionId = msg[start..end];
        }
        return true;
    }

    // ── Format detection ──────────────────────────────────────────────────────

    public async Task<ThinkingFormat> DetectThinkingFormatAsync(
        string? selectedSourceName, string? modelId = null, CancellationToken ct = default, string? userId = null)
    {
        var context = new HarnessContext { UserId = userId, BridgeUserId = userId };
        if (!string.IsNullOrEmpty(userId) && !_userLocalSources.ContainsKey(userId))
        {
            var dbSources = await _localSourceSvc.GetForUserAsync(userId);
            _userLocalSources[userId] = dbSources.Select(UserLocalSourceService.ToModelSource).ToList();
        }
        // Probe on the node the channel is bound to — with multiple bridges the default node may be a
        // different machine whose localhost hosts a different (or no) model server.
        context.BridgeNodeId = ResolveSourceNodeId(userId, selectedSourceName);
        return await _harness.DetectThinkingFormatAsync(selectedSourceName, modelId, context, ct);
    }

    /// <summary>The bridge node a user channel is bound to (null for cloud/unbound channels).</summary>
    public string? ResolveSourceNodeId(string? userId, string? sourceName) =>
        userId != null && sourceName != null
            ? GetSourcesForUser(userId).FirstOrDefault(s => s.Name == sourceName)?.BridgeNodeId
            : null;

    public async Task<ToolCallFormat> DetectToolCallFormatAsync(
        string? selectedSourceName, string? modelId = null, CancellationToken ct = default, string? userId = null)
    {
        var context = new HarnessContext { UserId = userId, BridgeUserId = userId };
        if (!string.IsNullOrEmpty(userId) && !_userLocalSources.ContainsKey(userId))
        {
            var dbSources = await _localSourceSvc.GetForUserAsync(userId);
            _userLocalSources[userId] = dbSources.Select(UserLocalSourceService.ToModelSource).ToList();
        }
        context.BridgeNodeId = ResolveSourceNodeId(userId, selectedSourceName);
        return await _harness.DetectToolCallFormatAsync(selectedSourceName, modelId, context, ct);
    }

    /// <summary>
    /// Resolves the effective context window for a source+model using the precedence order:
    /// channel override → cached provider discovery → well-known cloud catalog → assumed fallback.
    /// </summary>
    public async Task<ContextWindow> ResolveContextWindowAsync(
        string? selectedSourceName, string? modelId = null, string? userId = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(userId) && !_userLocalSources.ContainsKey(userId))
        {
            var dbSources = await _localSourceSvc.GetForUserAsync(userId);
            _userLocalSources[userId] = dbSources.Select(UserLocalSourceService.ToModelSource).ToList();
        }
        var source = ResolveSource(userId, selectedSourceName);
        var context = new HarnessContext { UserId = userId, BridgeUserId = userId };
        context.BridgeNodeId = ResolveSourceNodeId(userId, selectedSourceName);
        return await _harness.ResolveContextWindowAsync(source, modelId, context, ct);
    }

    /// <summary>
    /// Detects thinking + tool-call formats and reports whether the result is ambiguous enough to
    /// warrant a human decision. Ambiguous = the probe reached the model but couldn't CLASSIFY it
    /// (thinking or tool-call == Unknown), AND no human has already confirmed a decision for this
    /// source/model. A "None" verdict is NOT ambiguous — it's a confident result: no separate
    /// thinking channel (a plain model that reasons inline, e.g. granite), and/or native OpenAI
    /// tool_calls that need no client-side parsing. Treating None as ambiguous made every plain
    /// model with native tools falsely trip the "FORMAT NOT RECOGNISED" gate. Cloud/public providers
    /// are known-good and never prompt. Positive detections are cached automatically by the probe.
    /// </summary>
    public async Task<FormatResolution> ResolveFormatsAsync(
        string? selectedSourceName, string? modelId = null, string? userId = null, CancellationToken ct = default)
    {
        var thinking = await DetectThinkingFormatAsync(selectedSourceName, modelId, ct, userId);
        var toolCall = await DetectToolCallFormatAsync(selectedSourceName, modelId, ct, userId);

        var source = ResolveSource(userId, selectedSourceName);
        if (source == null || source.IsPublicProvider)
            return new FormatResolution(thinking, toolCall, false);

        var model        = modelId ?? source.Models.FirstOrDefault() ?? "";
        var confirmed    = await _runtime.FormatCache.IsConfirmedAsync(source.Url, model, ct);
        var inconclusive = thinking == ThinkingFormat.Unknown
                        || toolCall == ToolCallFormat.Unknown;

        return new FormatResolution(thinking, toolCall, inconclusive && !confirmed);
    }

    /// <summary>
    /// Distinguishes "couldn't reach the model server" from "reached it but couldn't classify the
    /// format". Only meaningful for bridged local channels; public/cloud providers are assumed
    /// reachable. Returns <c>Reachable=true</c> whenever it can't prove otherwise.
    /// </summary>
    public async Task<(bool Reachable, string? Detail)> ProbeSourceReachabilityAsync(
        string? selectedSourceName, string? modelId = null, string? userId = null, CancellationToken ct = default)
    {
        var source = ResolveSource(userId, selectedSourceName);
        if (source == null || source.IsPublicProvider || !source.IsBridged || string.IsNullOrEmpty(userId))
            return (true, null);
        var nodeId = ResolveSourceNodeId(userId, selectedSourceName);
        return await _runtime.ProbeReachabilityAsync(userId, source.Url, source.ChannelName ?? source.Name, nodeId, ct);
    }

    /// <summary>Persist a human's acceptance of an ambiguous detection so it is never re-probed.</summary>
    public async Task ConfirmFormatsAsync(
        string? selectedSourceName, string? modelId, ThinkingFormat thinking, ToolCallFormat toolCall,
        string? userId = null, CancellationToken ct = default)
    {
        var source = ResolveSource(userId, selectedSourceName);
        if (source == null) return;
        var model = modelId ?? source.Models.FirstOrDefault() ?? "";
        await _runtime.FormatCache.ConfirmFormatsAsync(source.Url, model, thinking, toolCall, ct);
    }

    /// <summary>Forget every cached/confirmed format for a channel (all its models) so it re-probes.</summary>
    public async Task ClearChannelFormatsAsync(string? selectedSourceName, string? userId = null, CancellationToken ct = default)
    {
        var source = ResolveSource(userId, selectedSourceName);
        if (source == null) return;
        // Cover every key the detection paths might use: each configured model plus the "" / "default"
        // fallbacks used when no explicit model is selected.
        var models = source.Models.Concat(["", "default"]).Distinct();
        foreach (var m in models)
            await _runtime.FormatCache.ClearAsync(source.Url, m, ct);
    }

    private ModelSource? ResolveSource(string? userId, string? sourceName) =>
        userId != null && sourceName != null
            ? GetSourcesForUser(userId).FirstOrDefault(s => s.Name == sourceName)
            : null;

    public async Task<(ThinkingFormat Thinking, ToolCallFormat ToolCall)> ForceRedetectAsync(
        string sourceName, string modelId, CancellationToken ct = default, string? userId = null)
    {
        var context = new HarnessContext { UserId = userId, BridgeUserId = userId };
        if (!string.IsNullOrEmpty(userId) && !_userLocalSources.ContainsKey(userId))
        {
            var dbSources = await _localSourceSvc.GetForUserAsync(userId);
            _userLocalSources[userId] = dbSources.Select(UserLocalSourceService.ToModelSource).ToList();
        }
        return await _harness.ForceRedetectAsync(sourceName, modelId, context, ct);
    }

    /// <summary>Drops every persisted model-format detection; all channels re-probe on next session.</summary>
    public async Task<int> PurgeAllModelFormatsAsync() =>
        _runtime.FormatCache is Services.Llm.WebFormatCache web ? await web.PurgeAllAsync() : 0;

    public void EvictFormatCache(string sourceName, string modelId)
    {
        // Best-effort eviction via the runtime cache.
        _ = Task.Run(async () =>
        {
            var source = _runtime.FindSource(sourceName, new HarnessContext());
            if (source == null) return;
            await _runtime.FormatCache.SetThinkingFormatAsync(source.Url, modelId, ThinkingFormat.Unknown);
            await _runtime.FormatCache.SetToolCallFormatAsync(source.Url, modelId, ToolCallFormat.Unknown);
        });
    }

    // ── Connectivity ──────────────────────────────────────────────────────────

    public async Task<(bool Reachable, string Url)> CheckConnectivityAsync(
        string? sourceName, string? userId = null, CancellationToken ct = default)
    {
        var context = new HarnessContext { UserId = userId, BridgeUserId = userId };
        var source  = _runtime.FindSource(sourceName, context);
        if (source == null) return (false, "");

        if (source.IsPublicProvider || source.IsBridged) return (true, source.Url);

        var healthUrl = source.Url.TrimEnd('/') + "/models";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            using var http = new HttpClient();
            var apiKey = source.GetApiKey();
            if (!string.IsNullOrEmpty(apiKey))
                http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            using var resp = await http.GetAsync(healthUrl, linked.Token);
            return (resp.IsSuccessStatusCode || (int)resp.StatusCode < 500, healthUrl);
        }
        catch
        {
            return (false, healthUrl);
        }
    }

    // API keys are authored and stored ONLY on the bridge node (bridge status page). The server has
    // no read/write path to them: cloud calls are proxied through the node's /llm/proxy.

    // ── Transcript fixing ─────────────────────────────────────────────────────

    public async Task<string> FixTranscriptAsync(string rawText, string channelName, string userId)
    {
        var context = new HarnessContext { UserId = userId, BridgeUserId = userId };
        var source  = _runtime.FindSource(channelName, context);
        if (source == null) return rawText;

        // The server never handles the cloud key. Bridged/public sources are proxied through the
        // node's /llm/proxy, which injects the locally-stored key; only genuinely direct local
        // sources call out from the server, and those need no user key.
        var routeViaBridge = source.IsBridged || source.IsPublicProvider;
        DelegatingHandler? handler = routeViaBridge
            ? new UniversalReasoningHandler
              {
                  InnerHandler = new BridgeHttpHandler(
                      _runtime, context,
                      keyRef: source.ChannelName ?? source.Name,
                      requireKey: source.IsPublicProvider,
                      nodeId: source.BridgeNodeId)
              }
            : null;

        var modelId = source.Models.FirstOrDefault() ?? "default";
        var client  = ChatClientFactory.Build(source, modelId, handler);

        const string systemPrompt =
            "You rewrite raw voice transcripts. Output ONLY the corrected transcript text and nothing " +
            "else — never acknowledge, explain, or add quotes.";

        // Small/local models tend to treat a lone system rule + a short user turn as a chat prompt
        // (\"I'm ready to receive the transcript…\"). Folding the task and the text into one user turn
        // that ends on a completion cue makes them continue with just the answer.
        var userPrompt =
            "Rewrite the voice transcript below: fix punctuation, capitalisation, obvious mishearings, " +
            "and remove filler words (um, uh, like). Preserve the meaning exactly. " +
            "Reply with only the rewritten transcript — no preamble, no quotes, no notes.\n\n" +
            $"Transcript:\n{rawText}\n\nRewritten transcript:";

        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        try
        {
            // Bridged sources only expose a streaming SSE response through the tunnel, so accumulate
            // the stream rather than calling the non-streaming completion API.
            var sb = new StringBuilder();
            await foreach (var update in client.CompleteChatStreamingAsync(messages))
                foreach (var part in update.ContentUpdate)
                    sb.Append(part.Text);

            var fixedText = CleanFixedTranscript(sb.ToString(), rawText);
            return string.IsNullOrEmpty(fixedText) ? rawText : fixedText;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vox transcript fix failed for channel {Channel}", channelName);
            return rawText;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex ThinkTagRegex = new(
        @"<(think|thought|thinking|reasoning|reflection)>.*?</\1>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

    // A paragraph is meta-commentary about the task (a reasoning model's leaked chain-of-thought)
    // if it talks *about* transcribing/rewriting. Real dictated speech rarely contains these words,
    // so this is a safe signal to drop such a leading block.
    private static readonly System.Text.RegularExpressions.Regex MetaParagraphRegex = new(
        @"\b(transcript|filler|rewrite|rewritten|punctuation|capitali[sz]|as-is|as is|no fillers|already (clean|correct|fine)|return it|the user (wants|said|asked)|no changes|nothing to (fix|correct|change))\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Safety net for reasoning/chatty models: strip <think> blocks, leaked chain-of-thought
    // paragraphs, an echoed cue/acknowledgement, and wrapping quotes. Conservative — falls back to
    // the full output (or raw text) if stripping would leave nothing.
    private static string CleanFixedTranscript(string modelOutput, string rawText)
    {
        var text = ThinkTagRegex.Replace(modelOutput, "").Trim();
        if (text.Length == 0) return rawText;

        // Drop leading paragraphs that are the model reasoning about the task (tagless CoT), keeping
        // everything from the first real (non-meta) paragraph onward so multi-line answers survive.
        var paras = System.Text.RegularExpressions.Regex.Split(text, @"\n\s*\n");
        int start = 0;
        while (start < paras.Length - 1 && MetaParagraphRegex.IsMatch(paras[start]))
            start++;
        if (start > 0)
            text = string.Join("\n\n", paras[start..]).Trim();

        // Drop an echoed "Rewritten transcript:" / "Corrected:" label.
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^\s*(rewritten transcript|corrected( transcript)?|output)\s*:\s*",
            "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        // Drop a leading conversational acknowledgement line, only when content follows it.
        var ackLine = new System.Text.RegularExpressions.Regex(
            @"^\s*(sure|okay|ok|understood|got it|here('?s| is)[^\n]*|i('?m| am) ready[^\n]*)[:.]?\s*\n+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var stripped = ackLine.Replace(text, "");
        if (stripped.Trim().Length > 0) text = stripped.Trim();

        // Remove a single pair of wrapping quotes.
        if (text.Length >= 2 &&
            ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
            text = text[1..^1].Trim();

        return text.Length == 0 ? rawText : text;
    }
}
