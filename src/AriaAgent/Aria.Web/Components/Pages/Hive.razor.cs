using Aria.Web.Data;
using Aria.Web.Helpers;
using Aria.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Pages;

public partial class Hive
{
    [Inject] public ILogger<Hive> Logger { get; set; } = default!;

    [Parameter] public int? InitialCollectiveId { get; set; }

    // ── State ──────────────────────────────────────────────────────────────────
    public int?                    _selectedCollectiveId;
    public AgentCollective?        _collective;
    public List<AgentCollective>   _collectives   = [];
    public List<CollectiveMember>  _members       = [];
    public List<CollectiveTask>    _tasks         = [];
    public List<CollectiveEvent>   _events        = [];
    public readonly HashSet<int>   _expandedEvents = [];

    public void ToggleEventExpand(int eventId)
    {
        if (!_expandedEvents.Remove(eventId))
            _expandedEvents.Add(eventId);
    }

    // Config edit buffers
    public string  _editName          = "";
    public string  _editObjective     = "";
    public int     _editMaxRounds     = 6;
    public string? _editOvermindSource;
    public string? _editOvermindModel;
    public int?    _editOvermindSubAgentId;
    public bool    _configSaved              = false;
    public bool    _editRequiresHumanApproval = false;
    public bool    _editAllowProjectTools     = false;
    public CollectiveBehavior _editBehavior  = CollectiveBehavior.HiveMind;
    public string  _editSynapseMemory        = "";
    public bool    _synapseSaved             = false;

    // Agent roster
    public List<SubAgent>        _allAgents      = [];
    public List<Aria.Agent.ModelSource> _availableSources = [];

    // Drawers
    public CollectiveMember?     _selectedDrone;
    public bool                  _showOvermindDrawer;
    public bool                  _timelineCollapsed = true;

    // Edge insert menu (positioned in fixed screen coords)
    public int?    _insertMenuMemberId;
    public double  _insertMenuX;
    public double  _insertMenuY;
    public string? _insertMenuNodeType;  // "gate" when clicking existing gate node, null for "+" button

    // Transform editor
    public MemberEdgeNode? _editingTransform;
    public string          _editTransformTemplate = "";

    // Condition editor
    public MemberEdgeNode? _editingCondition;
    public string          _editConditionMode   = "contains";
    public string          _editConditionValue  = "";
    public bool            _editConditionNegate;

    // Node hover tooltip
    public bool              _tooltipVisible;
    public double            _tooltipX;
    public double            _tooltipY;
    public CollectiveMember? _tooltipMember;
    public string?           _tooltipNodeType;  // "gate" or "transform"
    public string?           _tooltipExtra;

    // Canvas pan/zoom (driven by JS)
    public ElementReference _canvasRef;
    public bool _canvasJsInit;

    // Gate tracking
    public Dictionary<int, string?> _pendingGateMembers    = [];  // memberId → pendingContent
    public int                      _activeCogId;
    public bool                     _collectiveLevelGatePending;

    // JS interop ref for drag callbacks
    private DotNetObjectReference<Hive>? _dotNetRef;

    // Cogitate — creating the chat cogitation before navigating (Hive.Cogitation.razor.cs)
    public bool    _startingHiveChat;

    // Poll timer
    private Timer? _pollTimer;

    // ── Computed ──────────────────────────────────────────────────────────────

    public bool IsRunning => _selectedCollectiveId.HasValue && Orchestrator.IsRunning(_selectedCollectiveId.Value);

    public bool IsContentOffline =>
        _collective != null &&
        !string.IsNullOrEmpty(_collective.OriginNodeId) &&
        (SessionState.CurrentUser == null ||
         !BridgeRegistry.GetNodes(SessionState.CurrentUser.Id).Any(n => n.NodeId == _collective.OriginNodeId));

