using Aria.Web.Services.Chat;
using Aria.Web.Services.Memory;
using Aria.Web.Services.ModelBridge;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Pages;

public partial class Memory : IDisposable
{
    [Inject] private BridgeMemoryClient MemoryClient  { get; set; } = null!;
    [Inject] private UserSessionState   SessionState  { get; set; } = null!;
    [Inject] private NavigationManager  Nav           { get; set; } = null!;
    [Inject] private IJSRuntime         JS            { get; set; } = null!;
    [Inject] private ILogger<Memory>    Logger        { get; set; } = null!;
    [Inject] private ModelBridgeRegistry BridgeRegistry { get; set; } = null!;

    // Memory stores are node-local (one vault per bridge, never replicated), so the whole page is scoped
    // to one node at a time. Null = fall through to whichever node answers (the single-node default).
    internal List<(string NodeId, string Label)> _nodes = [];
    internal string? _selectedNodeId;
    // Per-node Inscribe queue / sticky extract failure — drives gold blink / red warn on the node bar
    // so a multi-bridge setup shows *which* vault matches the sidebar brain blink.
    internal HashSet<string> _processingNodeIds = [];
    internal Dictionary<string, string> _errorNodeTips = new(StringComparer.Ordinal);

    internal MemoryStatsDto?           _stats;
    internal MemoryGraphDto            _graph = new([], []);
    internal Dictionary<string, (double X, double Y)> _positions = [];
    internal List<MemoryGraphLayout.MemoryCluster> _clusters = [];
    internal double _worldWidth = 2800, _worldHeight = 2200, _worldCenterX = 1400, _worldCenterY = 1100;

    internal string?           _selectedEntityId;
    internal List<EngramDto>   _selectedEntityEngrams = [];
    internal string?           _mergeTargetId;

    internal string              _query = "";
    internal List<ProbeResultDto>? _searchResults;

    internal bool _loading = true;
    internal ElementReference _canvasRef;
    internal bool _canvasJsInit;
    // Set when the world we're painting changed under the canvas (node switch): the JS pan/zoom state
    // survives the re-render, so it has to be told to re-centre on the new world.
    private bool _canvasRecenterPending;
    private Timer? _nodeHealthTimer;

    // Empty-world defaults, mirrored from MemoryGraphLayout's fallback so a node with no engrams
    // paints a clean canvas instead of the previous node's world box.
    private const double EmptyWorldWidth = 2800, EmptyWorldHeight = 2200;

    protected override async Task OnInitializedAsync()
    {
        SessionState.OnChange += OnSessionChanged;
        if (SessionState.CurrentUser != null)
            await RefreshAsync();
        // Same cadence as the sidebar Noosphere poll — Inscribe is fire-and-forget so the page has
        // to ask each bridge which vaults still have a draining queue.
        _nodeHealthTimer = new Timer(async _ =>
        {
            try { await RefreshNodeHealthAsync(); }
            catch { /* best-effort */ }
        }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_graph.Nodes.Count > 0 && !_canvasJsInit)
        {
            _canvasJsInit = true;
            _canvasRecenterPending = false;
            try { await JS.InvokeVoidAsync("ariaInterop.initMemoryCanvas", ".mem-canvas-wrap", _worldCenterX, _worldCenterY); }
            catch (Exception ex) { Logger.LogWarning(ex, "[MemoryUI] canvas init failed"); _canvasJsInit = false; }
        }
        else if (_canvasRecenterPending && _canvasJsInit)
        {
            _canvasRecenterPending = false;
            try { await JS.InvokeVoidAsync("ariaInterop.recenterMemoryCanvas", ".mem-canvas-wrap", _worldCenterX, _worldCenterY); }
            catch (Exception ex) { Logger.LogWarning(ex, "[MemoryUI] canvas recenter failed"); }
        }
    }

    public void Dispose()
    {
        SessionState.OnChange -= OnSessionChanged;
        _nodeHealthTimer?.Dispose();
        _nodeHealthTimer = null;
    }

    private async void OnSessionChanged() => await InvokeAsync(RefreshAsync);

    /// <summary>Poll every connected bridge's /memory/stats and paint per-node busy/warn on the bar.</summary>
    private async Task RefreshNodeHealthAsync()
    {
        if (SessionState.CurrentUser == null) return;
        var userId = SessionState.CurrentUser.Id.ToString();
        var health = await MemoryClient.GetPerNodeHealthAsync(userId);

        var processing = health.Where(h => h.Processing && !string.IsNullOrEmpty(h.NodeId))
            .Select(h => h.NodeId).ToHashSet(StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var h in health)
        {
            if (string.IsNullOrEmpty(h.NodeId) || !h.HasExtractionError) continue;
            var msg = h.Stats!.LastExtractionError!;
            errors[h.NodeId] = $"// EXTRACTION FAILING · {h.Label} — {msg}";
        }

        // Selected vault just finished draining — reload graph so new entities appear without a manual switch.
        var selectedWasBusy = _selectedNodeId != null && _processingNodeIds.Contains(_selectedNodeId);
        var selectedStillBusy = _selectedNodeId != null && processing.Contains(_selectedNodeId);

        var changed = !processing.SetEquals(_processingNodeIds)
                      || !errors.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(_errorNodeTips.Keys)
                      || errors.Any(kv => !_errorNodeTips.TryGetValue(kv.Key, out var prev) || prev != kv.Value);

        _processingNodeIds = processing;
        _errorNodeTips = errors;

        if (selectedWasBusy && !selectedStillBusy)
        {
            await InvokeAsync(RefreshAsync);
            return;
        }

        if (changed)
            await InvokeAsync(StateHasChanged);
    }

