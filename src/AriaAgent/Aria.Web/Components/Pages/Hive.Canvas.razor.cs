using System.Text.Json;
using Aria.Web.Data;
using Aria.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Pages;

public partial class Hive
{
    // Cubic bezier point for the edge shape: P0=(fx,fy) P1=(fx,my) P2=(tx,my) P3=(tx,ty), my=(fy+ty)/2
    public static (int x, int y) BezierPoint(int fx, int fy, int tx, int ty, double t)
    {
        var my = (fy + ty) / 2.0;
        var bx = fx * (1-t)*(1-t)*(1+2*t) + tx * t*t*(3-2*t);
        var by = fy * (1-t)*(1-t)*(1-t)   + 3*(1-t)*t*my    + ty*t*t*t;
        return ((int)Math.Round(bx), (int)Math.Round(by));
    }

    // ── Node geometry (canvas) ────────────────────────────────────────────────
    // Both blocks are 150px wide. Heights vary a little with content (name wrap, run labels), so these
    // are slightly-generous half-heights: the border point then lands on or just outside the true edge,
    // never inside it — so edge nodes can never tuck under a block.
    public const double NodeHalfW      = 75;
    public const double OvermindCX     = 425;   // left 350 + width 150 / 2
    public const double OvermindTop    = 40;
    public const double OvermindHalfH  = 140;   // ~280px tall (larger avatar + labels + badges)
    public const double DroneHalfH     = 85;    // ~170px tall

    // Point where the ray from a rectangle's centre toward (tx,ty) crosses the rectangle border.
    // Anchoring edges here — rather than at fixed points inside the blocks — keeps the link (and the
    // transform/gate/condition nodes strung along it) starting/ending exactly at the borders where it
    // visually enters/exits, for ANY relative layout: drone below, beside, or above the Overmind.
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

    // ── Edge insert menu ──────────────────────────────────────────────────────

    public void ShowInsertMenu(int memberId, string? nodeType, int? nodeId, MouseEventArgs e)
    {
        _insertMenuMemberId  = memberId;
        _insertMenuNodeType  = nodeType;
        _insertMenuX         = e.ClientX;
        _insertMenuY         = e.ClientY;
        _editingTransform    = null;
    }

    public void CloseInsertMenu()
    {
        _insertMenuMemberId = null;
        _insertMenuNodeType = null;
    }

    // ── Transform node editor ─────────────────────────────────────────────────

    public void OpenTransformEditor(MemberEdgeNode node, MouseEventArgs e)
    {
        _editingTransform      = node;
        _editTransformTemplate = ExtractTransformTemplate(node.Config);
        _insertMenuMemberId    = null;
        _insertMenuX           = e.ClientX;
        _insertMenuY           = e.ClientY;
    }

    public void CloseTransformEditor()
    {
        _editingTransform = null;
    }

    public async Task SaveTransformEditor()
    {
        if (_editingTransform == null) return;
        var config = JsonSerializer.Serialize(new { template = _editTransformTemplate });
        await CollectiveService.UpdateEdgeNodeConfigAsync(_editingTransform.Id, config);
        if (_selectedCollectiveId.HasValue)
            _members = await CollectiveService.GetMembersAsync(_selectedCollectiveId.Value);
        _editingTransform = null;
        StateHasChanged();
    }

    public async Task DeleteTransform(int nodeId)
    {
        await CollectiveService.RemoveEdgeNodeAsync(nodeId);
        if (_selectedCollectiveId.HasValue)
            _members = await CollectiveService.GetMembersAsync(_selectedCollectiveId.Value);
        _editingTransform = null;
        StateHasChanged();
    }

    public async Task AddTransformToMember(int memberId)
    {
        var defaultConfig = JsonSerializer.Serialize(new { template = "{{original}}" });
        var node = await CollectiveService.AddEdgeNodeAsync(memberId, EdgeNodeType.Transform, 100, defaultConfig);
        if (_selectedCollectiveId.HasValue)
            _members = await CollectiveService.GetMembersAsync(_selectedCollectiveId.Value);
        CloseInsertMenu();
        // Immediately open the editor for the new node
        _editingTransform      = node;
        _editTransformTemplate = "{{original}}";
        StateHasChanged();
    }