    public bool CanStart =>
        _collective != null &&
        !IsRunning &&
        !IsContentOffline &&
        !string.IsNullOrWhiteSpace(_collective.Objective) &&
        _members.Count > 0 &&
        (!string.IsNullOrEmpty(_collective.OvermindSourceName) || !string.IsNullOrEmpty(SessionState.SelectedModelSource)) &&
        _collective.Status != CollectiveStatus.Completed;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override async Task OnInitializedAsync()
    {
        Orchestrator.OnCollectiveChanged       += OnOrchestratorChanged;
        SessionState.OnChange                  += OnSessionChanged;
        Orchestrator.OnHiveMemberGatePending   += OnMemberGatePending;
        Orchestrator.OnHiveMemberGateResolved  += OnMemberGateResolved;
        Orchestrator.OnHiveGatePending         += OnCollectiveGatePending;
        Orchestrator.OnHiveGateResolved        += OnCollectiveGateResolved;
        Orchestrator.OnHiveRunStateChanged     += OnRunStateChanged;
        _dotNetRef = DotNetObjectReference.Create(this);

        if (SessionState.CurrentUser != null)
        {
            await LoadUserDataAsync();
            if (InitialCollectiveId.HasValue)
                await SelectCollective(InitialCollectiveId.Value);
        }

        // Poll every 2s while running
        _pollTimer = new Timer(async _ =>
        {
            if (_selectedCollectiveId.HasValue && (IsRunning || _collective?.Status == CollectiveStatus.Planning))
                await InvokeAsync(RefreshAsync);
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Canvas torn down (hive deleted / deselected): the normal wrap leaves the DOM and is replaced by a
        // fresh element on the next create. Clear the init flag so that new element gets wired up again —
        // otherwise delete→create leaves the pan/zoom + drag handlers bound to the old, detached node.
        if (_collective == null)
        {
            _canvasJsInit = false;
            return;
        }

        if (_collective != null && !_canvasJsInit)
        {
            Logger.LogInformation("[HiveUI] OnAfterRenderAsync init start");
            _canvasJsInit = true;
            try
            {
                await JS.InvokeVoidAsync("ariaInterop.initHiveCanvas", ".hv-canvas-wrap");
                await JS.InvokeVoidAsync("ariaInterop.initDragNodes", ".hv-canvas-wrap", _dotNetRef);
                Logger.LogInformation("[HiveUI] OnAfterRenderAsync init success");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[HiveUI] OnAfterRenderAsync init failed");
                _canvasJsInit = false;
            }
        }
    }

    public void Dispose()
    {
        Orchestrator.OnCollectiveChanged      -= OnOrchestratorChanged;
        SessionState.OnChange                 -= OnSessionChanged;
        Orchestrator.OnHiveMemberGatePending  -= OnMemberGatePending;
        Orchestrator.OnHiveMemberGateResolved -= OnMemberGateResolved;
        Orchestrator.OnHiveGatePending        -= OnCollectiveGatePending;
        Orchestrator.OnHiveGateResolved       -= OnCollectiveGateResolved;
        Orchestrator.OnHiveRunStateChanged    -= OnRunStateChanged;
        _dotNetRef?.Dispose();
        _pollTimer?.Dispose();
    }

    private async void OnOrchestratorChanged(int collectiveId)
    {
        if (collectiveId == _selectedCollectiveId)
            await InvokeAsync(RefreshAsync);
    }

    // Live run-state tick (phase / drone state changed) — just re-render the canvas; no DB reload.
    private async void OnRunStateChanged(int collectiveId)
    {
        if (collectiveId == _selectedCollectiveId)
            await InvokeAsync(StateHasChanged);
    }

    private async void OnSessionChanged()
    {
        if (SessionState.CurrentUser != null)
            await InvokeAsync(async () =>
            {
                await LoadUserDataAsync();
                // On a refresh of /hive/{id}, the soul connects after init — restore the deep-linked
                // collective once it's available (only if nothing is open yet).
                if (InitialCollectiveId.HasValue && _selectedCollectiveId == null)
                    await SelectCollective(InitialCollectiveId.Value);
                StateHasChanged();
            });
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    public async Task LoadUserDataAsync()
    {
        if (SessionState.CurrentUser == null) return;
        _collectives      = await CollectiveService.GetListAsync(SessionState.CurrentUser.Id);
        _allAgents        = await SubAgentService.GetForUserAsync(SessionState.CurrentUser.Id);
        _availableSources = AgentService.GetSourcesForUser(SessionState.CurrentUser.Id).ToList();
    }

    public async Task RefreshAsync()
    {
        if (!_selectedCollectiveId.HasValue) return;
        _collective = await CollectiveService.GetAsync(_selectedCollectiveId.Value);
        if (_collective == null) return;
        _members = await CollectiveService.GetMembersAsync(_selectedCollectiveId.Value);
        _tasks   = await CollectiveService.GetTasksAsync(_selectedCollectiveId.Value);
        _events  = await CollectiveService.GetEventsAsync(_selectedCollectiveId.Value);
        if (SessionState.CurrentUser != null)
            _collectives = await CollectiveService.GetListAsync(SessionState.CurrentUser.Id);

        // Restore gate state if we navigated back while a run was in progress
        if (_activeCogId == 0)
        {
            var runCogId = _tasks.FirstOrDefault(t => t.CogitationId.HasValue)?.CogitationId;
            if (runCogId.HasValue) _activeCogId = runCogId.Value;
        }
        if (_activeCogId > 0)
        {
            _collectiveLevelGatePending = Orchestrator.HasPendingGate(_activeCogId);
            foreach (var m in _members)
                if (Orchestrator.HasPendingMemberGate(_activeCogId, m.Id))
                    _pendingGateMembers.TryAdd(m.Id, null);
        }

        StateHasChanged();
    }

    // ── Collective management ─────────────────────────────────────────────────

    public async Task CreateCollectiveAsync()
    {
        if (SessionState.CurrentUser == null) return;
        var c = await CollectiveService.CreateAsync(SessionState.CurrentUser.Id, "New Collective");
        _collectives = await CollectiveService.GetListAsync(SessionState.CurrentUser.Id);
        await SelectCollective(c.Id);
    }

    // Navigate only when the target differs from the current URL — during server prerender,
    // navigating to the same URL becomes an HTTP self-redirect loop (ERR_TOO_MANY_REDIRECTS).
    private void NavReplaceIfChanged(string target)
    {
        if (!string.Equals(Nav.Uri, Nav.ToAbsoluteUri(target).ToString(), StringComparison.OrdinalIgnoreCase))
            Nav.NavigateTo(target, forceLoad: false, replace: true);
    }

    public async Task SelectCollective(int id)
    {
        _selectedCollectiveId = id;
        _selectedDrone        = null;
        // Reflect the open collective in the URL so a page refresh restores it.
        NavReplaceIfChanged($"/hive/{id}");
        await RefreshAsync();
        // Populate edit buffers
        if (_collective != null)
        {
            _editName                    = _collective.Name;
            _editObjective               = _collective.Objective;
            _editMaxRounds               = _collective.MaxRounds;
            _editOvermindSource          = _collective.OvermindSourceName;
            _editOvermindModel           = _collective.OvermindModelId;
            _editOvermindSubAgentId      = _collective.OvermindSubAgentId;
            _editRequiresHumanApproval   = _collective.RequiresHumanApproval;
            _editAllowProjectTools       = _collective.AllowProjectTools;
            _editBehavior                = _collective.Behavior;
            _editSynapseMemory           = _collective.SynapseMemory ?? "";
            EnsureExplicitOvermindSelection();
        }
    }

    // The Hive requires an explicit Overmind channel + model rather than inheriting the chat's
    // "default", so every collective runs on a known model. A collective that has never chosen one
    // is seeded here from the current effective (default) selection — a concrete value the user sees
    // pre-filled and can confirm or change. PersistConfigAsync (called before Start) writes it back.
    private void EnsureExplicitOvermindSelection()
    {
        if (string.IsNullOrEmpty(_editOvermindSource))
            _editOvermindSource = OvermindEffectiveSource;   // collective's own, else session default

        if (string.IsNullOrEmpty(_editOvermindSource)) return;   // no channels connected yet

        var src = _availableSources.FirstOrDefault(s => s.Name == _editOvermindSource);
        if (src == null || src.Models.Count == 0) { _editOvermindModel = null; return; }

        if (string.IsNullOrEmpty(_editOvermindModel) || !src.Models.Contains(_editOvermindModel))
            _editOvermindModel = !string.IsNullOrEmpty(SessionState.SelectedModel) && src.Models.Contains(SessionState.SelectedModel)
                ? SessionState.SelectedModel
                : src.Models[0];
    }

    public async Task DeleteCollectiveAsync(int id)
    {
        if (_selectedCollectiveId == id)
        {
            _selectedCollectiveId = null;
            _collective = null;
            _members = [];
            _tasks = [];
            _events = [];
            NavReplaceIfChanged("/hive");
        }
        await CollectiveService.DeleteAsync(id);
        if (SessionState.CurrentUser != null)
            _collectives = await CollectiveService.GetListAsync(SessionState.CurrentUser.Id);
    }

    // Persist config + reload, WITHOUT the "saved" toast delay. Used by Start so it doesn't wait 1.5s.
    public async Task PersistConfigAsync()
    {
        if (_collective == null) return;
        await CollectiveService.UpdateConfigAsync(
            _collective.Id,
            _editName, _editObjective,
            _editOvermindSubAgentId,
            _editOvermindSource, _editOvermindModel,
            _editMaxRounds, _editRequiresHumanApproval, _editBehavior,
            _editAllowProjectTools);
        _collective = await CollectiveService.GetAsync(_collective.Id);
    }

    // Checkbox in the CONFIGURATION panel — persists immediately like every other config field.
    public async Task OnAllowProjectToolsChanged(bool v)
    {
        _editAllowProjectTools = v;
        await SaveConfigAsync();
    }

    // Live-updates the name everywhere it's displayed as the user types (DebouncedInput already
    // debounces the calls into this). Actual DB persistence still happens on blur via SaveConfigAsync.
    public void OnNameChanged(string v)
    {
        _editName = v;
        if (_collective != null)
        {
            _collective.Name = v;
            var row = _collectives.FirstOrDefault(c => c.Id == _collective.Id);
            if (row != null) row.Name = v;
            CollectiveService.NotifyRenamed(_collective.Id, v);
        }
        StateHasChanged();
    }

    public async Task SaveConfigAsync()
    {
        if (_collective == null) return;
        _configSaved = false;
        await PersistConfigAsync();
        _configSaved = true;
        StateHasChanged();
        await Task.Delay(1500);
        _configSaved = false;
        StateHasChanged();
    }

    public async Task SaveSynapseMemoryAsync()
    {
        if (_collective == null) return;
        _synapseSaved = false;
        var memory = string.IsNullOrWhiteSpace(_editSynapseMemory) ? null : _editSynapseMemory.Trim();
        await CollectiveService.SaveSynapseMemoryAsync(_collective.Id, memory);
        if (_collective != null) _collective = await CollectiveService.GetAsync(_collective.Id);
        _editSynapseMemory = _collective?.SynapseMemory ?? "";
        _synapseSaved = true;
        StateHasChanged();
        await Task.Delay(1500);
        _synapseSaved = false;
        StateHasChanged();
    }

    // ── Orchestration controls ────────────────────────────────────────────────

    public bool _starting;
    public string? _startError;

    public async Task StartCollectiveAsync()
    {
        if (_collective == null || _starting) return;
        _starting = true;
        _startError = null;
        StateHasChanged();
        try
        {
            // Enforce an explicit Overmind channel + model — never launch on an inherited "default".
            EnsureExplicitOvermindSelection();
            var overSrc = _availableSources.FirstOrDefault(s => s.Name == _editOvermindSource);
            if (string.IsNullOrEmpty(_editOvermindSource))
            {
                _startError = "Select a CHANNEL for the Overmind before launching this collective.";
                return;
            }
            if (overSrc is { Models.Count: > 0 } && string.IsNullOrEmpty(_editOvermindModel))
            {
                _startError = "Select a MODEL for the Overmind before launching this collective.";
                return;
            }

            // Persist latest config before starting (no toast delay)
            await PersistConfigAsync();

            // Layer B pre-authorisation: seal this collective for its run window while the human is here,
            // so the Overmind and drones — some possibly on remote, unattended bridges — clear the node
            // gate. One grant scoped to hive:{id} is replicated to every node; the orchestrator stamps
            // that session on all headless sub-calls. A no-op when nothing is enforcing.
            if (SessionState.CurrentUser != null)
            {
                var objective = string.IsNullOrWhiteSpace(_collective.Objective)
                    ? "(no objective set)"
                    : (_collective.Objective.Length > 300 ? _collective.Objective[..300] + "…" : _collective.Objective);
                var label = string.IsNullOrWhiteSpace(_collective.Name) ? $"Collective #{_collective.Id}" : _collective.Name;

                var res = await ContextApproval.PreauthorizeHiveAsync(
                    SessionState.CurrentUser.Id, _collective.Id, _collective.OriginNodeId, objective, label);

                if (res.Outcome is Aria.Web.Services.ModelBridge.ContextApprovalService.VigilPreauthResult.Refused
                                or Aria.Web.Services.ModelBridge.ContextApprovalService.VigilPreauthResult.NodeUnavailable)
                {
                    _startError = res.Outcome == Aria.Web.Services.ModelBridge.ContextApprovalService.VigilPreauthResult.NodeUnavailable
                        ? "Could not reach a node to pre-authorise this collective — it was not started."
                        : "Collective pre-authorisation was refused on your node — it was not started.";
                    return;
                }

                // "This run only" seal: have the orchestrator revoke it when the run ends so the next
                // launch re-asks. A durationed seal (OneShot=false) is left to lapse on its own.
                Orchestrator.SetOneShotSeal(_collective.Id,
                    res.OneShot ? ((string, string?)?)(SessionState.CurrentUser.Id, res.ApprovalNode) : null);
            }

            await Orchestrator.StartCollectiveAsync(_collective.Id);
            await RefreshAsync();
        }
        finally
        {
            _starting = false;
            StateHasChanged();
        }
    }

    public async Task PauseCollectiveAsync()
    {
        if (_collective == null) return;
        await Orchestrator.PauseAsync(_collective.Id);
        await RefreshAsync();
    }

    public async Task ResetCollectiveAsync()
    {
        if (_collective == null) return;
        await Orchestrator.ResetAsync(_collective.Id);
        await RefreshAsync();
    }

    // ── Per-collective gate events ────────────────────────────────────────────

    private void OnCollectiveGatePending(int cogId)
    {
        _ = InvokeAsync(() =>
        {
            _activeCogId                = cogId;
            _collectiveLevelGatePending = true;
            StateHasChanged();
        });
    }

    private void OnCollectiveGateResolved(int cogId)
    {
        _ = InvokeAsync(() =>
        {
            _collectiveLevelGatePending = false;
            StateHasChanged();
        });
    }

    public void ApproveCollectiveGate()
    {
        _collectiveLevelGatePending = false;
        if (_activeCogId != 0)
            Orchestrator.ApproveHumanGate(_activeCogId);
    }

    public void OpenOvermindDrawer()
    {
        Logger.LogInformation("[HiveUI] OpenOvermindDrawer clicked");
        _showOvermindDrawer = true;
        StateHasChanged();
        Logger.LogInformation("[HiveUI] OpenOvermindDrawer StateHasChanged done");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public bool IsDroneActiveInCogitation(int memberId)
    {
        // Drone is active in cogitation mode if its most recent event is Dispatched (not yet DroneResult)
        var latest = _events
            .Where(e => e.ActorMemberId == memberId)
            .OrderByDescending(e => e.Id)
            .FirstOrDefault();
        return latest?.Type == CollectiveEventType.Dispatched;
    }

    public string GetDroneStatus(int memberId)
    {
        // Prefer the orchestrator's live in-memory run state — updates instantly per drone, no DB reload.
        if (_selectedCollectiveId.HasValue)
        {
            switch (Orchestrator.GetDroneState(_selectedCollectiveId.Value, memberId))
            {
                case CollectiveOrchestrator.DroneRunState.Running:      return "working";
                case CollectiveOrchestrator.DroneRunState.AwaitingGate: return "gate";
                case CollectiveOrchestrator.DroneRunState.Done:         return "done";
                case CollectiveOrchestrator.DroneRunState.Skipped:      return "skipped";
            }
        }

        // Fallback to persisted task status (post-run, or before live state exists).
        var memberTasks = _tasks.Where(t => t.AssignedMemberId == memberId).ToList();
        if (IsRunning)
        {
            if (memberTasks.Any(t => t.Status == CollectiveTaskStatus.Running))    return "working";
            if (memberTasks.Any(t => t.Status == CollectiveTaskStatus.Dispatched)) return "thinking";
        }
        if (memberTasks.Any(t => t.Status == CollectiveTaskStatus.Failed))     return "failed";
        if (memberTasks.Any(t => t.Status == CollectiveTaskStatus.Completed))  return "done";
        return "idle";
    }

    public string GetActorName(int? actorMemberId)
    {
        if (actorMemberId == null) return "OVERMIND";
        var m = _members.FirstOrDefault(m => m.Id == actorMemberId);
        return m?.SubAgent.DisplayName.ToUpperInvariant() ?? $"#{actorMemberId}";
    }

    public static string StatusClass(CollectiveStatus s) => s switch
    {
        CollectiveStatus.Running  or CollectiveStatus.Planning => "hv-status-running",
        CollectiveStatus.Completed                             => "hv-status-done",
        CollectiveStatus.Failed                                => "hv-status-failed",
        CollectiveStatus.Paused                                => "hv-status-paused",
        _                                                      => ""
    };

    // ── Node channel / model / bridge badges (rendered under each node on the canvas) ─────────
    // A drone inherits the collective's Overmind channel+model when it has no explicit override; the
    // Overmind in turn falls back to the session's currently selected channel/model. The bridge (node)
    // backing a channel is either the channel's pinned node or the user's default connected node.

    public string? OvermindEffectiveSource =>
        !string.IsNullOrEmpty(_collective?.OvermindSourceName) ? _collective!.OvermindSourceName
                                                               : SessionState.SelectedModelSource;

    public string? OvermindEffectiveModel =>
        !string.IsNullOrEmpty(_collective?.OvermindModelId) ? _collective!.OvermindModelId
                                                            : SessionState.SelectedModel;

    public string? EffectiveSource(CollectiveMember m) =>
        !string.IsNullOrEmpty(m.SubAgent.ModelSourceName) ? m.SubAgent.ModelSourceName : OvermindEffectiveSource;

    public string? EffectiveModel(CollectiveMember m) =>
        !string.IsNullOrEmpty(m.SubAgent.ModelId) ? m.SubAgent.ModelId : OvermindEffectiveModel;

    public string OvermindChannelLabel => string.IsNullOrEmpty(OvermindEffectiveSource) ? "default" : OvermindEffectiveSource!;
    public string OvermindModelLabel   => string.IsNullOrEmpty(OvermindEffectiveModel)  ? "default" : OvermindEffectiveModel!;
    public string ChannelLabel(CollectiveMember m) { var s = EffectiveSource(m); return string.IsNullOrEmpty(s) ? "default" : s!; }
    public string ModelLabel(CollectiveMember m)   { var s = EffectiveModel(m);  return string.IsNullOrEmpty(s) ? "default" : s!; }

    /// <summary>Friendly label of the bridge node backing a channel (pinned node, else the default node).</summary>
    public string BridgeLabelForSource(string? sourceName)
    {
        if (SessionState.CurrentUser == null) return "—";
        var nodes = BridgeRegistry.GetNodes(SessionState.CurrentUser.Id);
        if (nodes.Count == 0) return "offline";
        var src      = string.IsNullOrEmpty(sourceName) ? null : _availableSources.FirstOrDefault(s => s.Name == sourceName);
        var pinnedId = src?.BridgeNodeId;
        var node = !string.IsNullOrEmpty(pinnedId)
            ? nodes.FirstOrDefault(n => n.NodeId == pinnedId)
            : nodes.FirstOrDefault();
        if (node == null) return string.IsNullOrEmpty(pinnedId) ? "—" : ShortId(pinnedId);
        return string.IsNullOrWhiteSpace(node.Label) ? ShortId(node.NodeId) : node.Label;
    }

    private static string ShortId(string id) => id.Length > 8 ? id[..8] : id;

    /// <summary>Truncate a badge label, appending an ellipsis; full value goes in the data-tip tooltip.</summary>
    public static string Trunc(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s[..max] + "…" : s);
}
