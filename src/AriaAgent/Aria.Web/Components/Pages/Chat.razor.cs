using Aria.Agent;
using Aria.Shared;
using Aria.Web.Data;
using Aria.Web.Services.Chat;
using Aria.Web.Services;
using Aria.Web.Services.Tool;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Threading;

namespace Aria.Web.Components.Pages;

public partial class Chat : ICogitationStreamSink
{
    [Inject] private AgentService              AgentService         { get; set; } = null!;
    [Inject] private CogitationRunRegistry      Registry             { get; set; } = null!;
    [Inject] private UserSessionState          SessionState         { get; set; } = null!;
    [Inject] private CogitationService         CogitationService    { get; set; } = null!;
    [Inject] private SubAgentService           SubAgentService      { get; set; } = null!;
    [Inject] private SubAgentSpawnService      SpawnService         { get; set; } = null!;
    [Inject] private IJSRuntime                JS                   { get; set; } = null!;
    [Inject] private ModelBridgeRegistry       BridgeRegistry       { get; set; } = null!;
    [Inject] private CircuitAuthService        CircuitAuth          { get; set; } = null!;
    [Inject] private BridgeCogitationClient    BridgeCogitation     { get; set; } = null!;
    [Inject] private BridgeMetricsClient       MetricsClient        { get; set; } = null!;
    [Inject] private VoxService                VoxService           { get; set; } = null!;
    [Inject] private SkillService              SkillService         { get; set; } = null!;
    [Inject] private CollectiveOrchestrator    Orchestrator         { get; set; } = null!;
    [Inject] private SealService               SealService          { get; set; } = null!;
    [Inject] private ContextApprovalService    ContextApproval      { get; set; } = null!;
    [Inject] private NavigationManager         Nav                  { get; set; } = null!;
    [Inject] private UserService               UserService          { get; set; } = null!;
    [Inject] private BridgeMemoryClient         MemoryClient         { get; set; } = null!;
    [Inject] private CogitationFolderService    FolderService        { get; set; } = null!;
    [Inject] private TerminalClient             TerminalClient       { get; set; } = null!;
    [Inject] private TerminalPtyService         TerminalPtyService   { get; set; } = null!;
    [Inject] private UserToolService             ToolService          { get; set; } = null!;

    [Parameter] public int? CogitationId { get; set; }

    // Format-detection modal (extracted component); session init awaits its RequestConfirmationAsync.
    private ChatFormatModal? _formatModal;

    private readonly List<MessageEntry>  _messages   = [];
    private readonly List<string>        _initLog    = [];

    private static MarkupString FormatInitLogLine(string line)
    {
        var encoded = System.Net.WebUtility.HtmlEncode(line)
            .Replace("&lt;em&gt;", "<em>")
            .Replace("&lt;/em&gt;", "</em>");
        return new MarkupString(encoded);
    }
    private string        _input            = "";
    private volatile bool _isStreaming      = false;
    private string?       _initError        = null;
    private string?       _statusOverride;
    private AIAgent?      _agent;
    private AgentSession? _session;
    private MessageEntry? _streamingMsg;
    // Thinking tokens arrive out-of-band via OnThinkingToken, posted to the dispatcher from a
    // threadpool thread, so they can land AFTER the content loop's finally nulls _streamingMsg
    // (common when a warm model bursts the whole response, e.g. the post-/clear greeting). Route
    // thinking here instead — set when a stream starts, only overwritten by the next stream — so
    // late thinking tokens still reach the right message rather than being dropped.
    private MessageEntry? _thinkingTarget;
    // Latest task manifest, pinned above the composer. Replaced wholesale on each update.
    private List<Aria.Tools.TodoItem> _currentManifest = [];
    private bool _manifestCollapsed;
    // The retargetable router the live agent's tools were built with (Harness bakes callbacks in at
    // construction — see CogitationStreamRouter) and the background run currently attached to this
    // view, if any. Detaching (navigation, switch) never cancels _attachedRun — only StopStreaming
    // or a config-rebuild does, via Registry.Cancel.
    private CogitationStreamRouter? _router;
    private CogitationRun?          _attachedRun;
    // The exact delegate instances subscribed in AttachToRun, so DetachFromRun can remove precisely
    // those (a `-=` with a different lambda instance would silently no-op and leak the subscription).
    private Action? _runUpdatedHandler;
    private Action? _runCompletedHandler;
    private Action? _runApprovalHandler;
    private CancellationTokenSource? _greetingCts;
    private string?       _agentSource;
    private string?       _agentModel;
    private string?       _lastUserId;
    private int?          _lastSubAgentId;
    // Owns the currently-open cogitation (reopen path). Distinct from the globally active sub-agent.
    private SubAgent?     _activeCogAgent;
    private int?          _cogitationId;
    private bool          _cogitationTitled;
    private bool          _historyLoaded;
    private bool          _historyInjected;
    private string?       _cogitationOriginNodeId;
    private bool          _cogitationOffline;
    // Backing field for _isInitializing below — mirrored onto SessionState.IsSessionInitializing so
    // NavMenu (a separate component) can grey out "NEW COGITATION" while a probe is in flight.
    private bool          _isInitializingField;
    private bool _isInitializing
    {
        get => _isInitializingField;
        set
        {
            _isInitializingField = value;
            SessionState.IsSessionInitializing = value;
        }
    }
    private bool          _rebuilding;   // soft session rebuild (tool/scope change) in flight — blocks sends
    private bool          _waitingForBridge;
    private bool          _initLogExpanded;
    private string?       _attachedFileName;
    private string?       _attachedFileContent;
    private string?       _attachError;
    private DateTime      _streamStart;
    private bool          _smartScrollPending;
    private string        _queuedInput      = "";
    private bool          _compactConfirmOpen;