    /// <summary>Refresh the connected-node roster and keep the current selection valid. Called before
    /// every load so the switcher tracks nodes coming and going.</summary>
    private void RefreshNodes(string userId)
    {
        _nodes = BridgeRegistry.GetNodes(userId)
            .Select(n => (n.NodeId, Label: string.IsNullOrEmpty(n.Label) ? n.NodeId : n.Label
                + (string.IsNullOrEmpty(n.Platform) ? "" : " · " + n.Platform)))
            .ToList();

        // Default to the first node; drop a selection whose node has disconnected.
        if (_selectedNodeId != null && _nodes.All(n => n.NodeId != _selectedNodeId))
            _selectedNodeId = null;
        if (_selectedNodeId == null && _nodes.Count > 0)
            _selectedNodeId = _nodes[0].NodeId;
    }

    internal async Task SelectNodeAsync(string nodeId)
    {
        if (_selectedNodeId == nodeId) return;
        _selectedNodeId = nodeId;
        CloseEntityDrawer();
        _searchResults = null;
        _canvasRecenterPending = true;
        await RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (SessionState.CurrentUser == null) return;
        _loading = true;
        StateHasChanged();

        var userId = SessionState.CurrentUser.Id.ToString();
        RefreshNodes(userId);
        _stats = await MemoryClient.GetStatsAsync(userId, _selectedNodeId);
        _graph = await MemoryClient.GetGraphAsync(userId, _selectedNodeId);

        if (_graph.Nodes.Count > 0)
        {
            var layout = MemoryGraphLayout.ComputeClusteredLayout(_graph.Nodes, _graph.Edges);
            _positions = layout.Positions;
            _clusters = layout.Clusters;
            _worldWidth = layout.Width;
            _worldHeight = layout.Height;
            _worldCenterX = layout.CenterX;
            _worldCenterY = layout.CenterY;
        }
        else
        {
            // MemoryCanvas paints hulls and positions independently of _graph.Nodes, so leaving the
            // previous node's layout in place drew its cluster halos and titles behind the "archivum
            // is silent" empty state after switching to a node with no engrams.
            _positions = [];
            _clusters = [];
            _worldWidth = EmptyWorldWidth;
            _worldHeight = EmptyWorldHeight;
            _worldCenterX = EmptyWorldWidth / 2;
            _worldCenterY = EmptyWorldHeight / 2;
        }

        _loading = false;
        // Seed pill blinks from the stats we already fetched for the selected node, then fill in
        // siblings via the background poll (avoids a full multi-node round-trip on every refresh).
        if (_selectedNodeId != null && _stats is { PendingIngests: > 0 })
            _processingNodeIds.Add(_selectedNodeId);
        else if (_selectedNodeId != null)
            _processingNodeIds.Remove(_selectedNodeId);
        StateHasChanged();
        _ = RefreshNodeHealthAsync();
    }

    internal async Task SelectEntityAsync(string entityId)
    {
        if (SessionState.CurrentUser == null) return;
        _selectedEntityId = _selectedEntityId == entityId ? null : entityId;
        if (_selectedEntityId == null) { _selectedEntityEngrams = []; StateHasChanged(); return; }

        var userId = SessionState.CurrentUser.Id.ToString();
        _selectedEntityEngrams = await MemoryClient.GetEngramsAsync(userId, limit: 100, entityId: _selectedEntityId, nodeId: _selectedNodeId);
        StateHasChanged();
    }

    internal void CloseEntityDrawer()
    {
        _selectedEntityId = null;
        _selectedEntityEngrams = [];
        _mergeTargetId = null;
    }

    internal async Task MergeSelectedEntityAsync()
    {
        if (SessionState.CurrentUser == null || _selectedEntityId == null || string.IsNullOrEmpty(_mergeTargetId)) return;
        var userId = SessionState.CurrentUser.Id.ToString();
        await MemoryClient.MergeEntityAsync(userId, _selectedEntityId, _mergeTargetId, _selectedNodeId);
        CloseEntityDrawer();
        await RefreshAsync();
    }

    internal async Task OnSearchKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await SearchAsync();
    }

    internal async Task SearchAsync()
    {
        if (SessionState.CurrentUser == null || string.IsNullOrWhiteSpace(_query)) return;
        var userId = SessionState.CurrentUser.Id.ToString();
        var result = await MemoryClient.ProbeAsync(userId, _query.Trim(), _selectedNodeId);
        _searchResults = result?.Results ?? [];
        StateHasChanged();
    }

    internal void ClearSearch()
    {
        _query = "";
        _searchResults = null;
    }

    internal async Task DeleteEngramAsync(string engramId)
    {
        if (SessionState.CurrentUser == null) return;
        var userId = SessionState.CurrentUser.Id.ToString();
        await MemoryClient.DeleteEngramAsync(userId, engramId, _selectedNodeId);
        _selectedEntityEngrams.RemoveAll(e => e.Id == engramId);
        _searchResults?.RemoveAll(r => r.Id == engramId);
        await RefreshAsync();
    }
}
