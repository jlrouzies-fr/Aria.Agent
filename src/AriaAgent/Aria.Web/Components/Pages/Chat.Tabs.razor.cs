using Aria.Web.Data.Cogitations;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Pages;

// ── Chat tab bar ─────────────────────────────────────────────────────────
// Renders the open-cogitation tab strip at the top of the Chat UI (Chat.razor). Ownership split:
// UserSessionState.OpenTabIds is the source of truth for WHICH ids are open and which is active;
// this partial owns the display metadata (title/colour) cache and localStorage persistence, since
// both need JS interop / DB access UserSessionState doesn't have.
public partial class Chat
{
    private sealed record TabInfo(string Title, string Color, bool IsHive);

    private readonly Dictionary<int, TabInfo> _tabInfo = new();
    private bool _tabsRestored;

    private string TabsStorageKey() => $"ariaOpenCogitationTabs:{SessionState.CurrentUser?.Id}";

    private async Task InitTabsAsync()
    {
        if (SessionState.CurrentUser == null) return;

        if (!_tabsRestored)
        {
            _tabsRestored = true;
            try
            {
                var stored = await JS.InvokeAsync<string?>("ariaInterop.getLocalStorage", TabsStorageKey());
                if (!string.IsNullOrEmpty(stored))
                {
                    var ids = stored.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.TryParse(s, out var v) ? v : (int?)null)
                        .Where(v => v.HasValue)
                        .Select(v => v!.Value);
                    SessionState.RestoreOpenTabs(ids);
                }
            }
            catch { /* localStorage unavailable — tabs just start empty */ }
        }

        await RefreshTabMetaAsync();
    }

    private void OnOpenTabsChanged() => _ = InvokeAsync(async () =>
    {
        await PersistTabsAsync();
        await RefreshTabMetaAsync();
        StateHasChanged();
    });

    // Cogitation titles/folders can change (auto-title after first message, refiling) without the
    // tab list itself changing — drop the cache so the next refresh re-fetches fresh metadata.
    private void OnCogitationsChangedForTabs() => _ = InvokeAsync(async () =>
    {
        _tabInfo.Clear();
        await RefreshTabMetaAsync();
        StateHasChanged();
    });

    private async Task PersistTabsAsync()
    {
        if (SessionState.CurrentUser == null) return;
        try
        {
            var value = string.Join(',', SessionState.OpenTabIds);
            await JS.InvokeVoidAsync("ariaInterop.setLocalStorage", TabsStorageKey(), value);
        }
        catch { }
    }

    private async Task RefreshTabMetaAsync()
    {
        foreach (var id in SessionState.OpenTabIds)
        {
            if (_tabInfo.ContainsKey(id)) continue;
            var cog = await CogitationService.GetAsync(id);
            if (cog != null) CacheTabMeta(cog);
        }

        foreach (var staleId in _tabInfo.Keys.Where(k => !SessionState.OpenTabIds.Contains(k)).ToList())
            _tabInfo.Remove(staleId);
    }

    private void CacheTabMeta(Cogitation cog)
    {
        var isHive = cog.CollectiveId != null;
        // Hive purple always wins (branding — visible "this is a collective" at a glance). Otherwise
        // the tab's identity color is the cogitation's OWN sub-agent accent, not the dossier/folder
        // it happens to be filed under — each tab should read as "which agent", not "which folder".
        var color = isHive ? "#8060c0" : (cog.SubAgent?.AccentColor ?? cog.Folder?.Color ?? "#8B0000");
        _tabInfo[cog.Id] = new TabInfo(cog.Title, color, isHive);
    }

    private TabInfo? GetTabInfo(int id) => _tabInfo.TryGetValue(id, out var t) ? t : null;

    private void SelectTab(int id)
    {
        if (id == _cogitationId) return;
        SessionState.SelectCogitation(id);
    }

    private void OpenNewTab() => SessionState.RequestNewChat();

    private void CloseTab(int id)
    {
        var becameEmpty = SessionState.CloseTab(id);
        _tabInfo.Remove(id);
        if (becameEmpty) SessionState.RequestNewChat();
    }
}
