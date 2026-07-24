using Aria.Harness.Core;
using Aria.Harness.Governance;
using Aria.Harness.Tools;
using Aria.Tools;
using Aria.Web.Data;

namespace Aria.Web.Services.Chat;

public class UserSessionState
{
    /// <summary>Unique per-circuit (per browser tab) handle. Keys per-circuit soul verification (§12):
    /// a circuit is verified only after its OWN browser proves local-bridge control. Opaque token the
    /// browser echoes back during attestation; never a secret on its own.</summary>
    public string SessionToken { get; } = Guid.NewGuid().ToString("N");

/// <summary>Fired when the active user or active sub-agent changes — Chat.razor re-inits the agent.</summary>
    public event Action? OnChange;

    /// <summary>Fired when the user explicitly requests a new conversation.</summary>
    public event Action? NewChatRequested;

    /// <summary>Fired when the cogitation list should be refreshed (new created, one selected, or sub-agent changed).</summary>
    public event Action? CogitationsChanged;

    /// <summary>Fired when the user selects a past cogitation from the sidebar.</summary>
    public event Action<int>? CogitationSelected;

    /// <summary>Fired when the focused dossier changes.</summary>
    public event Action? FocusedFolderChanged;

    public int? FocusedFolderId { get; private set; }

    public void FocusFolder(int? folderId)
    {
        if (FocusedFolderId == folderId) return;
        FocusedFolderId = folderId;
        FocusedFolderChanged?.Invoke();
    }

    public int? PendingCogitationId { get; private set; }

    public void ConsumePendingCogitation() => PendingCogitationId = null;

    /// <summary>Fired when the selected model source changes — Chat.razor refreshes UI only, no re-init.</summary>
    public event Action? SourceChanged;

    /// <summary>Fired when the user explicitly changes tool or MCP server settings — Chat.razor does a
    /// soft re-init so the new tool list takes effect without losing the message history.</summary>
    public event Action? ToolSettingsChanged;
    public void NotifyToolSettingsChanged() => ToolSettingsChanged?.Invoke();

    /// <summary>Fired when the active sub-agent's config is updated in-place (tools, directives, colour).
    /// Chat.razor does a soft re-init: new agent session with updated tools, messages kept intact.</summary>
    public event Action<SubAgent>? ActiveSubAgentUpdated;

    /// <summary>Fired when session init (channel handshake, format probing) starts/stops. NavMenu
    /// subscribes so it can grey out "NEW COGITATION" while a probe is in flight — starting a second
    /// cogitation mid-probe would race the same channel/model handshake.</summary>
    public event Action? InitializingChanged;

    private bool _isSessionInitializing;
    public bool IsSessionInitializing
    {
        get => _isSessionInitializing;
        set
        {
            if (_isSessionInitializing == value) return;
            _isSessionInitializing = value;
            InitializingChanged?.Invoke();
        }
    }

    private User? _currentUser;
    public User? CurrentUser
    {
        get => _currentUser;
        set { _currentUser = value; _activeSubAgent = null; OnChange?.Invoke(); }
    }

    private SubAgent? _activeSubAgent;
    public SubAgent? ActiveSubAgent
    {
        get => _activeSubAgent;
        set
        {
            _activeSubAgent = value;
            OnChange?.Invoke();
            CogitationsChanged?.Invoke();
        }
    }

    private string? _selectedModelSource;
    public string? SelectedModelSource
    {
        get => _selectedModelSource;
        set { _selectedModelSource = value; SourceChanged?.Invoke(); }
    }

    private string? _selectedModel;
    public string? SelectedModel
    {
        get => _selectedModel;
        set { _selectedModel = value; SourceChanged?.Invoke(); }
    }

    // Tool state — loaded from DB by NavMenu, held here so Chat.razor can read on demand.
    private readonly Dictionary<string, bool>                       _toolEnabled = new();
    private readonly Dictionary<string, Dictionary<string, string>> _toolConfig  = new();

    /// <summary>Active agent-governance mode (restraint + approval strictness). Persisted per user
    /// via <see cref="UserToolService"/>; defaults to Balanced until loaded.</summary>
    public GovernanceMode Governance { get; set; } = GovernanceMode.Balanced;

    /// <summary>Per-session governance budget overrides, set via the "/governance budget …" chat
    /// command. Session-scoped only — never persisted; cleared by "/governance budget reset".</summary>
    public int? GovernanceBudgetToolCalls { get; set; }
    public int? GovernanceBudgetFileReads { get; set; }

