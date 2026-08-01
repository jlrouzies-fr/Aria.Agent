namespace Aria.Web.Components.Layout;

public partial class NavMenu
{
    // Active only when on the /memory page AND no flyout panel is drawn over it — otherwise, since
    // opening a panel (Tools, Agents, ...) doesn't navigate away from whatever page is underneath,
    // Noosphere would stay "active" while a different item is actually selected.
    internal bool IsMemoryPageActive => Nav.Uri.Contains("/memory") && _activePanel == null;

    internal void OpenMemoryPage()
    {
        ClosePanel();
        if (!Nav.Uri.Contains("/memory"))
            Nav.NavigateTo("/memory");
    }

    // True while the bridge's Noosphere ingest worker still has inscribes queued for extraction —
    // drives the gold blink on the nav icon. Extraction is async (Inscribe returns immediately);
    // polled rather than pushed since the bridge has no channel back to the web circuit for this.
    internal bool _memoryProcessing;
    // Red warning when any connected node's last extraction failed (LM Studio down, bad URL, …).
    // Survives until that node successfully extracts again — Inscribe itself does not wait, so this
    // is how a human (or a later session) notices a silent failure.
    internal bool    _memoryExtractionFailed;
    internal string? _memoryExtractionFailTip;
    internal Timer?  _memoryPollTimer;

    internal async Task RefreshMemoryProcessingAsync(string userId)
    {
        var health = await MemoryClient.GetNavHealthAsync(userId);
        var processing = health.Processing;
        var failed = !string.IsNullOrEmpty(health.ExtractionError);
        var tip = failed
            ? "Noosphere extraction failing"
              + (string.IsNullOrEmpty(health.ErrorNodeLabel) ? "" : $" on \"{health.ErrorNodeLabel}\"")
              + $": {health.ExtractionError}. Open Noosphere / the bridge Memory tab — structured recall will stay broken until the local model is reachable again."
            : null;

        if (processing == _memoryProcessing
            && failed == _memoryExtractionFailed
            && tip == _memoryExtractionFailTip)
            return;

        _memoryProcessing = processing;
        _memoryExtractionFailed = failed;
        _memoryExtractionFailTip = tip;
        await InvokeAsync(StateHasChanged);
    }

    // Leads Noosphere extraction toward Terminal projects (docs/ideas/noosphere-archive-aware-extraction-plan.md):
    // when an inscribe mentions a configured project, the extractor is told to attach it as a "project"
    // entity, which then naturally becomes a community hub in the memory graph. Replace-all sync, so
    // renamed/removed projects don't linger as stale anchors. Projects now live on the bridge; fetch
    // them directly rather than parsing the stale server-side tool config.
    internal async Task SyncTerminalAnchorsAsync(string userId)
    {
        try
        {
            var projects = await TerminalClient.GetProjectsAsync(userId);
            var anchors = projects.Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => (p.Name, p.Description)).ToList();
            await MemoryClient.SyncAnchorsAsync(userId, anchors);
        }
        catch { /* best-effort — stale anchors until next sync */ }
    }
}