    public static string ExtractTransformTemplate(string? config)
    {
        if (string.IsNullOrWhiteSpace(config)) return "{{original}}";
        try
        {
            var doc = JsonDocument.Parse(config);
            return doc.RootElement.TryGetProperty("template", out var t)
                ? (t.GetString() ?? "{{original}}") : "{{original}}";
        }
        catch { return "{{original}}"; }
    }

    // ── Condition node editor ─────────────────────────────────────────────────

    public const string DefaultLlmFitCheck = "Is this drone well-suited to handle this task?";

    public static List<(string Value, string Label)> ConditionModeOptions() => new()
    {
        ("contains", "Contains text"),
        ("any",      "Any keyword (a, b, c)"),
        ("all",      "All keywords (a, b, c)"),
        ("regex",    "Regex match"),
        ("llm",      "LLM judges (yes/no)")
    };

    // When switching to LLM mode with no test yet, prefill the default fit-check so the user sees
    // what the judge does by default.
    public void OnConditionModeChanged(string? v)
    {
        _editConditionMode = v ?? "contains";
        if (_editConditionMode == "llm" && string.IsNullOrWhiteSpace(_editConditionValue))
            _editConditionValue = DefaultLlmFitCheck;
    }

    // Glyph shown on the canvas condition node so the user reads its mode at a glance.
    public static string ConditionGlyph(MemberEdgeNode node) =>
        CollectiveService.ParseCondition(node.Config).Mode switch
        {
            "any"   => "||",    // OR
            "all"   => "&",     // AND
            "regex" => ".*",    // pattern
            "llm"   => "?",     // Overmind judges (rendered in Overmind purple)
            _        => "≈"     // contains — loose/partial text match (not exact "=")
        };

    // Colour-codes the condition node: purple = LLM (Overmind judges, costs a call),
    // gold = a local deterministic test. Matches the "reviewed" timeline colour for LLM.
    public static string ConditionColor(MemberEdgeNode node) =>
        CollectiveService.ParseCondition(node.Config).Mode == "llm" ? "#8060c0" : "#c8a020";

    public void OpenConditionEditor(MemberEdgeNode node, MouseEventArgs e)
    {
        var (mode, value, negate) = CollectiveService.ParseCondition(node.Config);
        _editingCondition    = node;
        _editConditionMode   = mode;
        _editConditionValue  = value;
        _editConditionNegate = negate;
        _insertMenuMemberId  = null;
        _insertMenuX = e.ClientX; _insertMenuY = e.ClientY;
    }

    public void CloseConditionEditor() => _editingCondition = null;

    public async Task SaveConditionEditor()
    {
        if (_editingCondition == null) return;
        var config = JsonSerializer.Serialize(
            new { mode = _editConditionMode, value = _editConditionValue, negate = _editConditionNegate });
        await CollectiveService.UpdateEdgeNodeConfigAsync(_editingCondition.Id, config);
        if (_selectedCollectiveId.HasValue)
            _members = await CollectiveService.GetMembersAsync(_selectedCollectiveId.Value);
        _editingCondition = null;
        StateHasChanged();
    }

    public async Task DeleteCondition(int nodeId)
    {
        await CollectiveService.RemoveEdgeNodeAsync(nodeId);
        if (_selectedCollectiveId.HasValue)
            _members = await CollectiveService.GetMembersAsync(_selectedCollectiveId.Value);
        _editingCondition = null;
        StateHasChanged();
    }

    public async Task AddConditionToMember(int memberId)
    {
        // Default a new condition to the LLM fit-check so the user immediately sees the judging behavior.
        var defaultConfig = JsonSerializer.Serialize(new { mode = "llm", value = DefaultLlmFitCheck, negate = false });
        var node = await CollectiveService.AddEdgeNodeAsync(memberId, EdgeNodeType.Condition, 50, defaultConfig);
        if (_selectedCollectiveId.HasValue)
            _members = await CollectiveService.GetMembersAsync(_selectedCollectiveId.Value);
        CloseInsertMenu();
        _editingCondition    = node;
        _editConditionMode   = "llm";
        _editConditionValue  = DefaultLlmFitCheck;
        _editConditionNegate = false;
        StateHasChanged();
    }