    public bool HasGovernanceBudgetOverrides =>
        GovernanceBudgetToolCalls != null || GovernanceBudgetFileReads != null;

    /// <summary>Per-session auto-compaction threshold in tokens, set via the "/compact auto …" chat
    /// command. Null = default (<see cref="Aria.Harness.Context.AutoCompaction.DefaultThresholdTokens"/>),
    /// 0 = off. Session-scoped only — never persisted.</summary>
    public int? AutoCompactThreshold { get; set; }

    /// <summary>The active mode's policy with any per-session budget overrides layered on top.
    /// Recomputed on demand so a mode switch or override applies from the next turn.</summary>
    public GovernancePolicy EffectiveGovernancePolicy() =>
        GovernancePolicy.FromMode(Governance)
            .WithBudgetOverrides(GovernanceBudgetToolCalls, GovernanceBudgetFileReads);

    /// <summary>How aggressively the Noosphere Inscribe tool is used without an explicit user request.
    /// Persisted per user via <see cref="UserToolService"/>; defaults to ModelAuto until loaded.</summary>
    public AutoMemoryMode AutoMemory { get; set; } = AutoMemoryMode.ModelAuto;

    /// <summary>Turn interval for <see cref="AutoMemoryMode.Regular"/> — auto-inscribe fires every N exchanges.</summary>
    public int AutoMemoryInterval { get; set; } = 5;

    /// <summary>Whether the agent recalls memory from just the LLM node or fans out across all connected
    /// nodes. Persisted per user via <see cref="UserToolService"/>; defaults to AllNodes (memory is
    /// node-local, so a multi-node soul must fan out to reach memory on another machine).</summary>
    public RecallScope RecallScope { get; set; } = RecallScope.AllNodes;

    public bool IsToolEnabled(string id) =>
        _toolEnabled.TryGetValue(id, out var e) && e;

    public Dictionary<string, string> GetToolConfig(string id) =>
        _toolConfig.TryGetValue(id, out var c) ? c : [];

    public void SetToolState(string toolId, bool enabled, Dictionary<string, string> config)
    {
        _toolEnabled[toolId] = enabled;
        _toolConfig[toolId]  = config;
    }

    public void LoadToolStates(Dictionary<string, (bool Enabled, Dictionary<string, string> Config)> states)
    {
        _toolEnabled.Clear();
        _toolConfig.Clear();
        foreach (var (id, (enabled, cfg)) in states)
        {
            _toolEnabled[id] = enabled;
            _toolConfig[id]  = cfg;
        }
    }

    public List<ActiveToolConfig> GetEnabledTools()
    {
        var userId = CurrentUser?.Id.ToString();
        return _toolEnabled
            .Where(kv => kv.Value)
            .Select(kv =>
            {
                var cfg = new Dictionary<string, string>(_toolConfig.GetValueOrDefault(kv.Key) ?? []);
                if (userId is not null) cfg["_userId"] = userId;
                return new ActiveToolConfig(kv.Key, cfg);
            })
            .ToList();
    }

    /// <summary>
    /// Returns the sub-agent's enabled tools, pulling credentials from the soul's config.
    /// </summary>
    public List<ActiveToolConfig> GetEnabledToolsForSubAgent(SubAgent agent)
    {
        var userId = CurrentUser?.Id.ToString();
        return agent.ToolStates
            .Where(ts => ts.Enabled)
            .Select(ts =>
            {
                var cfg = new Dictionary<string, string>(_toolConfig.GetValueOrDefault(ts.ToolId) ?? []);
                if (userId is not null) cfg["_userId"] = userId;
                return new ActiveToolConfig(ts.ToolId, cfg);
            })
            .ToList();
    }

    /// <summary>
    /// Returns MCP servers the sub-agent has opted into (subset of soul's servers).
    /// </summary>
    public IEnumerable<McpServerConfig> GetMcpServersForSubAgent(SubAgent agent)
    {
        if (string.IsNullOrEmpty(agent.EnabledMcpNamesJson)) return [];
        List<string> names;
        try { names = System.Text.Json.JsonSerializer.Deserialize<List<string>>(agent.EnabledMcpNamesJson) ?? []; }
        catch { return []; }
        return McpServers.Where(s => names.Contains(s.Name));
    }

    // MCP server list — loaded from DB by NavMenu
    public List<McpServerConfig> McpServers { get; } = [];

    public void SetMcpServers(IEnumerable<McpServerConfig> servers)
    {
        McpServers.Clear();
        McpServers.AddRange(servers);
    }

