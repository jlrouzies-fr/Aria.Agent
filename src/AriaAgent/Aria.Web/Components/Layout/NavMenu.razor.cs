using System.Security.Cryptography;
using System.Text.Json;
using Aria.Agent;
using Aria.Tools;
using Aria.Web.Data;
using Aria.Web.Helpers;
using Aria.Web.Services.Chat;
using Aria.Web.Services;
using Aria.Web.Services.Tool;
using Aria.Web.Components.Pages;
using Aria.Web.Components.Shared;
using Markdig;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Layout;

public partial class NavMenu
{
    [Inject] internal AgentService     AgentService  { get; set; } = null!;
    [Inject] internal UserService      UserService   { get; set; } = null!;
    [Inject] internal UserToolService  ToolService   { get; set; } = null!;
    [Inject] internal BridgeMcpClient  McpClient     { get; set; } = null!;
    [Inject] internal UserSessionState SessionState  { get; set; } = null!;
    [Inject] internal NodeService      NodeService   { get; set; } = null!;
    [Inject] internal PendingEnrollmentService PendingEnrollments { get; set; } = null!;
    [Inject] internal CircuitAuthService CircuitAuth { get; set; } = null!;
    [Inject] internal IJSRuntime       JS            { get; set; } = null!;
    [Inject] internal NavigationManager  Nav               { get; set; } = null!;
    [Inject] internal CollectiveService  CollectiveService { get; set; } = null!;
    [Inject] internal ModelBridgeRegistry    BridgeRegistry  { get; set; } = null!;
    [Inject] internal ILogger<NavMenu>       Log             { get; set; } = null!;
    [Inject] internal BridgeCogitationClient BridgeClient    { get; set; } = null!;
    [Inject] internal ExchangeSessionService ExchangeService { get; set; } = null!;
    [Inject] internal BridgeMemoryClient     MemoryClient    { get; set; } = null!;
    [Inject] internal CogitationRunRegistry  RunRegistry     { get; set; } = null!;
    [Inject] internal CogitationFolderService FolderService  { get; set; } = null!;
    [Inject] internal TerminalClient         TerminalClient  { get; set; } = null!;

    internal List<User>       _users       = [];
    internal List<Cogitation> _cogitations = [];

    internal string? _activePanel;
    internal bool    _sidebarCollapsed;
    // INDEX renders as a centred modal (not the narrow flyout) — the catalogue is wide and
    // hard to read squeezed into a section-panel.
    internal bool    _indexModal;

    // ── §12 manual session-code unlock (fallback for insecure-context browsers) ──
    // The actual entry form lives in BridgeGatewayModal and the Chat centre; NavMenu only owns the
    // shared unlock handler (registered on SessionState) and the cached-code retry.
    internal bool _secureContext = true;   // assume secure until JS reports otherwise (avoids a flash)

    internal bool ShowChannelWarning =>
        SessionState.CurrentUser != null && SessionState.SelectedModelSource == null;

    internal DotNetObjectReference<NavMenu>? _dotnetRef;
    internal string? _lastAppliedTheme;

    // Vigil unseen notifications
    internal int    _unseenVigilCount = 0;
    internal Timer? _vigilPollTimer;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await ApplyThemeAsync();
        try
        {
            await JS.InvokeVoidAsync("ariaInterop.setChannelTooltip", ShowChannelWarning, "aria-channel-title");
        }
        catch { /* JS not ready yet */ }

        // Re-apply sidebar collapsed class after every render — Blazor's DOM diff on the SSR
        // layout resets <aside class="sidebar"> on each navigation, stripping sidebar-collapsed.
        if (!firstRender && _sidebarCollapsed)
        {
            try { await JS.InvokeVoidAsync("ariaInterop.initSidebarCollapse"); }
            catch { }
        }

        if (_skillEditing && !_skillEditorWired && _dotnetRef != null)
        {
            try
            {
                await JS.InvokeVoidAsync("ariaInterop.initSkillEditor", "skill-editor-textarea", _dotnetRef);
                await JS.InvokeVoidAsync("ariaInterop.focusElement", "skill-name-input");
                _skillEditorWired = true;
            }
            catch { }
        }

