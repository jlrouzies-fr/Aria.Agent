using Aria.Web.Data;
using Aria.Web.Data.Cogitations;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Layout;

public partial class NavMenu
{
    // ── Folder state ──────────────────────────────────────────────────────
    internal List<CogitationFolder> _folders = [];

    internal bool        _folderModal;
    internal int?        _editingFolderId;
    internal string      _editFolderName        = "";
    internal string      _editFolderColor       = "#8B0000";
    internal int?        _editFolderDefaultAgentId;
    internal string      _editFolderDefaultProjectPath = "";
    internal string      _editFolderDirective   = "";
    internal string?     _folderModalError;
    internal bool        _folderModalSaved;

    internal CogitationFolder? _folderPendingDelete;
    internal int               _folderPendingDeleteCount;

    internal static readonly (string Hex, string Label)[] FolderColorPresets =
    [
        ("#8B0000", "Crimson"),
        ("#B8860B", "Gold"),
        ("#C4621D", "Burnt Orange"),
        ("#1C3A6A", "Void Blue"),
        ("#1A6A5A", "Teal"),
        ("#4A0D6A", "Violet"),
        ("#7A1A4A", "Dark Rose"),
        ("#2D5A1B", "Green"),
        ("#4A3728", "Bone"),
        ("#1A1A3A", "Midnight"),
    ];

    private const string FolderCollapseKeyPrefix = "ariaFolderCollapsed:";

    internal string FolderCollapseKey(int folderId) => $"{FolderCollapseKeyPrefix}{folderId}";

    internal async Task LoadFoldersAsync()
    {
        if (SessionState.CurrentUser == null) return;
        _folders = await FolderService.GetListAsync(SessionState.CurrentUser.Id);
    }

    // ── Focus ─────────────────────────────────────────────────────────────
    internal void FocusFolder(int? folderId)
    {
        if (SessionState.FocusedFolderId == folderId) return;
        SessionState.FocusFolder(folderId);
    }

    internal void ClearFocusedFolder() => FocusFolder(null);

    // ── Create / Edit modal ───────────────────────────────────────────────
    internal void OpenNewFolderModal()
    {
        _editingFolderId            = null;
        _editFolderName             = "";
        _editFolderColor            = FolderColorPresets[0].Hex;
        _editFolderDefaultAgentId   = null;
        _editFolderDefaultProjectPath = "";
        _editFolderDirective        = "";
        _folderModalError           = null;
        _folderModalSaved           = false;
        _folderModal                = true;
    }

    internal async Task OpenEditFolderModalAsync(CogitationFolder folder)
    {
        _editingFolderId            = folder.Id;
        _editFolderName             = folder.Name;
        _editFolderColor            = folder.Color ?? FolderColorPresets[0].Hex;
        _editFolderDefaultAgentId   = folder.DefaultSubAgentId;
        _editFolderDefaultProjectPath = folder.DefaultProjectPath ?? "";
        _editFolderDirective        = folder.StandingDirective ?? "";
        _folderModalError           = null;
        _folderModalSaved           = false;
        _folderModal                = true;
        await Task.Yield();
    }

    internal void CloseFolderModal()
    {
        _folderModal      = false;
        _folderModalError = null;
        _folderModalSaved = false;
    }

    internal void OnFolderColorPicked(string hex) => _editFolderColor = hex;

    internal async Task CommitFolderAsync()
    {
        if (SessionState.CurrentUser == null) return;
        if (string.IsNullOrWhiteSpace(_editFolderName))
        {
            _folderModalError = "Name is required.";
            return;
        }

        _folderModalError = null;
        try
        {
            var color       = string.IsNullOrWhiteSpace(_editFolderColor) ? null : _editFolderColor.Trim();
            var projectPath = string.IsNullOrWhiteSpace(_editFolderDefaultProjectPath) ? null : _editFolderDefaultProjectPath.Trim();
            var directive   = string.IsNullOrWhiteSpace(_editFolderDirective) ? null : _editFolderDirective.Trim();

            if (_editingFolderId == null)
            {
                var created = await FolderService.CreateAsync(
                    SessionState.CurrentUser.Id,
                    _editFolderName,
                    color,
                    _editFolderDefaultAgentId,
                    projectPath,
                    directive);
                SessionState.FocusFolder(created.Id);
            }
            else
            {
                await FolderService.UpdateAsync(
                    SessionState.CurrentUser.Id,
                    _editingFolderId.Value,
                    _editFolderName,
                    color,
                    _editFolderDefaultAgentId,
                    projectPath,
                    directive);
            }

            _folders = await FolderService.GetListAsync(SessionState.CurrentUser.Id);
            _folderModalSaved = true;
            CloseFolderModal();
        }
        catch (Exception ex)
        {
            _folderModalError = $"Error: {ex.Message}";
        }
    }

    // ── Delete ────────────────────────────────────────────────────────────
    internal async Task RequestDeleteFolderAsync(CogitationFolder folder)
    {
        if (SessionState.CurrentUser == null) return;
        _folderPendingDeleteCount = await CogitationService.CountByFolderAsync(SessionState.CurrentUser.Id, folder.Id);
        _folderPendingDelete      = folder;
    }

