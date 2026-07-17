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

    protected override async Task OnInitializedAsync()
    {
        SessionState.OnChange += OnSessionChanged;
        if (SessionState.CurrentUser != null)
            await RefreshAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_graph.Nodes.Count > 0 && !_canvasJsInit)
        {
            _canvasJsInit = true;
            try { await JS.InvokeVoidAsync("ariaInterop.initMemoryCanvas", ".mem-canvas-wrap", _worldCenterX, _worldCenterY); }
            catch (Exception ex) { Logger.LogWarning(ex, "[MemoryUI] canvas init failed"); _canvasJsInit = false; }
        }
    }

    public void Dispose() => SessionState.OnChange -= OnSessionChanged;

    private async void OnSessionChanged() => await InvokeAsync(RefreshAsync);

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

        _loading = false;
        StateHasChanged();
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
