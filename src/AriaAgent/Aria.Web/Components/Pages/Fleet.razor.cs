using Aria.Web.Services.Chat;
using Aria.Web.Services.Fleet;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Pages;

public partial class Fleet
{
    [Inject] private FleetRegistry    FleetRegistry { get; set; } = default!;
    [Inject] private UserSessionState SessionState  { get; set; } = default!;
    [Inject] private IJSRuntime       JS            { get; set; } = default!;
    [Inject] private ILogger<Fleet>   Logger        { get; set; } = default!;

    // ── State ──────────────────────────────────────────────────────────────────
    private List<FleetNode> _nodes   = [];
    private FleetNode?      _selected;
    private bool            _copied;
    private bool            _canvasJsInit;
    private readonly CancellationTokenSource _pollCts = new();

    // ── Canvas geometry (world units, matches the 4000×3000 SVG) ─────────────
    // Vertical lineage tree: ARIA CORE sits at the top, fleet node cards form a row below it.
    // NodeHalfH is generous because card height varies with gauges/chips; edges anchor at the
    // top center of each card so they always meet the border cleanly.
    public  const double CoreCX     = 2000;
    public  const double CoreCY     = 460;
    public  const double CoreHalfW  = 85;
    public  const double CoreHalfH  = 45;     // card bottom sits just under "2 NODES" so edges feel attached
    public  const double NodeHalfW  = 105;
    public  const double NodeHalfH  = 130;
    public  const double NodeRowY   = 570;    // node row snug beneath the core
    public  const double NodeGap    = 70;     // horizontal gap between node cards
    private const int    MaxChips   = 6;

    /// <summary>Vertical center of the whole tree, used to position the initial viewport.</summary>
    public static double ViewCenterY => (CoreCY + CoreHalfH + NodeRowY) / 2;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override async Task OnInitializedAsync()
    {
        SessionState.OnChange += OnSessionChanged;
        await RefreshAsync();
        _ = PollLoopAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_canvasJsInit) return;
        _canvasJsInit = true;
        try
        {
            await JS.InvokeVoidAsync("ariaInterop.initFleetCanvas", ".fl-canvas-wrap", CoreCX, ViewCenterY);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[FleetUI] OnAfterRenderAsync init failed");
            _canvasJsInit = false;
        }
    }

    public void Dispose()
    {
        SessionState.OnChange -= OnSessionChanged;
        _pollCts.Cancel();
        _pollCts.Dispose();
    }

    private async void OnSessionChanged()
    {
        if (SessionState.CurrentUser != null)
            await InvokeAsync(RefreshAsync);
    }

    // ── Polling (5 s while the page is open) ─────────────────────────────────

    private async Task PollLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(_pollCts.Token))
                await InvokeAsync(RefreshAsync);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException)    { }
        catch (InvalidOperationException)  { }   // circuit gone
        catch (JSDisconnectedException)    { }
    }

    private async Task RefreshAsync()
    {
        var userId = SessionState.CurrentUser?.Id;
        if (userId == null) return;
        try
        {
            _nodes = (await FleetRegistry.GetFleetAsync(userId, _pollCts.Token)).ToList();
        }
        catch (OperationCanceledException) { return; }
        // Re-point the selection at the fresh node instance so the open drawer keeps live data.
        if (_selected != null)
            _selected = _nodes.FirstOrDefault(n => n.NodeId == _selected.NodeId);
        StateHasChanged();
    }

    // ── Selection / drawer ────────────────────────────────────────────────────

    private void SelectNode(FleetNode node) => _selected = node;
    private void CloseDrawer()              => _selected = null;

    private async Task CopyFleetJsonAsync()
    {
        var userId = SessionState.CurrentUser?.Id;
        if (userId == null) return;
        var json = await FleetRegistry.GetStatusJsonAsync(userId, _pollCts.Token);
        await JS.InvokeVoidAsync("ariaInterop.copyText", json);
        _copied = true;
        StateHasChanged();
        await Task.Delay(1500);
        _copied = false;
        StateHasChanged();
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    /// <summary>Card top-left for node <paramref name="index"/> of <paramref name="count"/>,
    /// arranged in a horizontal row beneath ARIA CORE, centered on the canvas.</summary>
    public static (double Left, double Top) NodePosition(int index, int count)
    {
        if (count <= 0) return (CoreCX - NodeHalfW, NodeRowY);
        var cardW = NodeHalfW * 2;
        var totalW = count * cardW + (count - 1) * NodeGap;
        var startX = CoreCX - totalW / 2;
        return (startX + index * (cardW + NodeGap), NodeRowY);
    }

    // Point where the ray from a rectangle's centre toward (tx,ty) crosses the rectangle border —
    // same anchor rule as the Hive canvas, so links meet cards at their edges in any direction.
    public static (int x, int y) BorderPoint(double cx, double cy, double halfW, double halfH, double tx, double ty)
    {
        var dx = tx - cx;
        var dy = ty - cy;
        if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6) return ((int)Math.Round(cx), (int)Math.Round(cy));
        var scaleX = Math.Abs(dx) > 1e-6 ? halfW / Math.Abs(dx) : double.PositiveInfinity;
        var scaleY = Math.Abs(dy) > 1e-6 ? halfH / Math.Abs(dy) : double.PositiveInfinity;
        var t = Math.Min(scaleX, scaleY);
        return ((int)Math.Round(cx + dx * t), (int)Math.Round(cy + dy * t));
    }

    // ── Formatting helpers (shared with FleetNodeDrawer) ─────────────────────

    public static string FormFactorGlyph(FleetNode n) => n.Hardware?.FormFactor switch
    {
        "laptop"  => "💻",
        "desktop" => "🖥",
        _         => "❔",
    };

    public static string OsBadge(FleetNode n) => n.Platform switch
    {
        "Windows" => "WIN",
        "macOS"   => "MAC",
        "Linux"   => "LNX",
        _         => "OS",
    };

    public static string Pct(double? v) => v is { } x ? $"{Math.Round(x):0}%" : "—";

    public static string FmtMb(double? mb) => mb is { } v
        ? (v >= 1024 ? $"{v / 1024:0.#} GB" : $"{v:0} MB")
        : "—";

    public static string FmtWatts(double? mw) => mw is { } v ? $"{v / 1000:0.#} W" : "—";

    public static string FmtUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : $"{t.Hours}h {t.Minutes}m";

    /// <summary>CSS width for a gauge fill; unknown values collapse to an empty bar.</summary>
    public static string GaugeWidth(double? pct) =>
        pct is { } p ? $"{(int)Math.Clamp(Math.Round(p), 0, 100)}%" : "0%";

    public static double? RamPercent(FleetNode n) =>
        n.Metrics is { SystemMemoryUsedMb: { } u, SystemMemoryTotalMb: { } t } && t > 0
            ? u / t * 100
            : null;

    public static double? VramPercent(FleetNode n) =>
        n.Metrics is { GpuMemoryTotalMb: { } t } && t > 0
            ? (t - (n.Metrics.GpuMemoryFreeMb ?? t)) / t * 100
            : null;

    public static double? VramUsedMb(FleetNode n) =>
        n.Metrics is { GpuMemoryTotalMb: { } t, GpuMemoryFreeMb: { } f } ? t - f : null;

    public static List<string> AllModels(FleetNode n) =>
        n.Channels.SelectMany(c => c.Models).ToList();

    public static int ChipOverflow(FleetNode n) => Math.Max(0, AllModels(n).Count - MaxChips);

    /// <summary>Truncate a chip label; the full value goes in the data-tip tooltip.</summary>
    public static string Trunc(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s[..max] + "…" : s);
}