    // Dossier defaults applied at session build for new chats; preserved across soft rebuilds.
    private string? _standingDirective;
    private bool    _explorerProjectFromFolder;

    // Suggested filing banner: shown once when an unfiled chat's active project matches exactly one dossier.
    private bool    _suggestedFilingVisible;
    private int?    _suggestedFolderId;
    private string? _suggestedFolderName;
    private string? _suggestedFolderColor;

    // Bridge telemetry panel state — one entry per connected node (both bridges of a multi-node soul).
    private IReadOnlyList<NodeMetrics> _nodeMetrics = [];
    // Node ids whose per-bridge metrics block is collapsed (click the green node badge to toggle).
    private readonly HashSet<string> _collapsedTelemetryNodes = new();
    private bool           _telemetryKeepExpanded;
    private bool           _telemetryCollapsed = true;
    private readonly CancellationTokenSource _metricsCts = new();
    private Task?          _metricsLoopTask;
    private long           _metricsRenderPending;
    private int            _telemetryTickKey;
    private string         _tickDuration = "2s";
    private DateTime?      _lastMetricsRenderTime;

    private string  _ariaAvatarKey      = "";
    private string? _historyAgentName   = null;
    private string? _historyAvatarKey   = null;
    private string? _historyAccentColor = null;
    // True when the open cogitation is a Hive collective run — swaps the header/avatar/accent
    // from the Overmind's own SubAgent identity to the Hive's, and tints the chat shell purple.
    private bool    _isHiveCogitation   = false;
    // The collective a Hive cogitation belongs to — needed to route sends through the
    // Overmind/drone orchestration instead of the normal single-agent session.
    private int?    _hiveCollectiveId   = null;
    // Guards against firing the Overmind's self-introduction more than once per cogitation
    // (OnCogitationSelected can re-run before the greeting call persists its message).
    private bool    _hiveGreetingSent   = false;
    private const string HivePurpleAccent = "#8060c0"; // matches the Overmind/LLM-judge purple used on the Hive canvas

    private string  _unlockCode = "";
    private string? _unlockError;
    private bool    _unlocking;

    private static string PickAriaAvatar() => $"aria-{Random.Shared.Next(1, 5)}.jpeg";

    private bool CanInit() => SessionState.SelectedModelSource != null;

    private string? BridgeUserId() => SessionState.CurrentUser?.Id.ToString();

    /// <summary>Fetches the bridge-authoritative Terminal project list and caches it in session state.
    /// Call whenever the bridge comes online or the Terminal tool state changes.</summary>
    private async Task RefreshTerminalProjectsAsync()
    {
        var userId = BridgeUserId();
        if (userId == null) return;
        try
        {
            var projects = await TerminalClient.GetAllProjectsAsync(userId);
            SessionState.SetProjects(projects);
        }
        catch { }
    }

