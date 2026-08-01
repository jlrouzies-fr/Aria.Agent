using Aria.Harness.Context;
using Aria.Harness.Formats;
using Aria.Web.Data;
using Aria.Web.Helpers;
using Aria.Web.Services.Chat;
using Aria.Web.Services;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Components;

namespace Aria.Web.Components.Pages;

public partial class Chat
{
    // Fleet wiring shared by every session-creation call below: the fleet_status snapshot provider
    // (user resolved lazily — the tool can fire long after creation) and node labels for the
    // cross-node governance approval reason ("run on WINDOWS-RTX2" instead of a key thumbprint).
    private Task<string> FleetStatusSnapshot(CancellationToken ct) =>
        Fleet.GetStatusJsonAsync(BridgeUserId() ?? "", ct);

    private IReadOnlyDictionary<string, string> FleetNodeLabels()
    {
        var userId = BridgeUserId();
        if (userId == null) return new Dictionary<string, string>();
        return BridgeRegistry.GetNodes(userId)
            .Where(n => !string.IsNullOrWhiteSpace(n.Label))
            .ToDictionary(n => n.NodeId, n => n.Label);
    }

    // Cheap new-chat path: keep the already-built agent (tools/MCP/persona intact) and just
    // spin a fresh thread. No connectivity check, no format probe, no bridge tool reload — so
    // none of the init bar flashes. Falls back to a full InitAgentAsync if the reuse faults.
    // `router` is the SAME instance the reused agent's tools were built with (TryReuseAgentForNewChat
    // only allows this path when that cogitation's background run — the only other thing that could
    // be retargeting it — is no longer active), so it's safe to point back at this component.
    /// <summary>Applies the focused dossier's defaults (sub-agent, project, standing directive)
    /// before a new chat session is built. Safe to call repeatedly; only mutates state when a folder
    /// is focused and the corresponding default is set.</summary>
    private async Task ApplyFocusedFolderDefaultsAsync()
    {
        if (SessionState.CurrentUser == null) return;
        var folderId = SessionState.FocusedFolderId;
        if (folderId == null) return;

        var folder = await FolderService.GetByIdAsync(folderId.Value);
        if (folder == null)
        {
            SessionState.FocusFolder(null);
            return;
        }

        if (folder.DefaultSubAgentId is { } agentId)
        {
            var agent = SessionState.ActiveSubAgent?.Id == agentId
                ? SessionState.ActiveSubAgent
                : await SubAgentService.GetByIdAsync(agentId);
            if (agent != null)
                SessionState.ActiveSubAgent = agent;
        }

        if (!string.IsNullOrWhiteSpace(folder.DefaultProjectPath))
        {
            var project = SessionState.Projects.FirstOrDefault(p => p.Path == folder.DefaultProjectPath);
            if (project != null)
            {
                SessionState.ActiveProject = project;
                _explorerProjectFromFolder = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(folder.StandingDirective))
            _standingDirective = folder.StandingDirective.Trim();
    }

    private async Task StartFreshSessionAsync(AIAgent agent, CogitationStreamRouter router)
    {
        try
        {
            var subAgent   = SessionState.ActiveSubAgent;
            var sourceName = subAgent?.ModelSourceName ?? SessionState.SelectedModelSource;
            var modelId    = subAgent?.ModelId         ?? SessionState.SelectedModel;

            if (string.IsNullOrEmpty(_ariaAvatarKey))
                _ariaAvatarKey = PickAriaAvatar();

            _agent          = agent;
            _router         = router;
            _router.Target  = this;
            _session        = await agent.CreateSessionAsync();
            _agentSource    = sourceName;
            _agentModel     = modelId;
            _lastSubAgentId = subAgent?.Id;
            _isInitializing = false;
            _initLogExpanded = false;

            var sessionLabel = subAgent != null
                ? $"// SESSION ESTABLISHED — AGENT: {subAgent.GeneratedName.ToUpperInvariant()} //"
                : $"// SESSION ESTABLISHED — SOUL: {SessionState.CurrentUser!.Name.ToUpperInvariant()} //";
            _messages.Add(new MessageEntry("system", sessionLabel));
            StateHasChanged();

            _greetingCts?.Cancel();
            _greetingCts?.Dispose();
            _greetingCts = new CancellationTokenSource();
            _ = SendGreetingAsync(_greetingCts.Token);
        }
        catch
        {
            // Reuse faulted — rebuild from scratch.
            await InitAgentAsync();
        }
    }

    private async Task InitAgentAsync()
    {
        try
        {
            _initError        = null;
            _cogitationId     = null;
            _cogitationTitled = false;
            _historyLoaded    = false;
            _historyInjected  = false;
            _isInitializing   = true;
            _initLogExpanded  = true;   // auto-open the probing pop-up so the user sees format detection progress
            _agentSource      = null;
            _agentModel       = null;
            _activeCogAgent   = null;   // fresh chat — not a reopened cogitation
            _initLog.Clear();
            if (string.IsNullOrEmpty(_ariaAvatarKey))
                _ariaAvatarKey = PickAriaAvatar();

            var subAgent   = SessionState.ActiveSubAgent;
            var sourceName = subAgent?.ModelSourceName ?? SessionState.SelectedModelSource;
            var modelId    = subAgent?.ModelId         ?? SessionState.SelectedModel;

            _initLog.Add($"// CHANNEL: {sourceName ?? "UNKNOWN"}");
            _initLog.Add($"// MODEL:   {modelId ?? "DEFAULT"}");
            if (subAgent != null)
                _initLog.Add($"// AGENT:   {subAgent.GeneratedName.ToUpperInvariant()} [{subAgent.ArchetypeName}]");
            StateHasChanged();

            if (!await EnsureChannelReadyAsync(sourceName)) return;

            var userId          = SessionState.CurrentUser?.Id;
            var isBridgedSource = (userId != null
                ? AgentService.GetSourcesForUser(userId)
                : [])
                .FirstOrDefault(s => s.Name == sourceName)?.IsBridged == true;
            _initLog.Add(isBridgedSource
                ? "// PROBE:   Awaiting bridge format report <em>(waiting model reply)</em> ..."
                : "// PROBE:   Analysing reasoning format <em>(waiting model reply)</em> ...");
            StateHasChanged();

            var resolution = await AgentService.ResolveFormatsAsync(sourceName, modelId, userId: userId);
            var format     = resolution.Thinking;

            _initLog.Add(isBridgedSource
                ? $"// FORMAT:  {format} (bridge-reported)"
                : $"// FORMAT:  {format}");

            // Ambiguous probe (no recognizable thinking / unconfirmed tools) → let the human decide
            // once whether to lock it in, instead of silently guessing or re-probing every session.
            if (resolution.NeedsConfirmation && sourceName != null)
            {
                _initLog.Add("// PROBE:   Format unrecognised — awaiting your decision...");
                StateHasChanged();

                var confirmed = _formatModal != null && await _formatModal.RequestConfirmationAsync(
                    new ChatFormatModal.FormatDetectPrompt(sourceName, modelId, resolution.Thinking, resolution.ToolCall),
                    CancellationToken.None);

                if (confirmed)
                {
                    // The modal action itself already persisted the decision — SAVE stores the detected
                    // formats, APPLY stores the human-picked override, and a conclusive RETRY stores the
                    // fresh probe. Re-persisting here would clobber an override/retry with the original
                    // detection, so we only log.
                    _initLog.Add("// FORMAT:  Saved by your confirmation — auto-detection disabled for this model.");
                }
                else
                {
                    // A None/Unknown verdict is written to the format cache during the probe, and a
                    // cached None short-circuits the next probe (None != Unknown). So "re-probe next
                    // session" only works if we drop that cached negative here — otherwise the modal
                    // reappears instantly next session without the model ever being re-probed.
                    await AgentService.ClearChannelFormatsAsync(sourceName, userId: userId);
                    _initLog.Add("// FORMAT:  Left unsaved — cache cleared, will re-probe next session.");
                }
            }

            _initLog.Add("// INIT:    Establishing session...");
            StateHasChanged();

            await RefreshTerminalProjectsAsync();

            var tools      = subAgent != null ? SessionState.GetEnabledToolsForSubAgent(subAgent) : SessionState.GetEnabledTools();
            var mcpServers = subAgent != null ? SessionState.GetMcpServersForSubAgent(subAgent)   : SessionState.McpServers;

            IEnumerable<(string, string)>? agentSkills = null;
            if (subAgent != null)
            {
                var skills = await SkillService.GetForAgentAsync(subAgent.Id);
                agentSkills = skills.Select(s => (s.Name, s.MarkdownContent));
            }

            string? instructionsOverride = subAgent != null
                ? AgentPersona.BuildSystemPrompt(
                    subAgent.GeneratedName, subAgent.GeneratedPersonality, subAgent.UserDirectives, agentSkills, _standingDirective)
                : null;

            _router = new CogitationStreamRouter { Target = this };
            (_agent, _session) = await AgentService.CreateSessionAsync(
                tools,
                sourceName,
                format,
                _router.ThinkingToken,
                mcpServers,
                modelId,
                line => { _initLog.Add(line); _ = InvokeAsync(StateHasChanged); },
                bridgeUserId: BridgeUserId(),
                bridgeNodeId: ResolveNodeId(sourceName),
                userId: SessionState.CurrentUser?.Id,
                instructionsOverride: instructionsOverride,
                agentNameOverride: subAgent?.GeneratedName,
                onToolStart:    _router.ToolStart,
                onToolComplete: _router.ToolComplete,
                onTodoUpdate:   _router.TodoUpdate,
                governanceMode: SessionState.Governance,
                onApprovalRequested: _router.ApprovalRequestedAsync,
                onAskUser: _router.AskUserAsync,
                contextStatusProvider: BuildContextStatusSnapshot,
                activeProjectPath: SessionState.ActiveProject?.Path,
                terminalProjects: SessionState.Projects,
                sessionId: SessionState.SessionToken,
                recallScope: SessionState.RecallScope,
                fleetApprovalRequired: SessionState.FleetApprovalRequired,
                fleetStatusProvider: FleetStatusSnapshot,
                nodeLabels: FleetNodeLabels(),
                // Interactive chat sessions may delegate: spawn_agent/agent_result run a persona
                // headlessly under this session's grant + governance. Depth 0 — children get none.
                subAgentSpawner: SpawnService.ForSession(
                    SessionState.CurrentUser?.Id, SessionState.SessionToken, SessionState.Governance));

            _agentSource    = sourceName;
            _agentModel     = modelId;
            _effectiveContextWindow = await AgentService.ResolveContextWindowAsync(sourceName, modelId, userId);
            _lastSubAgentId = subAgent?.Id;
            var sessionLabel = subAgent != null
                ? $"// SESSION ESTABLISHED — AGENT: {subAgent.GeneratedName.ToUpperInvariant()} //"
                : $"// SESSION ESTABLISHED — SOUL: {SessionState.CurrentUser!.Name.ToUpperInvariant()} //";
            _initLog.Add("// STATUS:  COGITATOR ONLINE");
            _isInitializing = false;
            _initLogExpanded = false;
            _messages.Add(new MessageEntry("system", sessionLabel));

            StateHasChanged();

            _greetingCts?.Cancel();
            _greetingCts?.Dispose();
            _greetingCts = new CancellationTokenSource();
            _ = SendGreetingAsync(_greetingCts.Token);
        }
        catch (Exception ex)
        {
            _isInitializing = false;
            _initLogExpanded = false;
            _initError      = StreamingErrorHelper.FriendlyError(ex.Message, SessionState.SelectedModelSource);
            StateHasChanged();
        }
    }

    private MessageEntry ToMessageEntry(CogitationMessage m) =>
        ToMessageEntryCore(m.Role, m.Content, m.ThinkingContent, m.SectionsJson, m.CreatedAt, m.ImageBase64, m.ImageMediaType, dbMessageId: m.Id);

    private MessageEntry ToMessageEntry(BridgeMessageDto m) =>
        ToMessageEntryCore(m.Role, m.Content, m.ThinkingContent, m.SectionsJson, m.CreatedAt, m.ImageBase64, m.ImageMediaType, bridgeMessageId: m.Id);

    private static readonly System.Text.Json.JsonSerializerOptions SectionJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private MessageEntry ToMessageEntryCore(string role, string content, string? thinkingContent, DateTime createdAt,
        string? imageBase64 = null, string? imageMediaType = null)
    {
        var entry = new MessageEntry(role, content)
        {
            IsSoul         = role == "user",
            SpriteKey      = role == "assistant" ? _historyAvatarKey : null,
            AccentColor    = role == "assistant" ? _historyAccentColor : null,
            AgentName      = role == "assistant" ? _historyAgentName : null,
            Timestamp      = createdAt.ToLocalTime(),
            ImageBase64    = imageBase64,
            ImageMediaType = imageMediaType,
        };
        if (!string.IsNullOrEmpty(thinkingContent))
        {
            entry.ThinkingContent = thinkingContent;
            CollapseThinking(entry);   // history thinking starts folded
        }
        return entry;
    }

    private MessageEntry ToMessageEntryCore(string role, string content, string? thinkingContent, string? sectionsJson, DateTime createdAt,
        string? imageBase64 = null, string? imageMediaType = null, int? dbMessageId = null, string? bridgeMessageId = null)
    {
        var entry = ToMessageEntryCore(role, content, thinkingContent, createdAt, imageBase64, imageMediaType);
        entry.DbMessageId = dbMessageId;
        entry.BridgeMessageId = bridgeMessageId;
        if (!string.IsNullOrWhiteSpace(sectionsJson))
        {
            try
            {
                var sections = System.Text.Json.JsonSerializer.Deserialize<List<MessageSection>>(sectionsJson, SectionJsonOptions);
                if (sections != null)
                    entry.Sections.AddRange(sections);
            }
            catch { /* malformed sections JSON — ignore and leave Content-based view */ }
        }
        return entry;
    }

    // Returns true if channel is ready, false if init should abort (error already logged).
    private async Task<bool> EnsureChannelReadyAsync(string? sourceName = null)
    {
        sourceName ??= SessionState.SelectedModelSource;

        if (!EnsureBridgeBound(sourceName)) return false;

        _initLog.Add("// CHECK:   Testing channel connectivity...");
        StateHasChanged();

        var (ok, url) = await AgentService.CheckConnectivityAsync(sourceName, SessionState.CurrentUser?.Id);
        if (ok) return true;

        _initLog.Add($"// ERROR:   Channel unreachable — {url}");
        _initLog.Add("//          Verify the service is running before opening a session.");
        _isInitializing = false;
        _initLogExpanded = false;
        _initError      = $"Channel unreachable at {url}";
        StateHasChanged();
        return false;
    }

    private void OnCogitationSelected(int cogitationId) =>
        _ = InvokeAsync(async () =>
        {
            DetachFromRun();
            ResetChatState();
            _cogitationTitled = true;
            _cogitationId     = cogitationId;
            // Reflected here (not just by SelectCogitation) so the sidebar's active-row highlight and
            // the tab bar's close-active-tab logic stay correct on bootstrap paths that call this
            // directly (URL restore, pending-selection) rather than going through SelectCogitation.
            SessionState.ActiveCogitationId = cogitationId;
            SessionState.OpenTab(cogitationId);
            SyncChatUrl(cogitationId);
            if (BridgeUserId() is { } seenUserId) Registry.MarkSeen(seenUserId, cogitationId);

            _statusOverride = "LOADING COGITATION...";
            StateHasChanged();

            var cogData = await CogitationService.GetAsync(cogitationId);
            if (cogData != null) CacheTabMeta(cogData);
            _ariaAvatarKey    = cogData?.AriaAvatarKey ?? PickAriaAvatar();
            _isHiveCogitation = cogData?.CollectiveId != null && cogData?.Collective != null;
            _hiveCollectiveId = cogData?.CollectiveId;
            var agentDeleted  = cogData?.SubAgentId != null && cogData?.SubAgent == null;

            if (_isHiveCogitation)
            {
                // Brand the conversation as the Hive rather than as a solo chat with the Overmind's
                // own SubAgent (which SubAgentId happens to point at).
                _historyAgentName   = $"{cogData!.Collective!.Name}'s Overmind";
                _historyAvatarKey   = cogData.Collective.OvermindAvatarPath?.Replace("avatars/", "");
                _historyAccentColor = HivePurpleAccent;

                // Reattach to an in-progress Overmind run (e.g. page refresh mid-orchestration).
                var phase = Orchestrator.GetPhase(_hiveCollectiveId!.Value);
                if (!string.IsNullOrEmpty(phase))
                {
                    _isStreaming    = true;
                    _statusOverride = $"OVERMIND: {phase.ToUpperInvariant()}…";
                }
            }
            else
            {
                _historyAgentName   = agentDeleted ? "Deleted Agent" : cogData?.SubAgent?.DisplayName;
                _historyAvatarKey   = agentDeleted ? null : (cogData?.SubAgent?.AvatarSpriteKey ?? _ariaAvatarKey);
                _historyAccentColor = agentDeleted ? null : cogData?.SubAgent?.AccentColor;
            }

            // Bind the rebuilt session to the cogitation's OWN sub-agent (its channel/model/tools/
            // persona) — not the globally-selected channel.
            SubAgent? cogAgent = null;
            if (!agentDeleted && cogData?.SubAgentId is { } cogAgentId)
                cogAgent = SessionState.ActiveSubAgent?.Id == cogAgentId
                    ? SessionState.ActiveSubAgent
                    : await SubAgentService.GetByIdAsync(cogAgentId);
            _activeCogAgent = cogAgent;

            // Preserve the dossier's standing directive across soft rebuilds of an existing cogitation.
            if (cogData?.FolderId is { } folderId)
            {
                var folder = await FolderService.GetByIdAsync(folderId);
                if (!string.IsNullOrWhiteSpace(folder?.StandingDirective))
                    _standingDirective = folder.StandingDirective.Trim();
            }

            _cogitationOriginNodeId = cogData?.OriginNodeId;
            _cogitationOffline      = !IsCogitationContentAvailable();

            if (_cogitationOffline)
            {
                _historyLoaded   = false;
                _historyInjected = false;
                _statusOverride  = null;
                _isInitializing  = false;
                _initLogExpanded = false;
                StateHasChanged();
                return;
            }

            List<MessageEntry> historyEntries;
            if (_cogitationOriginNodeId == null)
            {
                // Legacy/server-stored content.
                var msgs = await CogitationService.GetMessagesAsync(cogitationId);
                historyEntries = msgs.Select(ToMessageEntry).ToList();
            }
            else
            {
                // Bridge-owned content: read from the owning node (with fallback to synced copies).
                var userId = BridgeUserId();
                var msgs = userId != null
                    ? await BridgeCogitation.GetMessagesAsync(userId, cogitationId, _cogitationOriginNodeId)
                    : [];
                historyEntries = msgs.Select(ToMessageEntry).ToList();
            }

            foreach (var entry in historyEntries)
                _messages.Add(entry);

            _historyLoaded   = historyEntries.Count > 0;
            _historyInjected = false;

            // A freshly-opened, still-empty Hive cogitation gets no messages until the soul sends one —
            // have the Overmind introduce itself first, same as a normal new chat's greeting.
            if (_isHiveCogitation && historyEntries.Count == 0 && !_hiveGreetingSent)
            {
                _hiveGreetingSent = true;
                _ = SendHiveGreetingAsync(_hiveCollectiveId!.Value, cogitationId);
            }

            // Reattach to a run still streaming in the background (survived a prior navigation away,
            // or a page refresh) instead of rebuilding the session from scratch.
            if (BridgeUserId() is { } reattachUserId &&
                Registry.TryGet(cogitationId) is { } run && run.UserId == reattachUserId)
            {
                // The reply is guaranteed unpersisted while Streaming or AwaitingContextApproval (a
                // halted run stays in the registry, unpersisted, until it's retried or cancelled — see
                // CogitationRunRegistry.RunLoopAsync). Treating only Streaming as "still live" here left
                // _isStreaming/_streamingMsg desynced from the registry's real busy state on reattach: the
                // input looked ready while the cogitation was actually still blocked on approval, so a
                // reattached "try again" either got silently swallowed or queued with no visible feedback.
                // During the brief Persisting window (run just finished while we were loading history)
                // only append it if the just-loaded history doesn't already contain it, to avoid rendering
                // it twice.
                var appendMirror = run.Status is CogitationRunStatus.Streaming or CogitationRunStatus.AwaitingContextApproval;
                if (run.Status == CogitationRunStatus.Persisting)
                {
                    var lastAssistant = _messages.LastOrDefault(m => m.Role == "assistant");
                    appendMirror = lastAssistant == null || lastAssistant.Content != run.Reply.Content;
                }

                _agent           = run.Agent;
                _session         = run.Session;
                _router          = run.Router;
                _agentSource     = run.AgentSourceName;
                _agentModel      = run.AgentModel;
                _lastSubAgentId  = run.SubAgentId;
                _historyInjected = true;   // adopted session already holds this turn's full context

                if (appendMirror)
                {
                    var mirror = new MessageEntry("assistant", "")
                    {
                        SpriteKey   = run.Reply.SpriteKey,
                        AccentColor = run.Reply.AccentColor,
                        AgentName   = run.Reply.AgentName,
                        IsSoul      = false,
                    };
                    _messages.Add(mirror);
                    _streamingMsg   = mirror;
                    _thinkingTarget = mirror;
                    _isStreaming    = true;
                }

                AttachToRun(run);

                _statusOverride = null;
                _isInitializing = false;
                _initLogExpanded = false;
                StateHasChanged();
                await ScrollToBottomAsync();
                return;
            }

            var sourceName = cogAgent?.ModelSourceName ?? SessionState.SelectedModelSource;
            var modelId    = cogAgent?.ModelId         ?? SessionState.SelectedModel;

            _initLog.Add($"// CHANNEL: {sourceName ?? "UNKNOWN"}");
            _initLog.Add($"// MODEL:   {modelId ?? "DEFAULT"}");
            if (cogAgent != null)
                _initLog.Add($"// AGENT:   {cogAgent.GeneratedName.ToUpperInvariant()} [{cogAgent.ArchetypeName}]");
            _statusOverride = null;
            _isInitializing = true;
            _initLogExpanded = true;   // auto-open the probing pop-up for reopened cogitations
            StateHasChanged();

            try
            {
                if (!await EnsureChannelReadyAsync(sourceName)) return;

                _initLog.Add("// PROBE:   Analysing reasoning format <em>(waiting model reply)</em> ...");
                StateHasChanged();

                var format = await AgentService.DetectThinkingFormatAsync(
                    sourceName, modelId, userId: SessionState.CurrentUser?.Id);

                _initLog.Add($"// FORMAT:  {format}");
                _initLog.Add("// INIT:    Establishing session...");
                StateHasChanged();

                var tools      = cogAgent != null ? SessionState.GetEnabledToolsForSubAgent(cogAgent) : SessionState.GetEnabledTools();
                var mcpServers = cogAgent != null ? SessionState.GetMcpServersForSubAgent(cogAgent)   : SessionState.McpServers;

                string? instructionsOverride = null;
                if (cogAgent != null)
                {
                    var skills = await SkillService.GetForAgentAsync(cogAgent.Id);
                    instructionsOverride = AgentPersona.BuildSystemPrompt(
                        cogAgent.GeneratedName, cogAgent.GeneratedPersonality, cogAgent.UserDirectives,
                        skills.Select(s => (s.Name, s.MarkdownContent)), _standingDirective);
                }

                _router = new CogitationStreamRouter { Target = this };
                (_agent, _session) = await AgentService.CreateSessionAsync(
                    tools,
                    sourceName,
                    format,
                    _router.ThinkingToken,
                    mcpServers,
                    modelId,
                    line => { _initLog.Add(line); _ = InvokeAsync(StateHasChanged); },
                    bridgeUserId: BridgeUserId(),
                    bridgeNodeId: ResolveNodeId(sourceName),
                    userId: SessionState.CurrentUser?.Id,
                    instructionsOverride: instructionsOverride,
                    agentNameOverride: cogAgent?.GeneratedName,
                    onToolStart:    _router.ToolStart,
                    onToolComplete: _router.ToolComplete,
                    onTodoUpdate:   _router.TodoUpdate,
                governanceMode: SessionState.Governance,
                onApprovalRequested: _router.ApprovalRequestedAsync,
                onAskUser: _router.AskUserAsync,
                contextStatusProvider: BuildContextStatusSnapshot,
                activeProjectPath: SessionState.ActiveProject?.Path,
                terminalProjects: SessionState.Projects,
                sessionId: SessionState.SessionToken,
                recallScope: SessionState.RecallScope,
                fleetApprovalRequired: SessionState.FleetApprovalRequired,
                fleetStatusProvider: FleetStatusSnapshot,
                nodeLabels: FleetNodeLabels(),
                // Interactive chat sessions may delegate: spawn_agent/agent_result run a persona
                // headlessly under this session's grant + governance. Depth 0 — children get none.
                subAgentSpawner: SpawnService.ForSession(
                    SessionState.CurrentUser?.Id, SessionState.SessionToken, SessionState.Governance));

                _agentSource    = sourceName;
                _agentModel     = modelId;
                _initLog.Add("// STATUS:  COGITATOR ONLINE");
                _isInitializing = false;
                _initLogExpanded = false;
            }
            catch (Exception ex)
            {
                _initError      = StreamingErrorHelper.FriendlyError(ex.Message, sourceName);
                _isInitializing = false;
                _initLogExpanded = false;
            }

            StateHasChanged();
            await ScrollToBottomAsync();
        });

    // ── Suggested filing banner ───────────────────────────────────────────
    private async Task CheckSuggestedFilingAsync()
    {
        if (_cogitationId == null || SessionState.CurrentUser == null) return;
        if (_suggestedFilingVisible) return;
        if (SessionState.ActiveProject?.Path is not { } projectPath) return;

        var cog = await CogitationService.GetAsync(_cogitationId.Value);
        if (cog?.FolderId != null || cog?.SuggestedFilingDismissed == true) return;

        var folders = await FolderService.GetListAsync(SessionState.CurrentUser.Id);
        var matches = folders
            .Where(f => !string.IsNullOrWhiteSpace(f.DefaultProjectPath) &&
                        f.DefaultProjectPath.Equals(projectPath, StringComparison.Ordinal))
            .ToList();

        if (matches.Count != 1) return;

        var match = matches[0];
        _suggestedFolderId    = match.Id;
        _suggestedFolderName  = match.Name;
        _suggestedFolderColor = match.Color ?? "#8B0000";
        _suggestedFilingVisible = true;
        await InvokeAsync(StateHasChanged);
    }

    private async Task AcceptSuggestedFilingAsync()
    {
        if (_cogitationId == null || _suggestedFolderId == null) return;
        await CogitationService.MoveToFolderAsync(_cogitationId.Value, _suggestedFolderId.Value);
        _suggestedFilingVisible = false;
        SessionState.NotifyCogitationsChanged();
        await InvokeAsync(StateHasChanged);
    }

    private async Task DismissSuggestedFilingAsync()
    {
        if (_cogitationId == null) return;
        await CogitationService.SetSuggestedFilingDismissedAsync(_cogitationId.Value, true);
        _suggestedFilingVisible = false;
        await InvokeAsync(StateHasChanged);
    }

    // Soft re-init: agent config updated in-place — rebuild session with new tools, keep messages.
    private void OnActiveSubAgentUpdated(SubAgent updated) => _ = InvokeAsync(async () =>
    {
        if (_agent == null || !CanInit()) return;
        CancelActiveStreaming();

        _agent           = null;
        _session         = null;
        _isStreaming      = false;
        _streamingMsg    = null;
        _historyInjected = false;
        _historyLoaded   = _messages.Any(m => m.Role != "system");

        _messages.Add(new MessageEntry("system", "// TOOLS UPDATED — SESSION REFRESHED //"));
        StateHasChanged();

        try
        {
            var subAgent   = SessionState.ActiveSubAgent;
            var sourceName = subAgent?.ModelSourceName ?? SessionState.SelectedModelSource;
            var modelId    = subAgent?.ModelId         ?? SessionState.SelectedModel;
            var tools      = subAgent != null ? SessionState.GetEnabledToolsForSubAgent(subAgent) : SessionState.GetEnabledTools();
            var mcpServers = subAgent != null ? SessionState.GetMcpServersForSubAgent(subAgent)   : SessionState.McpServers;
            var format     = await AgentService.DetectThinkingFormatAsync(sourceName, modelId, userId: SessionState.CurrentUser?.Id);

            IEnumerable<(string, string)>? refreshSkills = null;
            if (subAgent != null)
            {
                var skills = await SkillService.GetForAgentAsync(subAgent.Id);
                refreshSkills = skills.Select(s => (s.Name, s.MarkdownContent));
            }

            string? instructions = subAgent != null
                ? AgentPersona.BuildSystemPrompt(
                    subAgent.GeneratedName, subAgent.GeneratedPersonality, subAgent.UserDirectives, refreshSkills, _standingDirective)
                : null;

            _router = new CogitationStreamRouter { Target = this };
            (_agent, _session) = await AgentService.CreateSessionAsync(
                tools, sourceName, format, _router.ThinkingToken, mcpServers, modelId,
                _ => { }, bridgeUserId: BridgeUserId(),
                bridgeNodeId: ResolveNodeId(sourceName),
                userId: SessionState.CurrentUser?.Id,
                instructionsOverride: instructions,
                agentNameOverride: subAgent?.GeneratedName,
                onToolStart:    _router.ToolStart,
                onToolComplete: _router.ToolComplete,
                onTodoUpdate:   _router.TodoUpdate,
                governanceMode: SessionState.Governance,
                onApprovalRequested: _router.ApprovalRequestedAsync,
                onAskUser: _router.AskUserAsync,
                contextStatusProvider: BuildContextStatusSnapshot,
                activeProjectPath: SessionState.ActiveProject?.Path,
                terminalProjects: SessionState.Projects,
                sessionId: SessionState.SessionToken,
                recallScope: SessionState.RecallScope,
                fleetApprovalRequired: SessionState.FleetApprovalRequired,
                fleetStatusProvider: FleetStatusSnapshot,
                nodeLabels: FleetNodeLabels(),
                // Interactive chat sessions may delegate: spawn_agent/agent_result run a persona
                // headlessly under this session's grant + governance. Depth 0 — children get none.
                subAgentSpawner: SpawnService.ForSession(
                    SessionState.CurrentUser?.Id, SessionState.SessionToken, SessionState.Governance));

            _agentSource = sourceName;
            _agentModel  = modelId;
        }
        catch (Exception ex)
        {
            _messages.Add(new MessageEntry("system", $"// REFRESH FAULT: {ex.Message} //"));
        }

        StateHasChanged();
    });

    // Tool or MCP server settings changed — rebuild the agent in-place so the new
    // tool list takes effect immediately without wiping the message history.
    private void OnToolSettingsChanged()
    {
        _ = RefreshTerminalProjectsAsync();
        _ = RebuildSessionAsync("// TOOLS UPDATED — SESSION REFRESHED //");
        _ = RefreshTerminalBridgeStatusAsync();
    }

    // Selecting a project in the file explorer / via "/project" scopes the agent's Terminal tools to
    // just that project (see HarnessOptions.ActiveProjectPath) — so rebuild the session in-place, the
    // same soft re-init a tool-settings change does, but only when the selection actually changed.
    private async Task SetActiveProjectAsync(TerminalProject? project)
    {
        if (SessionState.ActiveProject?.Path == project?.Path) return;
        SessionState.ActiveProject = project;
        // Project changed: force the Changes tab to refresh the next time it is opened.
        _changesLoadedForPath = null;
        _changesNeedRefresh = true;
        if (_agent != null && CanInit())
        {
            var note = project != null
                ? $"// PROJECT SCOPE → {project.Name.ToUpperInvariant()} — SESSION REFRESHED //"
                : "// PROJECT SCOPE CLEARED — SESSION REFRESHED //";
            await RebuildSessionAsync(note);
        }
    }

    // Soft re-init: rebuild the session (new tools/scope) in-place, keeping the message history.
    // The old agent/session stay mounted while the new one is built and are swapped in atomically —
    // never nulled mid-flight — so the render stays in the chat-body branch and the file explorer
    // panel (whose width is a JS-applied inline style) isn't torn down and recreated at its CSS
    // default width. Sends are blocked via _rebuilding until the swap completes.
    private Task RebuildSessionAsync(string systemNote) => InvokeAsync(async () =>
    {
        if (_agent == null || !CanInit() || _rebuilding) return;
        CancelActiveStreaming();

        _rebuilding = true;
        _messages.Add(new MessageEntry("system", systemNote));
        StateHasChanged();

        try
        {
            var subAgent   = SessionState.ActiveSubAgent;
            var sourceName = subAgent?.ModelSourceName ?? SessionState.SelectedModelSource;
            var modelId    = subAgent?.ModelId         ?? SessionState.SelectedModel;
            var tools      = subAgent != null ? SessionState.GetEnabledToolsForSubAgent(subAgent) : SessionState.GetEnabledTools();
            var mcpServers = subAgent != null ? SessionState.GetMcpServersForSubAgent(subAgent)   : SessionState.McpServers;
            var format     = await AgentService.DetectThinkingFormatAsync(sourceName, modelId, userId: SessionState.CurrentUser?.Id);

            string? instructions = subAgent != null
                ? AgentPersona.BuildSystemPrompt(
                    subAgent.GeneratedName, subAgent.GeneratedPersonality, subAgent.UserDirectives, null, _standingDirective)
                : null;

            var newRouter = new CogitationStreamRouter { Target = this };
            var (newAgent, newSession) = await AgentService.CreateSessionAsync(
                tools, sourceName, format, newRouter.ThinkingToken, mcpServers, modelId,
                _ => { }, bridgeUserId: BridgeUserId(),
                bridgeNodeId: ResolveNodeId(sourceName),
                userId: SessionState.CurrentUser?.Id,
                instructionsOverride: instructions,
                agentNameOverride: subAgent?.GeneratedName,
                onToolStart:    newRouter.ToolStart,
                onToolComplete: newRouter.ToolComplete,
                onTodoUpdate:   newRouter.TodoUpdate,
                governanceMode: SessionState.Governance,
                onApprovalRequested: newRouter.ApprovalRequestedAsync,
                onAskUser: newRouter.AskUserAsync,
                contextStatusProvider: BuildContextStatusSnapshot,
                activeProjectPath: SessionState.ActiveProject?.Path,
                terminalProjects: SessionState.Projects,
                sessionId: SessionState.SessionToken,
                recallScope: SessionState.RecallScope,
                fleetApprovalRequired: SessionState.FleetApprovalRequired,
                fleetStatusProvider: FleetStatusSnapshot,
                nodeLabels: FleetNodeLabels(),
                // Interactive chat sessions may delegate: spawn_agent/agent_result run a persona
                // headlessly under this session's grant + governance. Depth 0 — children get none.
                subAgentSpawner: SpawnService.ForSession(
                    SessionState.CurrentUser?.Id, SessionState.SessionToken, SessionState.Governance));

            // Atomic swap — only now do the old agent/session/router give way to the new ones.
            _router          = newRouter;
            _agent           = newAgent;
            _session         = newSession;
            _agentSource     = sourceName;
            _agentModel      = modelId;
            _streamingMsg    = null;
            _isStreaming     = false;
            _historyInjected = false;
            _historyLoaded   = _messages.Any(m => m.Role != "system");
        }
        catch (Exception ex)
        {
            _messages.Add(new MessageEntry("system", $"// REFRESH FAULT: {ex.Message} //"));
        }
        finally
        {
            _rebuilding = false;
        }

        StateHasChanged();
    });
}