    internal void CancelDeleteFolder() => _folderPendingDelete = null;

    internal async Task ConfirmDeleteFolderAsync()
    {
        if (_folderPendingDelete == null || SessionState.CurrentUser == null) return;

        await FolderService.DeleteAsync(SessionState.CurrentUser.Id, _folderPendingDelete.Id);
        if (SessionState.FocusedFolderId == _folderPendingDelete.Id)
            SessionState.FocusFolder(null);

        _folders             = await FolderService.GetListAsync(SessionState.CurrentUser.Id);
        _cogitations         = await CogitationService.GetListAsync(SessionState.CurrentUser.Id);
        _folderPendingDelete = null;
    }

    // ── File cogitation ───────────────────────────────────────────────────
    internal async Task FileCogitationAsync(Cogitation cog, int? folderId)
    {
        await CogitationService.MoveToFolderAsync(cog.Id, folderId);
        if (!string.IsNullOrEmpty(cog.OriginNodeId) && SessionState.CurrentUser is { } u)
        {
            _ = Task.Run(async () =>
            {
                await BridgeClient.UpdateFolderAsync(u.Id.ToString(), cog.Id, folderId, cog.OriginNodeId);
            });
        }
        if (SessionState.CurrentUser != null)
        {
            _cogitations = await CogitationService.GetListAsync(SessionState.CurrentUser.Id);
            _folders     = await FolderService.GetListAsync(SessionState.CurrentUser.Id);
        }
    }

    // ── Continue a cogitation on another bridge (cross-node migration) ────────
    // Copies the conversation's content to the chosen node, then repoints its origin so it continues
    // there. Requires both the current origin bridge and the target bridge to be online.
    internal int? _cogMigratingId;
    internal string? _cogMigrateMsg;

    internal async Task ContinueCogitationOnNodeAsync(Cogitation cog, string toNodeId)
    {
        if (SessionState.CurrentUser is not { } u) return;
        var userId   = u.Id.ToString();
        var fromNode = cog.OriginNodeId;
        var nodes    = BridgeRegistry.GetNodes(userId);

        if (!nodes.Any(n => n.NodeId == toNodeId))
        {
            _cogMigrateMsg = "Target bridge is offline.";
            return;
        }
        if (string.IsNullOrEmpty(fromNode) || !nodes.Any(n => n.NodeId == fromNode))
        {
            _cogMigrateMsg = "This conversation's origin bridge is offline — cannot copy it.";
            return;
        }

        _cogMigratingId = cog.Id;
        _cogMigrateMsg  = null;
        StateHasChanged();

        var ok = await BridgeClient.MigrateToNodeAsync(
            userId, userId, cog.Id, cog.Title, cog.AriaAvatarKey, cog.SubAgentId?.ToString(),
            cog.FolderId, fromNode!, toNodeId);

        if (ok)
        {
            await CogitationService.SetOriginNodeAsync(cog.Id, toNodeId);
            _cogitations   = await CogitationService.GetListAsync(u.Id);
            _cogMigrateMsg = null;
        }
        else
        {
            _cogMigrateMsg = "Copy failed — the conversation stays on its current bridge.";
        }

        _cogMigratingId = null;
        StateHasChanged();
    }

    internal async Task BulkFileCogitationsAsync(IEnumerable<int> cogitationIds, int? folderId)
    {
        if (SessionState.CurrentUser == null) return;

        var userId = SessionState.CurrentUser.Id.ToString();
        var cogs = _cogitations.Where(c => cogitationIds.Contains(c.Id)).ToList();

        foreach (var cog in cogs)
            await CogitationService.MoveToFolderAsync(cog.Id, folderId);

        foreach (var cog in cogs.Where(c => !string.IsNullOrEmpty(c.OriginNodeId)))
        {
            _ = Task.Run(async () =>
            {
                await BridgeClient.UpdateFolderAsync(userId, cog.Id, folderId, cog.OriginNodeId);
            });
        }

        _cogitations = await CogitationService.GetListAsync(SessionState.CurrentUser.Id);
        _folders     = await FolderService.GetListAsync(SessionState.CurrentUser.Id);
    }

    // ── New cogitation while folder focused ───────────────────────────────
    internal void NewCogitationInFocusedFolder()
    {
        ClosePanel();
        NewChat();
    }

    // ── Collapse persistence ──────────────────────────────────────────────
    internal async Task<bool> IsFolderCollapsedAsync(int folderId)
    {
        try
        {
            // No stored preference yet → collapsed, so a first-time view of the panel isn't a wall
            // of every cogitation across every dossier. Once a human explicitly expands/collapses a
            // dossier that choice is persisted and wins from then on.
            var stored = await JS.InvokeAsync<string?>("ariaInterop.getLocalStorage", FolderCollapseKey(folderId));
            return stored is null || stored == "1";
        }
        catch { return true; }
    }

    internal async Task SetFolderCollapsedAsync(int folderId, bool collapsed)
    {
        try
        {
            await JS.InvokeVoidAsync("ariaInterop.setLocalStorage", FolderCollapseKey(folderId), collapsed ? "1" : "0");
        }
        catch { }
    }
}