    // The sub-agent governing the active conversation: the globally active one for a fresh chat,
    // or the cogitation's own agent when a past cogitation was reopened.
    private SubAgent? EffectiveAgent => SessionState.ActiveSubAgent ?? _activeCogAgent;

    // The bridge node this session runs on. Cloud channels return null (node-agnostic).
    private string? ResolveNodeId(string? sourceName) =>
        sourceName != null && SessionState.CurrentUser is { } u
            ? AgentService.GetSourcesForUser(u.Id).FirstOrDefault(s => s.Name == sourceName)?.BridgeNodeId
            : null;

    /// <summary>
    /// Returns the currently-connected default bridge node id for the current user, or null if none.
    /// </summary>
    private string? GetDefaultBridgeNodeId()
    {
        var userId = BridgeUserId();
        if (userId == null) return null;
        var node = BridgeRegistry.GetNodes(userId).FirstOrDefault();
        return node?.NodeId;
    }

    /// <summary>
    /// Returns the bridge node that should own a new cogitation: the channel-bound node if the current
    /// channel is bridged, otherwise the user's default connected node.
    /// </summary>
    private string? GetActiveBridgeNodeId()
    {
        var sourceName = SessionState.SelectedModelSource;
        if (!string.IsNullOrEmpty(sourceName))
        {
            var channelNodeId = ResolveNodeId(sourceName);
            if (!string.IsNullOrEmpty(channelNodeId)) return channelNodeId;
        }
        return GetDefaultBridgeNodeId();
    }

    /// <summary>
    /// True when the current cogitation's content is available from a connected node.
    /// Legacy/server-stored cogitations are always available.
    /// </summary>
    private bool IsCogitationContentAvailable()
    {
        if (_cogitationOriginNodeId == null) return true;
        var userId = BridgeUserId();
        if (userId == null) return false;
        return BridgeRegistry.GetNodes(userId).Any(n => n.NodeId == _cogitationOriginNodeId);
    }

    /// <summary>Returns a display label for the current cogitation's origin node, or the raw id.</summary>
    private string GetCogitationOriginNodeLabel()
    {
        if (_cogitationOriginNodeId == null) return "server";
        var userId = BridgeUserId();
        if (userId == null) return _cogitationOriginNodeId;
        var node = BridgeRegistry.GetNodes(userId).FirstOrDefault(n => n.NodeId == _cogitationOriginNodeId)
                   ?? BridgeRegistry.GetNodes(userId).FirstOrDefault();
        return string.IsNullOrWhiteSpace(node?.Label) ? _cogitationOriginNodeId : node.Label;
    }

    // A bridged channel must be bound to a connected bridge. Errors clearly (no silent fallback).
    private bool EnsureBridgeBound(string? sourceName)
    {
        if (sourceName == null || SessionState.CurrentUser is not { } u) return true;
        var src = AgentService.GetSourcesForUser(u.Id).FirstOrDefault(s => s.Name == sourceName);
        if (src is not { IsBridged: true }) return true;

        if (string.IsNullOrEmpty(src.BridgeNodeId))
        {
            _initError      = $"Channel '{sourceName}' is not bound to a bridge. Pick one in Channels.";
            _isInitializing = false;
            _initLog.Add("// ERROR:   Channel not bound to a bridge.");
            StateHasChanged();
            return false;
        }
        if (!BridgeRegistry.TryGetNode(u.Id.ToString(), src.BridgeNodeId, out _))
        {
            _initError      = $"The bridge bound to '{sourceName}' is offline. Start it, or pick a connected bridge.";
            _isInitializing = false;
            _initLog.Add("// ERROR:   Bound bridge offline.");
            StateHasChanged();
            return false;
        }
        return true;
    }

    // Per-circuit verification (§12): true only when THIS browser circuit has attested via its local bridge.
    private bool SoulVerified =>
        SessionState.CurrentUser != null &&
        CircuitAuth.IsVerified(SessionState.CurrentUser.Id);