    // ── Active project (chat "#" file picker scope) ───────────────────────
    // Ephemeral per-circuit selection of which declared Terminal project the "#" picker searches.
    // No default — the user must explicitly select a project; changes fire ActiveProjectChanged so
    // the Explorer, Terminal, and agent context stay in sync.

    // Bridge-authoritative project list. The web cannot edit this; it is refreshed from the node.
    private List<TerminalProject> _projects = [];

    /// <summary>Replaces the cached Terminal project list (normally fetched from the bridge).</summary>
    public void SetProjects(IEnumerable<TerminalProject> projects)
    {
        _projects = projects.Where(p => !string.IsNullOrWhiteSpace(p.Path)).ToList();
        ActiveProjectChanged?.Invoke();
    }

    /// <summary>The declared Terminal projects (name/path/description) owned by the bridge.
    /// Empty when the Terminal tool itself is disabled or no projects have been fetched yet —
    /// gates both the "#" file picker and the chat file Explorer, which have no other access path
    /// to project files.</summary>
    public List<TerminalProject> Projects =>
        IsToolEnabled("terminal") ? _projects : [];

    /// <summary>The project paths the bridge is allowed to read (gates the "#" picker file access).</summary>
    public string[] AllowedProjectPaths => Projects.Select(p => p.Path).ToArray();

    /// <summary>
    /// Node-approved session path expansions (Wave 5, "/scope add"), as last reported by the node.
    /// This is the SOFT copy used by the governance scope-lock's turn scope; the bridge remains the
    /// hard enforcer — a stale (expired) entry here still fails closed at the node. Refreshed whenever
    /// a "/scope" command runs.
    /// </summary>
    public List<string> SessionScopeExpansions { get; } = [];

    private TerminalProject? _activeProject;

    /// <summary>The currently selected Terminal project. Fires <see cref="ActiveProjectChanged"/>
    /// when the selection changes so the Explorer, Terminal, and agent context stay in sync.</summary>
    public TerminalProject? ActiveProject
    {
        get => _activeProject;
        set
        {
            if (ReferenceEquals(_activeProject, value)) return;
            if (_activeProject?.Path == value?.Path) return;
            _activeProject = value;
            ActiveProjectChanged?.Invoke();
        }
    }

    /// <summary>Fired when the active Terminal project changes.</summary>
    public event Action? ActiveProjectChanged;

    // Active cogitation — set by Chat.razor on session init/load
    public int? ActiveCogitationId { get; set; }

    public void SelectCogitation(int cogitationId)
    {
        ActiveCogitationId = cogitationId;
        if (CogitationSelected != null)
            CogitationSelected.Invoke(cogitationId);
        else
            PendingCogitationId = cogitationId; // Chat not mounted — it will consume on init
        CogitationsChanged?.Invoke();
    }

    public void NotifyCogitationsChanged() => CogitationsChanged?.Invoke();

    /// <summary>Updates the active sub-agent reference in-place and fires ActiveSubAgentUpdated
    /// without triggering a full re-init (OnChange is not fired).</summary>
    public void RefreshActiveSubAgent(SubAgent updated)
    {
        _activeSubAgent = updated;
        ActiveSubAgentUpdated?.Invoke(updated);
    }

    public bool PendingNewChat { get; private set; }

    public void RequestNewChat()
    {
        if (NewChatRequested == null)
            PendingNewChat = true; // Chat page not mounted yet — it will consume this on init
        else
            NewChatRequested.Invoke();
    }

    public void ConsumePendingNewChat()
    {
        PendingNewChat = false;
    }

    // ── Left-menu panel routing ───────────────────────────────────────────
    // Lets the chat "/" palette open a NavMenu section panel (e.g. "/tools", "/agents").
    public event Action<string>? OpenPanelRequested;
    public void RequestOpenPanel(string panel) => OpenPanelRequested?.Invoke(panel);

    // ── §12 initial bridge probe ──────────────────────────────────────────
    // True once this circuit's first local-bridge check (discovery + cached-code retry) has finished,
    // successfully or not. BridgeGatewayModal shows a loading state until then, so onboarding never
    // flashes while the probe is still in flight.
    public bool BridgeProbeCompleted { get; private set; }
    public event Action? BridgeProbeCompletedChanged;
    public void MarkBridgeProbeCompleted()
    {
        if (BridgeProbeCompleted) return;
        BridgeProbeCompleted = true;
        BridgeProbeCompletedChanged?.Invoke();
    }