        // Skills preview (and any other nav markdown) may contain .math fences.
        try { await JS.InvokeVoidAsync("ariaInterop.typesetMath"); } catch { }

        if (firstRender)
        {
            _dotnetRef = DotNetObjectReference.Create(this);

            try
            {
                var stored = await JS.InvokeAsync<string?>("localStorage.getItem", "aria-sidebar-collapsed");
                _sidebarCollapsed = stored == "1";
                await JS.InvokeVoidAsync("ariaInterop.initSidebarCollapse");
                if (_sidebarCollapsed) StateHasChanged();
            }
            catch { }

            // Detect whether this page can do automatic loopback attestation. Insecure LAN contexts
            // (http://192.168.x.x) can't — they fall back to the manual session-code pairing form.
            try { _secureContext = await JS.InvokeAsync<bool>("ariaInterop.isSecureContext"); }
            catch { _secureContext = true; }

            // In the one-bridge = one-soul model, discover the current soul directly from the local
            // bridge. This replaces the old localStorage-based user restore (which leaked soul existence).
            await DiscoverAndSelectUserAsync();

            // If discovery didn't verify the circuit (no local bridge reachable — including secure
            // HTTPS pages where the browser blocks the loopback fetch), retry the session code this
            // tab cached at its last manual unlock. No-op when discovery already verified.
            await TryCachedUnlockAsync();

            // Probe over (verified or not) — lets the gateway modal leave its loading state.
            SessionState.MarkBridgeProbeCompleted();
            if (!_secureContext) StateHasChanged();   // surface the code form if still locked
        }
    }

    internal async Task ApplyThemeAsync()
    {
        var color = SessionState.ActiveSubAgent?.AccentColor;
        if (color == _lastAppliedTheme) return;
        try
        {
            _lastAppliedTheme = color;
            if (!string.IsNullOrEmpty(color))
                await JS.InvokeVoidAsync("ariaInterop.applyTheme", color);
            else
                await JS.InvokeVoidAsync("ariaInterop.clearTheme");
        }
        catch { _lastAppliedTheme = null; }
    }

    protected override async Task OnInitializedAsync()
    {
        // Verification defaults to FALSE (locked). The UI starts locked and unlocks only after
        // the daemon's ECDSA challenge-response passes — so the soul name never flashes early.

        SessionState.CogitationsChanged   += OnCogitationsChanged;
        SessionState.FocusedFolderChanged += OnFocusedFolderChanged;
        SessionState.OnChange             += OnSessionStateChanged;
        SessionState.SourceChanged        += OnSourceChangedNav;
        SessionState.InitializingChanged  += OnInitializingChangedNav;
        SessionState.VigilModalChanged    += OnVigilModalChanged;
        SessionState.OpenPanelRequested   += OnOpenPanelRequested;
        // NavMenu lives in the persistent layout (outside the Router's own render boundary), so a
        // route change alone doesn't re-render it — without this, Uri-based "active" checks (Noosphere,
        // WAR.PLANNER) go stale until something else happens to trigger a render.
        Nav.LocationChanged               += OnLocationChangedNav;
        ExchangeService.InviteReceived    += OnExchangeInviteReceived;
        ExchangeService.StatusChanged     += OnExchangeStatusChanged;
        BridgeRegistry.DirectBridgeRegistered   += OnDirectBridgeRegisteredNav;
        BridgeRegistry.DirectBridgeDisconnected += OnDirectBridgeDisconnectedNav;
        BridgeRegistry.SoulUnlinked             += OnSoulUnlinkedNav;
        BridgeRegistry.SoulRegistered           += OnSoulRegisteredNav;
        BridgeRegistry.SoulStatusChanged        += OnSoulStatusChangedNav;
        BridgeRegistry.NodesChanged             += OnNodesChangedNav;
        PendingEnrollments.Changed              += OnPendingEnrollmentsChanged;
        Services.Tool.UserToolService.ToolsChanged += OnToolsChangedNav;
        RunRegistry.RunsChanged                 += OnCogitationRunsChanged;
        RunRegistry.UnseenChanged                += OnCogitationUnseenChanged;
        CollectiveService.CollectiveRenamed      += OnCollectiveRenamed;
        SessionState.CodeUnlockHandler           = HandleCodeUnlockAsync;   // shared with BridgeGatewayModal

        _users = await UserService.GetUsersAsync();

        // Do NOT auto-select a user just because a bridge is connected somewhere. A bridge
        // connection is server-wide and may belong to a different machine; selecting it would
        // leak the soul's existence and leave the UI in a confusing "locked but selected" state.
        // Selection only happens from explicit user action or localStorage restore (below),
        // and is then validated by per-circuit bridge attestation before any sensitive data
        // is exposed.
        if (SessionState.CurrentUser != null)
            await LoadUserDataAsync(SessionState.CurrentUser.Id);

        if (SessionState.SelectedModelSource == null && _userSources.Count > 0)
        {
            var (defName, defModel) = GetDefaultSource();
            SessionState.SelectedModelSource = defName;
            SessionState.SelectedModel = defModel;
        }

        _vigilPollTimer = new Timer(async _ =>
        {
            if (SessionState.CurrentUser == null) return;
            var prev = _unseenVigilCount;
            await RefreshUnseenVigilsAsync(SessionState.CurrentUser.Id);
            if (_unseenVigilCount != prev)
                await InvokeAsync(StateHasChanged);
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        // Faster cadence than the vigil poll — Noosphere extraction usually finishes within a few
        // seconds, and the spinner should track that closely enough to feel live.
        _memoryPollTimer = new Timer(async _ =>
        {
            if (SessionState.CurrentUser == null) return;
            await RefreshMemoryProcessingAsync(SessionState.CurrentUser.Id.ToString());
        }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    internal async Task OpenVigilModalAsync()
    {
        SessionState.OpenVigilModal();
        if (SessionState.CurrentUser != null)
        {
            await CronSlotService.MarkAllSeenAsync(SessionState.CurrentUser.Id);
            _unseenVigilCount = 0;
            SessionState.SetUnseenVigilCount(0);
        }
    }

    internal async void OnVigilModalChanged() => await InvokeAsync(StateHasChanged);

    internal void OnVigilCogitation(int cogId)
    {
        SessionState.CloseVigilModal();
        SessionState.SelectCogitation(cogId);
    }

    internal async void OnSourceChangedNav() =>
        await InvokeAsync(StateHasChanged);

    internal async void OnInitializingChangedNav() =>
        await InvokeAsync(StateHasChanged);

    internal void OnLocationChangedNav(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e) =>
        InvokeAsync(StateHasChanged);

    internal async void OnCogitationsChanged()
    {
        if (SessionState.CurrentUser != null)
        {
            _cogitations = await CogitationService.GetListAsync(SessionState.CurrentUser.Id);
            _folders     = await FolderService.GetListAsync(SessionState.CurrentUser.Id);
            await InvokeAsync(StateHasChanged);
        }
    }

    internal async void OnFocusedFolderChanged() => await InvokeAsync(StateHasChanged);

    internal async Task SelectUserAsync(User user, bool skipAttest = false)
    {
        await LoadUserDataAsync(user.Id);
        SessionState.SelectedModelSource = user.LastModelSource;

        if (user.LastModelSource != null)
        {
            var savedModel = await UserService.GetSourcePreferenceAsync(user.Id, user.LastModelSource);
            var source = _userSources.FirstOrDefault(s => s.Name == user.LastModelSource);
            SessionState.SelectedModel = savedModel ?? source?.Models.FirstOrDefault();
        }
        else if (_userSources.Count > 0)
        {
            var (defName, defModel) = GetDefaultSource();
            SessionState.SelectedModelSource = defName;
            SessionState.SelectedModel = defModel;
        }

        var previousUserId = SessionState.CurrentUser?.Id;
        SessionState.CurrentUser = user;
        // No localStorage persistence: in the one-bridge = one-soul model the local bridge is the
        // source of identity. Remembering the soul in browser storage would leak its existence.
        try { await UserService.EnsureAvatarAsync(user); }
        catch { /* non-critical — avatar assigned on next login if column not yet present */ }

        // Leave hive detail pages only when switching to a DIFFERENT soul (the hive belonged to the
        // previous one). On initial load / refresh (same soul, or no previous), keep the deep link so
        // refreshing /hive/{id} restores the open collective.
        if (previousUserId != null && previousUserId != user.Id && Nav.Uri.Contains("/hive/"))
            Nav.NavigateTo("/");

        // Prove this circuit controls the newly-selected soul's local bridge (§12). Skipped when the
        // caller has already performed discovery/attestation (e.g. DiscoverAndSelectUserAsync).
        if (!skipAttest && _dotnetRef != null) await AttestCircuitAsync();
    }

    /// <summary>
    /// True when a cogitation's content is currently unreachable because its origin bridge node is offline.
    /// Legacy/server-stored cogitations are always considered available.
    /// </summary>
    internal bool IsCogitationOffline(Cogitation cog)
    {
        if (string.IsNullOrEmpty(cog.OriginNodeId)) return false;
        var userId = SessionState.CurrentUser?.Id.ToString();
        if (userId == null) return true;
        return !BridgeRegistry.GetNodes(userId).Any(n => n.NodeId == cog.OriginNodeId);
    }

    /// <summary>True while a cogitation is streaming in the background (possibly detached from Chat,
    /// e.g. the user navigated away or opened another cogitation).</summary>
    internal bool HasActiveRun(int cogitationId) => RunRegistry.IsActive(cogitationId);

    /// <summary>Drives the "// COGITATIONS" nav icon blink — true whenever any of this user's
    /// cogitations has a run streaming in the background, regardless of which one is open.</summary>
    internal bool HasAnyActiveCogitationRun =>
        SessionState.CurrentUser != null && RunRegistry.AnyActiveForUser(SessionState.CurrentUser.Id);

    /// <summary>Drives the green "unseen" dot next to "// COGITATIONS" — true while a background run
    /// finished without anyone watching. Mirrors the vigil unseen-count dot.</summary>
    internal bool HasUnseenCogitationCompletion =>
        SessionState.CurrentUser != null && RunRegistry.HasUnseenCompletions(SessionState.CurrentUser.Id);

    /// <summary>Drives the per-row unseen dot in the COGITATIONS panel, so it's clear which specific
    /// cogitation(s) finished unread rather than just that "something" did.</summary>
    internal bool IsCogitationUnseen(int cogitationId) =>
        SessionState.CurrentUser != null && RunRegistry.IsUnseen(SessionState.CurrentUser.Id, cogitationId);

    internal void OnCogitationUnseenChanged(string userId)
    {
        if (userId != SessionState.CurrentUser?.Id) return;
        _ = InvokeAsync(StateHasChanged);
    }

    internal void OnCogitationRunsChanged(string userId, int cogitationId)
    {
        if (userId != SessionState.CurrentUser?.Id) return;
        _ = InvokeAsync(StateHasChanged);
    }

    internal async Task LoadUserDataAsync(string userId)
    {
        // Drop any device/pending state from the previously-selected soul so it can't flash as if it
        // belonged to the new one. Reload immediately if the devices panel is the one on screen.
        _nodes = [];
        _pending = [];
        if (_activePanel == "devices") await LoadNodesAsync();

        var states = await ToolService.GetToolStatesAsync(userId);
        SessionState.LoadToolStates(states);
        SessionState.Governance = await ToolService.GetGovernanceModeAsync(userId);
        (SessionState.AutoMemory, SessionState.AutoMemoryInterval) = await ToolService.GetAutoMemorySettingsAsync(userId);
        SessionState.RecallScope = await ToolService.GetRecallScopeAsync(userId);
        SessionState.FleetApprovalRequired = await ToolService.GetFleetApprovalRequiredAsync(userId);
        await SyncTerminalAnchorsAsync(userId);
        _ = RefreshMemoryProcessingAsync(userId);

        _bridgeMcpServers = await McpClient.GetMcpInfosAsync(userId);
        SessionState.SetMcpServers(_bridgeMcpServers.Select(BridgeMcpClient.ToConfig));

        _cogitations     = await CogitationService.GetListAsync(userId);
        _folders         = await FolderService.GetListAsync(userId);
        _subAgents       = await SubAgentService.GetForUserAsync(userId);
        _skills          = await SkillService.GetForUserAsync(userId);
        _hiveCollectives = await CollectiveService.GetListAsync(userId);

        await RefreshOAuthStatusAsync();
        // Configured cloud providers now live on the bridge (key-custody). Empty until the bridge
        // connects; refreshed again once verification succeeds (see RefreshConfiguredProvidersAsync).
        await RefreshConfiguredProvidersAsync();
        _voxSettings = await VoxService.GetSettingsAsync(userId);

        await RefreshUserSourcesAsync(userId);
        await RefreshUnseenVigilsAsync(userId);
    }

    internal async Task RefreshUnseenVigilsAsync(string userId)
    {
        _unseenVigilCount = await CronSlotService.GetUnseenCompletedCountAsync(userId);
        SessionState.SetUnseenVigilCount(_unseenVigilCount);
    }

    internal async Task RefreshUserSourcesAsync(string userId)
    {
        // Warm the cache from the bridge, then take CUSTOM channels only for the local list — public
        // providers come from the catalog (AvailableModelSources), so including them here duplicates them.
        await LocalSourceService.GetForUserAsync(userId);
        _userLocalDbSources = LocalSourceService.GetCustomCached(userId);
        var localModelSources = _userLocalDbSources.Select(UserLocalSourceService.ToModelSource).ToList();
        AgentService.SetUserLocalSources(userId, localModelSources);
        _userSources = AgentService.GetSourcesForUser(userId).ToList();
    }

    // ── Cogitations ───────────────────────────────────────────────────────

    internal void SelectCogitation(Cogitation cog)
    {
        if (!SoulVerified) return;   // never open cogitation data without a verified bridge
        ClosePanel();

        // The list now shows cogitations from every agent/Hive — switch the active channel to match
        // this one's own agent so reopening it doesn't require a manual channel switch first.
        SessionState.ActiveSubAgent = cog.SubAgentId.HasValue
            ? _subAgents.FirstOrDefault(a => a.Id == cog.SubAgentId.Value)
            : null;

        SessionState.SelectCogitation(cog.Id);
        var onChat = Nav.Uri.TrimEnd('/').Equals(Nav.BaseUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        if (!onChat)
            Nav.NavigateTo("/");
    }

    internal async Task DeleteCogitation(Cogitation cog)
    {
        if (!string.IsNullOrEmpty(cog.OriginNodeId) && SessionState.CurrentUser is { } u)
        {
            var userId = u.Id.ToString();
            // Best-effort deletion on the bridge node that owns the content.
            _ = Task.Run(async () =>
            {
                await BridgeRegistry.SendLocalRestAsync(userId, "DELETE", $"/cogitations/{BridgeCogitationClient.BridgeId(cog.Id)}");
            });
        }
        if (SessionState.CurrentUser != null)
        {
            // A deleted cogitation can no longer be opened, so nothing would ever clear its unseen
            // flag or its background run — without this, the green dot lingers forever (the bug: it
            // stays lit even after every cogitation is gone).
            RunRegistry.Cancel(cog.Id);
            RunRegistry.MarkSeen(SessionState.CurrentUser.Id, cog.Id);
        }
        await CogitationService.DeleteAsync(cog.Id);
        _cogitations.Remove(cog);
        if (SessionState.ActiveCogitationId == cog.Id)
            SessionState.ActiveCogitationId = null;
    }

    // ── Nav ───────────────────────────────────────────────────────────────

    internal void NewChat()
    {
        if (!SoulVerified) return;   // no verified bridge → no new cogitation
        ClosePanel();
        CloseModal();
        CloseIndexModal();
        SessionState.RequestNewChat();
        // Only navigate if not already on the chat page — same-URL NavigateTo causes
        // a full page reload in Blazor Server, killing the circuit and flashing default styles.
        var onChat = Nav.Uri.TrimEnd('/').Equals(Nav.BaseUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        if (!onChat)
            Nav.NavigateTo("/");
    }

    internal void SelectUser(User user) => _ = SelectUserAsync(user);

    // ── Panel ─────────────────────────────────────────────────────────────

    internal void TogglePanel(string panel)
    {
        if (panel == "index") { _indexModal = !_indexModal; return; }
        _activePanel = _activePanel == panel ? null : panel;
        if (_activePanel == "devices") _ = LoadNodesAsync();
        // Deliberately NOT marking cogitations seen just from opening this panel — the per-row dots
        // (IsCogitationUnseen) are the whole point of opening it; clearing them on open would hide
        // which one(s) are unread before the user ever sees them. Each row clears individually when
        // its own cogitation is opened (Registry.MarkSeen in Chat.OnCogitationSelected).
    }

    internal void ClosePanel() => _activePanel = null;

    internal void CloseIndexModal() => _indexModal = false;

    // Opens a section panel on request from elsewhere (e.g. the chat "/" command palette).
    internal async void OnOpenPanelRequested(string panel)
    {
        if (panel == "index") { _indexModal = true; await InvokeAsync(StateHasChanged); return; }
        _activePanel = panel;
        if (panel == "devices") await LoadNodesAsync();
        await InvokeAsync(StateHasChanged);
    }

    internal async void OnSessionStateChanged() => await InvokeAsync(StateHasChanged);

    // Soul verification changed (daemon connected/disconnected). Re-render so soul-gated controls
    // — notably the New Cogitation button — enable/disable immediately.
    internal void OnSoulStatusChangedNav(string sessionKey)
    {
        // Per-circuit verification key (§12): "circuit-{token}-{userId}".
        if (sessionKey == $"circuit-{SessionState.SessionToken}-{SessionState.CurrentUser?.Id}")
            _ = InvokeAsync(StateHasChanged);
    }

    internal async Task ToggleSidebar()
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        try { await JS.InvokeVoidAsync("ariaInterop.setSidebarCollapsed", _sidebarCollapsed); }
        catch { }
    }

    public void Dispose()
    {
        SessionState.CogitationsChanged   -= OnCogitationsChanged;
        SessionState.FocusedFolderChanged -= OnFocusedFolderChanged;
        SessionState.OnChange             -= OnSessionStateChanged;
        SessionState.SourceChanged        -= OnSourceChangedNav;
        SessionState.InitializingChanged  -= OnInitializingChangedNav;
        SessionState.VigilModalChanged    -= OnVigilModalChanged;
        SessionState.OpenPanelRequested   -= OnOpenPanelRequested;
        Nav.LocationChanged               -= OnLocationChangedNav;
        ExchangeService.InviteReceived    -= OnExchangeInviteReceived;
        ExchangeService.StatusChanged     -= OnExchangeStatusChanged;
        BridgeRegistry.DirectBridgeRegistered   -= OnDirectBridgeRegisteredNav;
        BridgeRegistry.DirectBridgeDisconnected -= OnDirectBridgeDisconnectedNav;
        BridgeRegistry.SoulUnlinked             -= OnSoulUnlinkedNav;
        BridgeRegistry.SoulRegistered           -= OnSoulRegisteredNav;
        BridgeRegistry.SoulStatusChanged        -= OnSoulStatusChangedNav;
        BridgeRegistry.NodesChanged             -= OnNodesChangedNav;
        PendingEnrollments.Changed              -= OnPendingEnrollmentsChanged;
        Services.Tool.UserToolService.ToolsChanged -= OnToolsChangedNav;
        RunRegistry.RunsChanged                 -= OnCogitationRunsChanged;
        RunRegistry.UnseenChanged                -= OnCogitationUnseenChanged;
        CollectiveService.CollectiveRenamed      -= OnCollectiveRenamed;
        if (SessionState.CodeUnlockHandler == HandleCodeUnlockAsync) SessionState.CodeUnlockHandler = null;
        _vigilPollTimer?.Dispose();
        _memoryPollTimer?.Dispose();
        _dotnetRef?.Dispose();
        try { JS.InvokeVoidAsync("ariaInterop.setChannelTooltip", false, ""); } catch { }
    }
}