    private void OnUnlockKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") _ = UnlockWithCodeAsync();
    }

    private async Task UnlockWithCodeAsync()
    {
        if (_unlocking) return;
        _unlockError = null;
        if (string.IsNullOrWhiteSpace(_unlockCode)) { _unlockError = "Enter the session code from your bridge."; return; }
        _unlocking = true;
        StateHasChanged();
        var (ok, error) = await SessionState.TryCodeUnlockAsync(_unlockCode);
        if (ok) _unlockCode = "";
        else    _unlockError = error;
        _unlocking = false;
        StateHasChanged();
    }

    // Returns true if we started waiting (caller should bail out of init).
    private bool WaitForBridgeIfNeeded()
    {
        var uid = BridgeUserId();
        if (uid == null) return false;

        if (BridgeRegistry.HasBridge(uid))
            return false;

        _waitingForBridge = true;
        _isInitializing   = true;
        _initError        = null;
        _initLog.Clear();
        _initLog.Add($"// CHANNEL: {(SessionState.SelectedModelSource ?? "UNKNOWN").ToUpper()}");
        _initLog.Add("// BRIDGE:  Waiting for bridge daemon to connect...");
        StateHasChanged();
        return true;
    }

    // Leaves the currently-attached background run alone — it keeps streaming and will persist its
    // own reply. Used for navigation/switch/new-chat, where the user isn't asking to stop anything,
    // just to look elsewhere.
    private void DetachFromRun()
    {
        if (_attachedRun != null)
        {
            _attachedRun.HasAttachedViewer = false;
            if (_runUpdatedHandler   != null) _attachedRun.Updated         -= _runUpdatedHandler;
            if (_runCompletedHandler != null) _attachedRun.Completed       -= _runCompletedHandler;
            if (_runApprovalHandler  != null) _attachedRun.ApprovalChanged -= _runApprovalHandler;
            _attachedRun = null;
        }
        _runUpdatedHandler   = null;
        _runCompletedHandler = null;
        _runApprovalHandler  = null;
        // The approval bar is keyed only on _pendingApproval, not on _attachedRun — without clearing
        // it here it keeps rendering a stale gate for a run we no longer reference. Its buttons then
        // fall into the run-less TCS branch (Chat.Approval.razor.cs), which was never populated for
        // this run, so clicking AUTHORISE/REFUSE silently no-ops while the real pending approval sits
        // unresolved on the orphaned run until its own timeout. Reattaching (AttachToRun) re-derives
        // this fresh from the run's current PendingApproval, so clearing it here loses nothing.
        _pendingApproval = null;
        // Same stale-gate reasoning as the approval bar: reattaching re-derives this from the
        // run's current PendingAskUser, so clearing it here loses nothing.
        _pendingAskUser  = null;
        _awaitingContextApprovalSessionId = null;
        _isStreaming = false;
        _router      = null;
        _greetingCts?.Cancel();
        _greetingCts?.Dispose();
        _greetingCts = null;
    }

    // Cancels the run for the cogitation currently open in this view (config/session rebuilds only —
    // the agent/session snapshot the run owns is about to be replaced underneath it) then detaches.
    private void CancelActiveStreaming()
    {
        if (_cogitationId.HasValue) Registry.Cancel(_cogitationId.Value);
        // Stop any in-flight context-approval poll for this cogitation — otherwise it keeps polling the
        // node for up to 3 min and, on late success, would adopt this cogitation's retried run into
        // whatever view the user has since switched to. (The continuation is also cogId-guarded.)
        try { _contextApprovalCts?.Cancel(); } catch { }
        DetachFromRun();
    }

    // User-pressed Stop: cancel the in-flight generation. The run's finally-block flips status and
    // persists the partial reply.
    private void StopStreaming()
    {
        if (_cogitationId.HasValue) Registry.Cancel(_cogitationId.Value);
        try { _greetingCts?.Cancel(); } catch { }
    }

    private void ResetChatState()
    {
        DetachFromRun();
        _agent            = null;
        _session          = null;
        _agentSource      = null;
        _agentModel       = null;
        _activeCogAgent   = null;
        _lastSubAgentId   = SessionState.ActiveSubAgent?.Id;
        _isStreaming      = false;
        _streamingMsg     = null;
        _thinkingTarget   = null;
        _currentManifest.Clear();
        _messages.Clear();
        _refreshedToolCalls.Clear();
        _initLog.Clear();
        _initError        = null;
        _initLogExpanded  = false;
        _cogitationId         = null;
        _cogitationTitled     = false;
        _historyLoaded        = false;
        _historyInjected      = false;
        _cogitationOriginNodeId = null;
        _cogitationOffline    = false;
        _ariaAvatarKey        = "";
        _historyAgentName     = null;
        _historyAvatarKey     = null;
        _historyAccentColor   = null;
        _isHiveCogitation     = false;
        _hiveCollectiveId     = null;
        _hiveGreetingSent     = false;
        _hiveTyping           = null;
        _standingDirective    = null;
        _explorerProjectFromFolder = false;
        _suggestedFilingVisible = false;
        _suggestedFolderId      = null;
        _suggestedFolderName    = null;
        _suggestedFolderColor   = null;
        SessionState.ActiveCogitationId = null;
    }

    protected override async Task OnInitializedAsync()
    {
        SessionState.OnChange                  += OnSessionChanged;
        SessionState.SourceChanged             += OnSourceChanged;
        SessionState.NewChatRequested          += OnNewChatRequested;
        SessionState.CogitationSelected        += OnCogitationSelected;
        SessionState.ActiveSubAgentUpdated     += OnActiveSubAgentUpdated;
        SessionState.ActiveProjectChanged      += OnActiveProjectChanged;
        SessionState.ToolSettingsChanged       += OnToolSettingsChanged;
        SessionState.UnseenVigilCountChanged   += OnUnseenVigilCountChanged;
        SessionState.HiveCogitationUpdated     += OnHiveCogitationUpdated;
        SessionState.OpenTabsChanged           += OnOpenTabsChanged;
        SessionState.CogitationsChanged        += OnCogitationsChangedForTabs;
        Orchestrator.OnHiveGatePending         += OnHiveGatePending;
        Orchestrator.OnHiveGateResolved        += OnHiveGateResolved;
        Orchestrator.OnHiveMemberGatePending   += OnHiveMemberGatePending;
        Orchestrator.OnHiveMemberGateResolved  += OnHiveMemberGateResolved;
        Orchestrator.OnHiveRunStateChanged     += OnHiveRunStateChanged;
        BridgeRegistry.DirectBridgeRegistered  += OnDirectBridgeRegistered;
        BridgeRegistry.SoulStatusChanged       += OnBridgeSoulStatusChanged;
        BridgeRegistry.NodesChanged            += OnNodesChanged;
        Registry.RunsChanged                   += OnRegistryRunChanged;
        ProjectFiles.ApprovalPendingChanged    += OnProjectFilesApprovalPending;
        _lastUserId = SessionState.CurrentUser?.Id;
        if (_lastUserId != null)
        {
            _telemetryKeepExpanded = await UserService.GetKeepTelemetryExpandedAsync(_lastUserId);
            _telemetryCollapsed = !_telemetryKeepExpanded;
        }
        if (SessionState.PendingNewChat)
        {
            SessionState.ConsumePendingNewChat();
            await OnNewChatRequestedAsync();
        }
        else if (SessionState.PendingCogitationId.HasValue)
        {
            var id = SessionState.PendingCogitationId.Value;
            SessionState.ConsumePendingCogitation();
            OnCogitationSelected(id);
        }
        else if (CogitationId.HasValue && SessionState.CurrentUser != null)
        {
            // Restore the cogitation from the URL after a page refresh.
            OnCogitationSelected(CogitationId.Value);
        }
        // else: soul not discovered yet on this fresh circuit (async bridge attestation still in
        // flight) — leave _cogitationId unset so OnSessionChanged's retry (below) picks this up
        // once SessionState.CurrentUser becomes available, instead of silently landing on blank.

        await InitTabsAsync();
        _ = StartTelemetryLoopAsync();
    }

    private async Task StartTelemetryLoopAsync()
    {
        _metricsLoopTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(_metricsCts.Token))
            {
                try
                {
                    var userId = BridgeUserId();
                    if (userId != null)
                    {
                        var metrics = await MetricsClient.GetAllMetricsAsync(userId);
                        if (metrics.Count > 0)
                        {
                            _nodeMetrics = metrics;

                            // Coalesce telemetry renders so we don't flood the Blazor dispatcher
                            // during heavy token streaming, but still refresh during long
                            // cogitation pauses where no token renders happen.
                            if (Interlocked.CompareExchange(ref _metricsRenderPending, 1, 0) == 0)
                            {
                                _ = InvokeAsync(FlushTelemetryRender);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, _metricsCts.Token);

        await Task.Yield();
    }

    private void FlushTelemetryRender()
    {
        Interlocked.Exchange(ref _metricsRenderPending, 0);

        // Sync the tick bar animation to the actual observed interval between metrics renders
        // so it reaches ~100% right as the next telemetry payload arrives, instead of drifting
        // against the free-running CSS animation.
        var now = DateTime.UtcNow;
        if (_lastMetricsRenderTime.HasValue)
        {
            var elapsedMs = (now - _lastMetricsRenderTime.Value).TotalMilliseconds;
            var clampedMs = Math.Clamp(elapsedMs, 1500, 4000);
            _tickDuration = $"{clampedMs:F0}ms";
        }
        _lastMetricsRenderTime = now;
        _telemetryTickKey++;

        StateHasChanged();
    }

    private void OnBridgeSoulStatusChanged(string sessionKey)
    {
        // Per-circuit verification key (§12): "circuit-{token}-{userId}".
        if (sessionKey != $"circuit-{SessionState.SessionToken}-{SessionState.CurrentUser?.Id}") return;
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnDirectBridgeRegistered(string userId)
    {
        if (userId != SessionState.CurrentUser?.Id.ToString()) return;
        _ = InvokeAsync(async () =>
        {
            if (_waitingForBridge && _agent == null)
            {
                _waitingForBridge = false;
                await InitAgentAsync();
            }
            else
            {
                // Daemon connected after init was already running or finished — re-render so
                // SoulVerified flips to true and the UI unlocks.
                StateHasChanged();
            }

            // The explorer restore may have run before the bridge was verified and failed to load
            // the tree (LoadExplorerTreeAsync needs BridgeUserId). Re-attempt now if appropriate.
            if (!_explorerCollapsed && GetExplorerActiveProject() is { } p && _explorerLoadedForPath != p.Path)
                await LoadExplorerTreeAsync();

            await RefreshTerminalProjectsAsync();
            await RefreshTerminalBridgeStatusAsync();
        });
    }

    private void OnUnseenVigilCountChanged() => _ = InvokeAsync(StateHasChanged);

    private void OnActiveProjectChanged() => _ = InvokeAsync(async () =>
    {
        // A live PTY session is scoped to a specific node/cwd; drop it when the project changes.
        if (_terminalMode == TerminalMode.Pty)
        {
            await DisposePtyAsync();
            _terminalMode = TerminalMode.QuickExec;
        }
        StateHasChanged();
    });

    private void OnNodesChanged(string userId)
    {
        if (userId != SessionState.CurrentUser?.Id.ToString()) return;
        _ = InvokeAsync(async () =>
        {
            if (_cogitationId.HasValue && _cogitationOriginNodeId != null)
            {
                var nowOffline = !IsCogitationContentAvailable();
                if (nowOffline != _cogitationOffline)
                {
                    _cogitationOffline = nowOffline;
                    if (!_cogitationOffline)
                    {
                        // Node came back online: reload the conversation content.
                        OnCogitationSelected(_cogitationId.Value);
                        return;
                    }
                }
            }
            StateHasChanged();
            await RefreshTerminalBridgeStatusAsync();
        });
    }

    private void OnSourceChanged() => _ = InvokeAsync(async () =>
    {
        StateHasChanged();
        // Auto-reinit when model/source changes while a session is already active
        if (_agent != null &&
            SessionState.CurrentUser != null &&
            CanInit() &&
            (SessionState.SelectedModelSource != _agentSource || SessionState.SelectedModel != _agentModel))
        {
            CancelActiveStreaming();
            ResetChatState();
            await ApplyFocusedFolderDefaultsAsync();
            if (WaitForBridgeIfNeeded()) return;
            await InitAgentAsync();
        }
    });

    private async void OnSessionChanged()
    {
        var newUserId     = SessionState.CurrentUser?.Id;
        var newSubAgentId = SessionState.ActiveSubAgent?.Id;
        bool userChanged  = newUserId     != _lastUserId;
        bool agentChanged = newSubAgentId != _lastSubAgentId;

        if (!userChanged && !agentChanged)
        {
            await InvokeAsync(StateHasChanged);
            return;
        }

        var previousUserId = _lastUserId;
        _lastUserId     = newUserId;
        _lastSubAgentId = newSubAgentId;

        if (userChanged && newUserId != null)
        {
            _telemetryKeepExpanded = await UserService.GetKeepTelemetryExpandedAsync(newUserId);
            _telemetryCollapsed = !_telemetryKeepExpanded;
            await InitTabsAsync();

            // The soul wasn't known yet when OnInitializedAsync ran (async bridge discovery still in
            // flight on a fresh page load/refresh), so the URL-restore branch there was skipped. Now
            // that the soul is known, restore it instead of falling through to the wipe below —
            // otherwise every hard refresh of a cogitation URL lands on a blank "awaiting directive".
            if (previousUserId == null && CogitationId.HasValue && !_cogitationId.HasValue)
            {
                OnCogitationSelected(CogitationId.Value);
                return;
            }
        }

        await InvokeAsync(() =>
        {
            DetachFromRun();
            _agent            = null;
            _session          = null;
            _currentManifest.Clear();
            _messages.Clear();
            _initLog.Clear();
            _initError        = null;
            _cogitationId     = null;
            _cogitationTitled = false;
            _historyLoaded    = false;
            _historyInjected  = false;
            SessionState.ActiveCogitationId = null;
            if (userChanged)
                ResetTerminalState();
            StateHasChanged();
        });
    }

    private void OnNewChatRequested() => _ = InvokeAsync(OnNewChatRequestedAsync);

    private async Task OnNewChatRequestedAsync()
    {
        // Fast path: a healthy session is already live on the same channel/model/agent, so reuse the
        // agent and just open a fresh thread. Avoids re-probing the channel and reloading every
        // bridge/MCP tool — the slow "init bar" the user sees on /clear. Must read these BEFORE
        // DetachFromRun()/ResetChatState() clear _router/_agent below.
        var reusableAgent  = TryReuseAgentForNewChat();
        var reusableRouter = reusableAgent != null ? _router : null;

        DetachFromRun();
        ResetChatState();
        ResetTerminalState();
        SessionState.ActiveProject = null;   // new cogitation starts with no project selected
        _explorerRoot = [];
        _explorerLoadedForPath = null;
        await SaveTerminalStateAsync();
        await ApplyFocusedFolderDefaultsAsync();

        // Deliberately not in ResetChatState(): that method is also called by OnCogitationSelected,
        // which runs on a hard page refresh to restore the SAME cogitation from the URL — collapsing
        // the explorer there would wipe it right back out on every reload. A genuinely new cogitation
        // is the only case that should drop the whole explorer view (tree + open file).
        _explorerCollapsed = true;
        await CloseExplorerViewer();   // also persists the collapsed flag above via SaveExplorerStateAsync
        SyncChatUrl(null);
        StateHasChanged();

        if (reusableAgent != null && reusableRouter != null)
        {
            await StartFreshSessionAsync(reusableAgent, reusableRouter);
            return;
        }

        if (SessionState.CurrentUser != null && CanInit())
        {
            if (WaitForBridgeIfNeeded()) return;
            await InitAgentAsync();
        }
    }

    // Returns the live agent if the current selection matches the running session and it
    // is healthy — meaning a new chat needs only a fresh thread, not a full rebuild.
    // Must be called BEFORE ResetChatState() clears the live-session fields.
    private AIAgent? TryReuseAgentForNewChat()
    {
        if (_agent == null || _initError != null || SessionState.CurrentUser == null || !CanInit())
            return null;

        // The tools on this agent were built with callbacks bound to _router, which a background
        // run for the cogitation we're leaving may still hold (Registry.StartRun retargets that very
        // router instance). Reusing this agent for an unrelated new chat while that run is still live
        // would either hand SendAsync a null router (StartRun would NRE deref'ing it) or, worse,
        // retarget the shared router mid-flight and splice this agent's callbacks into the wrong
        // cogitation's reply. Force a full rebuild whenever that run is still active.
        if (_cogitationId.HasValue && Registry.IsActive(_cogitationId.Value))
            return null;

        var subAgent   = SessionState.ActiveSubAgent;
        var sourceName = subAgent?.ModelSourceName ?? SessionState.SelectedModelSource;
        var modelId    = subAgent?.ModelId         ?? SessionState.SelectedModel;

        return _agentSource == sourceName && _agentModel == modelId && _lastSubAgentId == subAgent?.Id
            ? _agent
            : null;
    }

    private void OpenVigilFromChat()
    {
        var lastUserMsg  = _messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var lastAgentMsg = _messages.LastOrDefault(m => m.Role == "assistant")?.Content ?? "";

        var prefill = string.IsNullOrWhiteSpace(lastAgentMsg)
            ? lastUserMsg
            : $"Continue the following cogitation. Last user directive: {lastUserMsg[..Math.Min(200, lastUserMsg.Length)]}\n\nLast agent response: {lastAgentMsg[..Math.Min(300, lastAgentMsg.Length)]}…\n\nContinue from where we left off.";

        SessionState.OpenVigilModal(prefill.Trim(), cogitationId: _cogitationId);
    }

    public void Dispose()
    {
        SessionState.OnChange                  -= OnSessionChanged;
        SessionState.SourceChanged             -= OnSourceChanged;
        SessionState.NewChatRequested          -= OnNewChatRequested;
        SessionState.CogitationSelected        -= OnCogitationSelected;
        SessionState.ActiveSubAgentUpdated     -= OnActiveSubAgentUpdated;
        SessionState.ActiveProjectChanged      -= OnActiveProjectChanged;
        SessionState.ToolSettingsChanged       -= OnToolSettingsChanged;
        SessionState.UnseenVigilCountChanged   -= OnUnseenVigilCountChanged;
        SessionState.HiveCogitationUpdated     -= OnHiveCogitationUpdated;
        SessionState.OpenTabsChanged           -= OnOpenTabsChanged;
        SessionState.CogitationsChanged        -= OnCogitationsChangedForTabs;
        Orchestrator.OnHiveGatePending         -= OnHiveGatePending;
        Orchestrator.OnHiveGateResolved        -= OnHiveGateResolved;
        Orchestrator.OnHiveMemberGatePending   -= OnHiveMemberGatePending;
        Orchestrator.OnHiveMemberGateResolved  -= OnHiveMemberGateResolved;
        Orchestrator.OnHiveRunStateChanged     -= OnHiveRunStateChanged;
        BridgeRegistry.DirectBridgeRegistered  -= OnDirectBridgeRegistered;
        BridgeRegistry.SoulStatusChanged       -= OnBridgeSoulStatusChanged;
        BridgeRegistry.NodesChanged            -= OnNodesChanged;
        Registry.RunsChanged                   -= OnRegistryRunChanged;
        ProjectFiles.ApprovalPendingChanged    -= OnProjectFilesApprovalPending;
        DetachFromRun();
        _ = DisposePtyAsync();
        _voxRef?.Dispose();
        _pickerCts?.Cancel();
        _pickerCts?.Dispose();
        _pickerRef?.Dispose();
        _terminalInputDotNetRef?.Dispose();
        _ptyDotNetRef?.Dispose();

        _metricsCts.Cancel();
        _metricsCts.Dispose();
    }

    private void ToggleTelemetryNode(string nodeId)
    {
        if (!_collapsedTelemetryNodes.Remove(nodeId)) _collapsedTelemetryNodes.Add(nodeId);
    }

    private static string FormatSysMem(BridgeMetrics m)
    {
        if (m.SystemMemoryUsedMb is not { } used || m.SystemMemoryTotalMb is not { } total || total <= 0)
            return "--";
        return $"{used:F0}/{total:F0} MB ({used / total * 100:F0}%)";
    }

    private static string FormatBandwidth(BridgeMetrics m)
    {
        if (m.MemoryBandwidthGbps is { } bw)
            return $"{bw:F2} GB/s";
        if (!string.IsNullOrWhiteSpace(m.BandwidthSource))
            return m.BandwidthSource!;
        return "--";
    }

    private void SetTelemetryKeepExpanded(bool keepExpanded)
    {
        _telemetryKeepExpanded = keepExpanded;
        if (_telemetryKeepExpanded)
            _telemetryCollapsed = false;

        var userId = BridgeUserId();
        if (userId != null)
            _ = UserService.SaveKeepTelemetryExpandedAsync(userId, _telemetryKeepExpanded);
    }

    private static string TodoGlyph(string status) => status switch
    {
        "completed"   => "▣",
        "in_progress" => "◈",
        _             => "▢"
    };
}
