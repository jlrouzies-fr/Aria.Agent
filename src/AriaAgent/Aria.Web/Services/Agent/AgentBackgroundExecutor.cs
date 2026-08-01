using System.Text;
using System.Text.Json;
using Aria.Harness.Governance;
using Aria.Harness.Tools;
using Aria.Tools;
using Aria.Web.Data;
using Aria.Web.Helpers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aria.Web.Services.Agent;

/// <summary>
/// Runs an agent session headlessly — no live Blazor circuit required.
/// Bridge-dependent tools (terminal, wargame) are excluded unless the run carries an explicit
/// authorisation (an opted-in vigil/Hive collective, or an interactive session's spawned child);
/// cloud and SSE-MCP tools work normally.
/// </summary>
public class AgentBackgroundExecutor(
    AgentService             agentService,
    CronSlotService          cronService,
    IServiceScopeFactory     scopeFactory,
    ModelBridgeRegistry      bridgeRegistry,
    BridgeCogitationClient   bridgeCogitation,
    ILogger<AgentBackgroundExecutor> logger) : IHeadlessAgentRunner
{
    public static readonly HashSet<string> NoBridgeTools = ["terminal", "wargame"];

    // Ambient context id for a headless run that fans out into many sub-calls. The Hive orchestrator sets
    // it once for a whole collective run so every Overmind/drone call inherits `hive:{id}` — letting one
    // pre-authorised Layer B grant cover the fan-out without threading the id through a dozen call sites.
    // AsyncLocal isolates concurrent runs (each collective run is its own async flow); an explicit
    // sessionId argument always wins over the ambient value.
    private static readonly AsyncLocal<string?> _ambientSessionId = new();

    // Ambient bridge-tools authorisation for a headless fan-out. Same rationale as _ambientSessionId:
    // the Hive orchestrator sets it once from the collective's AllowProjectTools flag so every
    // Overmind/drone run in the flow keeps terminal/file tools instead of having them stripped,
    // without threading a flag through a dozen call sites. An explicit allowBridgeTools argument
    // always wins (OR-ed).
    private static readonly AsyncLocal<bool?> _ambientAllowBridgeTools = new();

    /// <summary>Sets the ambient headless session id for the returned scope; restores the prior value on
    /// dispose. Headless runs started within the scope that don't pass their own sessionId inherit it.</summary>
    public static IDisposable WithAmbientSession(string? sessionId)
    {
        var prev = _ambientSessionId.Value;
        _ambientSessionId.Value = sessionId;
        return new SessionScope(prev);
    }

    private sealed class SessionScope(string? prev) : IDisposable
    {
        public void Dispose() => _ambientSessionId.Value = prev;
    }

    /// <summary>Sets the ambient bridge-tools authorisation for the returned scope; restores the prior
    /// value on dispose. Headless runs started within the scope keep bridge/terminal tools when
    /// <paramref name="allowed"/> is true (the default — null/false — strips them as before).</summary>
    public static IDisposable WithAmbientBridgeTools(bool allowed)
    {
        var prev = _ambientAllowBridgeTools.Value;
        _ambientAllowBridgeTools.Value = allowed;
        return new BridgeToolsScope(prev);
    }

    /// <summary>Whether the current async flow carries an ambient bridge-tools authorisation.
    /// Internal for unit tests.</summary>
    internal static bool AmbientBridgeToolsAllowed => _ambientAllowBridgeTools.Value == true;

    private sealed class BridgeToolsScope(bool? prev) : IDisposable
    {
        public void Dispose() => _ambientAllowBridgeTools.Value = prev;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Core headless runner — shared by cron executor AND collective orchestrator
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a single agent call headlessly and returns the full concatenated text.
    /// Creates its own async scope; safe to call from singleton services.
    /// </summary>
    public async Task<string> RunHeadlessAsync(
        string userId,
        int? subAgentId,
        string prompt,
        string? sourceName,
        string? modelId,
        IReadOnlyList<ChatMessage>? seedHistory = null,
        string? instructionsPrefix = null,
        string? sessionId = null,
        bool allowBridgeTools = false,
        GovernanceMode? governanceMode = null,
        CancellationToken ct = default)
    {
        var (text, _) = await RunHeadlessCoreAsync(
            userId, subAgentId, prompt, sourceName, modelId, seedHistory, instructionsPrefix, sessionId,
            allowBridgeTools, governanceMode, ct);
        return text;
    }

    /// <summary>Same as <see cref="RunHeadlessAsync"/> but also returns any captured reasoning/thinking
    /// text, so callers that persist the reply as a chat message can store it split from the content
    /// (see <see cref="Services.CollectiveOrchestrator.CollectiveOrchestrator"/>'s Hive message writes).</summary>
    public Task<(string Text, string? Thinking)> RunHeadlessWithThinkingAsync(
        string userId,
        int? subAgentId,
        string prompt,
        string? sourceName,
        string? modelId,
        IReadOnlyList<ChatMessage>? seedHistory = null,
        string? instructionsPrefix = null,
        string? sessionId = null,
        bool allowBridgeTools = false,
        GovernanceMode? governanceMode = null,
        CancellationToken ct = default)
        => RunHeadlessCoreAsync(userId, subAgentId, prompt, sourceName, modelId, seedHistory, instructionsPrefix, sessionId,
            allowBridgeTools, governanceMode, ct);

    /// <summary><see cref="IHeadlessAgentRunner"/> entry point for delegated child runs: a headless
    /// persona run under the parent's session grant and governance mode.</summary>
    public Task<string> SpawnChildRunAsync(
        string userId,
        int subAgentId,
        string prompt,
        string? sessionId,
        bool allowBridgeTools,
        GovernanceMode governanceMode,
        CancellationToken ct = default)
        => RunHeadlessAsync(userId, subAgentId, prompt, sourceName: null, modelId: null,
            sessionId: sessionId, allowBridgeTools: allowBridgeTools, governanceMode: governanceMode, ct: ct);

    private async Task<(string Text, string? Thinking)> RunHeadlessCoreAsync(
        string userId,
        int? subAgentId,
        string prompt,
        string? sourceName,
        string? modelId,
        IReadOnlyList<ChatMessage>? seedHistory,
        string? instructionsPrefix,
        string? sessionId,
        bool allowBridgeTools,
        GovernanceMode? governanceMode,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var toolService        = scope.ServiceProvider.GetRequiredService<UserToolService>();
        var mcpClient          = scope.ServiceProvider.GetRequiredService<BridgeMcpClient>();
        var localSourceService = scope.ServiceProvider.GetRequiredService<UserLocalSourceService>();
        var subAgentService    = scope.ServiceProvider.GetRequiredService<SubAgentService>();
        var skillService       = scope.ServiceProvider.GetRequiredService<SkillService>();

        // Load per-user local LLM sources into AgentService cache
        var dbSources    = await localSourceService.GetForUserAsync(userId);
        var modelSources = dbSources.Select(UserLocalSourceService.ToModelSource).ToList();
        agentService.SetUserLocalSources(userId, modelSources);

        // Bridge/terminal tools survive the headless filter only for explicitly-authorised runs
        // (an opted-in vigil/Hive via the ambient flag, or an interactive session's spawned child).
        var allowBridge = allowBridgeTools || _ambientAllowBridgeTools.Value == true;

        // Build tool list — user tools for base Aria, or sub-agent tools for a drone
        List<ActiveToolConfig> enabledTools;
        string? instructionsOverride = null;
        string? agentNameOverride    = null;
        SubAgent? subAgent           = null;

        if (subAgentId.HasValue)
        {
            enabledTools = await subAgentService.GetEnabledToolConfigsAsync(subAgentId.Value, userId, allowBridge);

            // Build persona for the sub-agent
            subAgent = await subAgentService.GetByIdAsync(subAgentId.Value);
            if (subAgent != null)
            {
                var skills      = await skillService.GetForAgentAsync(subAgent.Id);
                var skillTuples = skills.Select(s => (s.Name, s.MarkdownContent));
                instructionsOverride = AgentPersona.BuildSystemPrompt(
                    subAgent.GeneratedName,
                    subAgent.GeneratedPersonality,
                    subAgent.UserDirectives,
                    skillTuples);
                agentNameOverride = subAgent.GeneratedName;

                // If sub-agent has its own model, prefer it unless caller explicitly overrides
                sourceName ??= subAgent.ModelSourceName;
                modelId    ??= subAgent.ModelId;
            }
        }
        else
        {
            enabledTools = await BuildUserToolListAsync(toolService, userId, allowBridge);
        }

        // Callers (e.g. the Hive orchestrator) can prepend role-specific framing — like the Overmind's
        // "you have drones at your service" briefing — ahead of the sub-agent's own persona charter.
        if (!string.IsNullOrEmpty(instructionsPrefix))
            instructionsOverride = string.IsNullOrEmpty(instructionsOverride)
                ? instructionsPrefix
                : instructionsPrefix + "\n\n" + instructionsOverride;

        var enabledMcpNames = subAgent?.EnabledMcpNamesJson is { Length: > 0 } json
            ? JsonSerializer.Deserialize<List<string>>(json) ?? []
            : [];
        var mcpServers = await mcpClient.GetConfigsForNamesAsync(userId, enabledMcpNames);

        var format     = await agentService.DetectThinkingFormatAsync(sourceName, modelId, ct, userId);
        var thinkingSb = new StringBuilder();

        var terminalProjects = await GetTerminalProjectsAsync(scope, userId, enabledTools);

        var (agent, session) = await agentService.CreateSessionAsync(
            enabledTools,
            selectedSourceName:   sourceName,
            thinkingFormat:       format,
            onThinkingToken:      t => thinkingSb.Append(t),
            selectedModel:        modelId,
            userId:               userId,
            userMcpServers:       mcpServers,
            instructionsOverride: instructionsOverride,
            agentNameOverride:    agentNameOverride,
            terminalProjects:     terminalProjects,
            // An explicit sessionId wins; otherwise inherit the ambient one (e.g. a Hive run's hive:{id}).
            sessionId:            sessionId ?? _ambientSessionId.Value,
            // A spawned child inherits its parent's governance mode (fresh per-session counters/budgets);
            // other headless callers leave this null and run ungoverned as before.
            governanceMode:       governanceMode ?? GovernanceMode.Off);

        if (seedHistory is { Count: > 0 })
            session.SetInMemoryChatHistory(seedHistory.ToList());

        var sb = new StringBuilder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        // Same turn-checkpoint stamping as interactive cogitations — spawned children / headless
        // runs get their own FileUndo checkpoint so /rewind can name them explicitly later.
        var previousCheckpoint = Aria.Harness.Core.HarnessContext.CurrentTurnCheckpoint;
        Aria.Harness.Core.HarnessContext.CurrentTurnCheckpoint = Guid.NewGuid().ToString("N");
        try
        {
            await foreach (var chunk in agentService.StreamAsync(prompt, agent, session, linked.Token))
                sb.Append(chunk);
        }
        finally
        {
            Aria.Harness.Core.HarnessContext.CurrentTurnCheckpoint = previousCheckpoint;
        }

        return (sb.ToString(), thinkingSb.Length > 0 ? thinkingSb.ToString() : null);
    }

    // Returns (text, estimatedTokens, thinking)
    public async Task<(string Text, int EstimatedTokens, string? Thinking)> RunHeadlessWithMetricsAsync(
        string userId,
        int? subAgentId,
        string prompt,
        string? sourceName,
        string? modelId,
        IReadOnlyList<ChatMessage>? seedHistory = null,
        string? instructionsPrefix = null,
        string? sessionId = null,
        bool allowBridgeTools = false,
        GovernanceMode? governanceMode = null,
        CancellationToken ct = default)
    {
        var (text, thinking) = await RunHeadlessCoreAsync(
            userId, subAgentId, prompt, sourceName, modelId, seedHistory, instructionsPrefix, sessionId,
            allowBridgeTools, governanceMode, ct);
        var tokens = (prompt.Length + text.Length) / 4;   // rough estimate: ~4 chars per token
        return (text, tokens, thinking);
    }


    // ─────────────────────────────────────────────────────────────────────────
    // Cron job executor (thin wrapper over manual session + cogitation persist)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Returns the user's currently-connected default bridge node, or null if none.</summary>
    private string? GetDefaultBridgeNodeId(string userId)
    {
        var node = bridgeRegistry.GetNodes(userId).FirstOrDefault();
        return node?.NodeId;
    }

    /// <summary>
    /// Loads the message history for a vigil continuation. Legacy/server-stored cogitations read from
    /// the server DB; bridge-owned cogitations read from the bridge node that owns the content.
    /// </summary>
    private async Task<List<ChatMessage>> LoadHistoryAsync(string userId, Cogitation cogitation, CogitationService cogitationService)
    {
        if (cogitation.OriginNodeId == null)
        {
            var msgs = await cogitationService.GetMessagesAsync(cogitation.Id);
            return msgs
                .Select(m => m.Role == "user"
                    ? new ChatMessage(ChatRole.User, m.Content)
                    : new ChatMessage(ChatRole.Assistant, m.Content))
                .ToList();
        }

        var bridgeMsgs = await bridgeCogitation.GetMessagesAsync(userId, cogitation.Id, cogitation.OriginNodeId);
        return bridgeMsgs
            .Select(m => m.Role == "user"
                ? new ChatMessage(ChatRole.User, m.Content)
                : new ChatMessage(ChatRole.Assistant, m.Content))
            .ToList();
    }

    public async Task ExecuteJobAsync(AgentCronJob job, CancellationToken ct)
    {
        await cronService.MarkRunningAsync(job.Id);
        logger.LogInformation("Background job {JobId} starting for user {UserId}", job.Id, job.UserId);

        try
        {
            await using var scope          = scopeFactory.CreateAsyncScope();
            var toolService                = scope.ServiceProvider.GetRequiredService<UserToolService>();
            var mcpClient                  = scope.ServiceProvider.GetRequiredService<BridgeMcpClient>();
            var cogitationService          = scope.ServiceProvider.GetRequiredService<CogitationService>();
            var localSourceService         = scope.ServiceProvider.GetRequiredService<UserLocalSourceService>();

            // Vigils require a connected bridge both for the LLM call and for storing the transcript.
            // Honor the node selected at booking time; fall back to the user's default connected node.
            var originNodeId = !string.IsNullOrEmpty(job.BridgeNodeId)
                ? (bridgeRegistry.GetNodes(job.UserId).Any(n => n.NodeId == job.BridgeNodeId) ? job.BridgeNodeId : null)
                : GetDefaultBridgeNodeId(job.UserId);

            if (originNodeId == null)
            {
                var reason = !string.IsNullOrEmpty(job.BridgeNodeId)
                    ? $"Selected bridge node '{job.BridgeNodeId}' is offline; vigil cannot run."
                    : "No bridge connected for user; vigil cannot run.";
                throw new InvalidOperationException(reason);
            }

            // Load per-user local LLM sources into AgentService cache — no Blazor session is active.
            var dbSources    = await localSourceService.GetForUserAsync(job.UserId);
            var modelSources = dbSources.Select(UserLocalSourceService.ToModelSource).ToList();
            agentService.SetUserLocalSources(job.UserId, modelSources);
            logger.LogInformation("Background job {JobId} — loaded {Count} local source(s) for user {UserId}: {Names}",
                job.Id, modelSources.Count, job.UserId,
                string.Join(", ", modelSources.Select(s => s.Name)));

            // Bridge/terminal tools are stripped for vigils by default; a vigil booked with
            // "allow project tools" keeps them, acting under the slot's pre-authorised grant.
            var enabledTools = await BuildUserToolListAsync(toolService, job.UserId, job.AllowProjectTools);

            var mcpInfos   = await mcpClient.GetMcpInfosAsync(job.UserId);
            var mcpServers = mcpInfos
                .Where(i => i.Enabled)
                .Select(BridgeMcpClient.ToConfig)
                .ToList<McpServerConfig>();

            var terminalProjects = await GetTerminalProjectsAsync(scope, job.UserId, enabledTools);

            // Stamp the vigil's own context id on the session. Every sensitive tool call this run makes
            // carries `vigil:{jobId}` as its session, so the node's Layer B gate matches the grant that
            // was pre-authorised for this vigil at booking time — letting it act unattended, but only
            // under a seal the human granted for exactly this job and window. See PreauthorizeVigilAsync.
            var (agent, session) = await agentService.CreateSessionAsync(
                enabledTools,
                selectedSourceName: job.SourceName,
                selectedModel:      job.ModelId,
                userId:             job.UserId,
                userMcpServers:     mcpServers,
                terminalProjects:   terminalProjects,
                sessionId:          $"vigil:{job.Id}");

            // Seed session with existing cogitation history if continuing one.
            Cogitation cogitation;
            if (job.TargetCogitationId.HasValue)
            {
                var existing = await cogitationService.GetAsync(job.TargetCogitationId.Value);
                cogitation = existing
                    ?? await cogitationService.CreateAsync(job.UserId, job.SubAgentId, originNodeId: originNodeId);

                var history = await LoadHistoryAsync(job.UserId, cogitation, cogitationService);
                if (history.Count > 0)
                    session.SetInMemoryChatHistory(history);

                logger.LogInformation("Background job {JobId} continuing cogitation {CogId} ({Count} prior messages)",
                    job.Id, cogitation.Id, history.Count);
            }
            else
            {
                cogitation = await cogitationService.CreateAsync(job.UserId, job.SubAgentId, originNodeId: originNodeId);
                var shortTitle = job.TaskPrompt.Length > 60 ? job.TaskPrompt[..60] + "…" : job.TaskPrompt;
                await cogitationService.SetTitleAsync(cogitation.Id, $"[VIGIL] {shortTitle}");
            }

            // Ensure the bridge record exists and persist the vigil prompt there.
            var bridgeOk = await bridgeCogitation.EnsureCogitationAsync(
                job.UserId, job.UserId, cogitation.Id,
                ariaAvatarKey: cogitation.AriaAvatarKey,
                subAgentId: job.SubAgentId?.ToString(),
                originNodeId: originNodeId);
            if (!bridgeOk)
                throw new InvalidOperationException("Failed to ensure bridge cogitation for vigil.");

            var userMsgOk = await bridgeCogitation.AddMessageAsync(
                job.UserId, cogitation.Id, "user", job.TaskPrompt, originNodeId: originNodeId);
            if (!userMsgOk)
                throw new InvalidOperationException("Failed to persist vigil prompt to bridge.");
            await cogitationService.TouchAsync(cogitation.Id);

            var sb = new StringBuilder();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            var previousCheckpoint = Aria.Harness.Core.HarnessContext.CurrentTurnCheckpoint;
            Aria.Harness.Core.HarnessContext.CurrentTurnCheckpoint = Guid.NewGuid().ToString("N");
            try
            {
                await foreach (var chunk in agentService.StreamAsync(job.TaskPrompt, agent, session, linked.Token))
                    sb.Append(chunk);
            }
            finally
            {
                Aria.Harness.Core.HarnessContext.CurrentTurnCheckpoint = previousCheckpoint;
            }

            var response = sb.ToString();
            var assistantMsgOk = await bridgeCogitation.AddMessageAsync(
                job.UserId, cogitation.Id, "assistant", response, originNodeId: originNodeId);
            if (!assistantMsgOk)
                throw new InvalidOperationException("Failed to persist vigil response to bridge.");
            await cogitationService.TouchAsync(cogitation.Id);

            var summary = response.Length > 300 ? response[..300] + "…" : response;
            await cronService.MarkCompletedAsync(job.Id, cogitation.Id, summary);

            logger.LogInformation("Background job {JobId} completed (cogitation {CogId})", job.Id, cogitation.Id);
        }
        catch (Aria.Shared.ContextApprovalRequiredException)
        {
            // The vigil hit the Layer B gate with no live pre-authorisation for its slot — no human is
            // present to approve mid-run, so it can't proceed. This happens when pre-auth was refused,
            // the grant window lapsed (e.g. an overdue run recovered long after its slot), or the vigil
            // predates pre-authorisation. Surface it plainly rather than as a raw exception.
            logger.LogWarning("Background job {JobId} blocked by Layer B — no live vigil grant for its slot", job.Id);
            await cronService.MarkFailedAsync(job.Id,
                "Blocked by the node seal (Layer B): this vigil had no live pre-authorisation for its slot. " +
                "Re-book it to pre-authorise, or disable enforcement on the executing node.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background job {JobId} failed", job.Id);
            await cronService.MarkFailedAsync(job.Id, ex.Message);
        }
    }

    private static async Task<List<ActiveToolConfig>> BuildUserToolListAsync(
        UserToolService toolService, string userId, bool allowBridgeTools = false)
    {
        var states = await toolService.GetToolStatesAsync(userId);
        return BuildToolList(states, userId, allowBridgeTools);
    }

    /// <summary>Pure headless tool-list filter: keeps the user's enabled tools, stamps the user id into
    /// each config, and strips bridge-dependent tools (<see cref="NoBridgeTools"/>) unless the run was
    /// explicitly authorised for project tools. Internal for unit tests.</summary>
    internal static List<ActiveToolConfig> BuildToolList(
        Dictionary<string, (bool Enabled, Dictionary<string, string> Config)> states,
        string userId,
        bool allowBridgeTools)
    {
        return states
            .Where(kv => kv.Value.Enabled && (allowBridgeTools || !NoBridgeTools.Contains(kv.Key)))
            .Select(kv =>
            {
                var cfg = new Dictionary<string, string>(kv.Value.Config);
                cfg["_userId"] = userId.ToString();
                return new ActiveToolConfig(kv.Key, cfg);
            })
            .ToList();
    }

    /// <summary>Fetches bridge-authoritative Terminal projects when the Terminal tool is enabled.
    /// Returns null otherwise, so the Harness falls back to legacy config parsing.</summary>
    private static async Task<IReadOnlyList<TerminalProject>?> GetTerminalProjectsAsync(
        AsyncServiceScope scope, string userId, List<ActiveToolConfig> enabledTools)
    {
        if (!enabledTools.Any(t => t.ToolId.Equals("terminal", StringComparison.OrdinalIgnoreCase)))
            return null;

        try
        {
            var terminalClient = scope.ServiceProvider.GetRequiredService<TerminalClient>();
            return await terminalClient.GetProjectsAsync(userId);
        }
        catch
        {
            return null;
        }
    }
}