    // ── §12 code-pairing unlock bridge ────────────────────────────────────
    // NavMenu owns the unlock+soul-select machinery; the BridgeGatewayModal (and NavMenu's own form)
    // both route a pasted session code through this handler so there's a single implementation.
    public Func<string, Task<(bool Ok, string? Error)>>? CodeUnlockHandler { get; set; }

    public Task<(bool Ok, string? Error)> TryCodeUnlockAsync(string code) =>
        CodeUnlockHandler?.Invoke(code) ?? Task.FromResult((false, (string?)"Unlock is not available right now."));

    // ── Vigil unseen count ────────────────────────────────────────────────

    public event Action? UnseenVigilCountChanged;
    public int UnseenVigilCount { get; private set; }

    public void SetUnseenVigilCount(int n)
    {
        if (n == UnseenVigilCount) return;
        UnseenVigilCount = n;
        UnseenVigilCountChanged?.Invoke();
    }

    // ── Vigil modal ───────────────────────────────────────────────────────

    public event Action? VigilModalChanged;

    public bool   VigilModalOpen          { get; private set; }
    public string VigilPrefillTask        { get; private set; } = "";
    public int?   VigilPrefillCogitationId { get; private set; }

    public void OpenVigilModal(string prefill = "", int? cogitationId = null)
    {
        VigilModalOpen             = true;
        VigilPrefillTask           = prefill;
        VigilPrefillCogitationId   = cogitationId;
        VigilModalChanged?.Invoke();
    }

    // ── Hive live cogitation ─────────────────────────────────────────────

    /// <summary>Fired when a Hive background run adds a message to the active cogitation.</summary>
    public event Action<int>? HiveCogitationUpdated;
    public void NotifyHiveCogitationUpdated(int cogitationId) =>
        HiveCogitationUpdated?.Invoke(cogitationId);

    /// <summary>Fired when a human-in-the-loop gate opens for a cogitation.</summary>
    public event Action<int>? HiveGatePending;
    public void NotifyHiveGatePending(int cogitationId) =>
        HiveGatePending?.Invoke(cogitationId);

    /// <summary>Fired when a gate closes (approved, cancelled, or timed out).</summary>
    public event Action<int>? HiveGateResolved;
    public void NotifyHiveGateResolved(int cogitationId) =>
        HiveGateResolved?.Invoke(cogitationId);

    public void CloseVigilModal()
    {
        VigilModalOpen             = false;
        VigilPrefillTask           = "";
        VigilPrefillCogitationId   = null;
        VigilModalChanged?.Invoke();
    }

    // ── Chat tab bar ─────────────────────────────────────────────────────
    // "Open tabs" is a session-visible list of cogitations shown at the top of the Chat UI,
    // distinct from the full history in the sidebar flyout. A cogitation joins this list the moment
    // it's opened (new, picked from the sidebar, or clicked from another tab) and stays until the
    // human closes its tab explicitly. Chat.razor persists the id list to localStorage; this class
    // only owns the in-memory ordering + which one is active (via SelectCogitation below).

    public event Action? OpenTabsChanged;

    private readonly List<int> _openTabIds = new();
    public IReadOnlyList<int> OpenTabIds => _openTabIds;

    public void OpenTab(int cogitationId)
    {
        if (_openTabIds.Contains(cogitationId)) return;
        _openTabIds.Add(cogitationId);
        OpenTabsChanged?.Invoke();
    }

    /// <summary>Removes a tab. If it was the active one, focuses its left neighbor (or, if it was
    /// the first tab, its new left-most neighbor) — mirrors browser-tab close behaviour. Returns
    /// true when no tabs remain, so the caller can fall back to a fresh "no cogitation" state.</summary>
    public bool CloseTab(int cogitationId)
    {
        var idx = _openTabIds.IndexOf(cogitationId);
        if (idx < 0) return false;

        var wasActive = ActiveCogitationId == cogitationId;
        _openTabIds.RemoveAt(idx);
        OpenTabsChanged?.Invoke();

        if (!wasActive) return false;
        if (_openTabIds.Count == 0) return true;

        var nextIdx = Math.Min(idx > 0 ? idx - 1 : 0, _openTabIds.Count - 1);
        SelectCogitation(_openTabIds[nextIdx]);
        return false;
    }

    /// <summary>Seeds the open-tab list from persisted state (localStorage) on first load. Does not
    /// change the active cogitation or fire per-tab open semantics.</summary>
    public void RestoreOpenTabs(IEnumerable<int> ids)
    {
        _openTabIds.Clear();
        _openTabIds.AddRange(ids.Distinct());
        OpenTabsChanged?.Invoke();
    }

}