    // ── Node tooltips ─────────────────────────────────────────────────────────

    public void ShowConditionTooltip(MemberEdgeNode node, CollectiveMember m, MouseEventArgs e)
    {
        var (mode, value, negate) = CollectiveService.ParseCondition(node.Config);
        _tooltipMember   = m;
        _tooltipNodeType = "condition";
        _tooltipExtra    = (negate ? "NOT " : "") + (mode == "llm" ? $"LLM: {value}" : $"contains: {value}");
        _tooltipX        = e.ClientX + 16;
        _tooltipY        = e.ClientY - 42;
        _tooltipVisible  = true;
    }

    public void ShowTooltip(CollectiveMember m, MouseEventArgs e)
    {
        _tooltipMember   = m;
        _tooltipNodeType = "gate";
        _tooltipExtra    = null;
        _tooltipX        = e.ClientX + 16;
        _tooltipY        = e.ClientY - 42;
        _tooltipVisible  = true;
    }

    public void ShowTransformTooltip(MemberEdgeNode node, CollectiveMember m, MouseEventArgs e)
    {
        _tooltipMember   = m;
        _tooltipNodeType = "transform";
        _tooltipExtra    = ExtractTransformTemplate(node.Config);
        _tooltipX        = e.ClientX + 16;
        _tooltipY        = e.ClientY - 42;
        _tooltipVisible  = true;
    }

    public void ShowRetryTooltip(CollectiveMember m, int retryCount, MouseEventArgs e)
    {
        _tooltipMember   = m;
        _tooltipNodeType = "retry";
        _tooltipExtra    = retryCount.ToString();
        _tooltipX        = e.ClientX + 16;
        _tooltipY        = e.ClientY - 42;
        _tooltipVisible  = true;
    }

    public void HideTooltip()
    {
        _tooltipVisible  = false;
        _tooltipMember   = null;
        _tooltipNodeType = null;
    }

    // ── Gate approval on canvas ───────────────────────────────────────────────

    public void ApproveCanvasGate(int memberId)
    {
        _insertMenuMemberId = null;
        if (_activeCogId != 0)
            Orchestrator.ApproveHiveMemberGate(_activeCogId, memberId);
    }

    // ── Per-drone gate events ─────────────────────────────────────────────────

    public void OnMemberGatePending(int cogId, int memberId, string droneName, string? content)
    {
        _ = InvokeAsync(() =>
        {
            _activeCogId = cogId;
            _pendingGateMembers[memberId] = content;
            StateHasChanged();
        });
    }

    public void OnMemberGateResolved(int cogId, int memberId)
    {
        _ = InvokeAsync(() =>
        {
            _pendingGateMembers.Remove(memberId);
            StateHasChanged();
        });
    }

    // ── JS drag callback ──────────────────────────────────────────────────────

    [JSInvokable]
    public async Task OnDroneMoved(int memberId, double x, double y)
    {
        var m = _members.FirstOrDefault(m => m.Id == memberId);
        if (m == null) return;
        m.CanvasX = x;
        m.CanvasY = y;
        await CollectiveService.SaveMemberPositionAsync(memberId, x, y);
        // Re-render so the SVG edge (drawn from CanvasX/Y) rebuilds immediately on release,
        // instead of only redrawing on the next unrelated render (a later click).
        await InvokeAsync(StateHasChanged);
    }

    public async Task AddGateToMember(int memberId)
    {
        _insertMenuMemberId = null;
        await CollectiveService.ToggleMemberGateAsync(memberId, true);
        await RefreshAsync();
    }

    public async Task RemoveGateFromMember(int memberId)
    {
        _insertMenuMemberId = null;
        await CollectiveService.ToggleMemberGateAsync(memberId, false);
        await RefreshAsync();
    }

    public async Task ToggleGateMode(int memberId, bool gateAfterResponse)
    {
        _insertMenuMemberId = null;
        await CollectiveService.SetGateAfterResponseAsync(memberId, gateAfterResponse);
        await RefreshAsync();
    }
}
