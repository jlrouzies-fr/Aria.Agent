using System.Text.Json;
using Aria.Web.Services.Chat;
using Aria.Web.Services.Cogitations;
using Aria.Web.Services.ModelBridge;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Pages;

public sealed class ExplorerNode
{
    public required string Name { get; init; }
    public required string RelPath { get; init; }
    public required bool IsDir { get; init; }
    public List<ExplorerNode> Children { get; } = [];
}

public partial class Chat
{
    private bool _explorerCollapsed = true;
    private bool _explorerLoading;
    private bool _explorerTruncated;
    private bool _explorerAwaitingApproval;   // a bridge call is parked on the node-side 8h approval ceremony
    private string _explorerFilter = "";
    private List<ExplorerNode> _explorerRoot = [];
    private readonly HashSet<string> _explorerExpanded = new(StringComparer.Ordinal);
    private string? _explorerLoadedForPath;

    private bool _viewerOpen;
    private bool _viewerModalMode;   // false = docked side panel (default), true = centered modal
    private bool _viewerIsDiff;       // true when the viewer is showing a git diff instead of file content
    private bool _viewerDiffCreated;  // true when the diff viewer is showing a new/untracked file
    private bool _viewerDiffDeleted;  // true when the diff viewer is showing a deleted file
    private bool _viewerDiffStaged;   // true when the diff viewer is showing a staged diff
    private string? _viewerRelPath;
    private string? _viewerAbsPath;
    private string? _viewerContent;
    private bool _viewerLoading;
    private bool _viewerTruncated;
    private bool _viewerRevertConfirming;

    // Edit-mode state for the file viewer.
    private bool _viewerEditing;
    private bool _viewerDirty;
    private string? _viewerEditBuffer;
    private string? _viewerBaseHash;
    private bool _viewerSavePending;
    private bool _viewerSavedTick;
    private bool _viewerConflictDialog;
    private string? _viewerConflictContent;
    private string? _viewerConflictHash;
    private string? _viewerConflictDiff;

    // Tool calls already checked for a file mutation — reference-keyed (ToolCallInfo is a plain
    // class, so this is identity, not content, equality). A live turn's tool activity streams
    // through CogitationRun (see OnRunUpdated in Chat.Messaging.razor.cs), which only signals
    // "something changed", not what — so completed tool sections are scanned on every update and
    // this set stops the same completed call from re-triggering a refresh on the next signal.
    private readonly HashSet<ToolCallInfo> _refreshedToolCalls = [];

    // Explorer/viewer UI state survives a hard page refresh via localStorage (the same technique as
    // the resize-handle widths) — a refresh tears down the whole Blazor circuit, resetting every C#
    // field to default, so without this the panel and any open file would silently vanish on reload.
    // Restored once per circuit in OnAfterRenderAsync once SessionState.Projects is populated.
    private const string ExplorerCollapsedKey  = "ariaExplorerCollapsed";
    private const string ExplorerProjectKey    = "ariaExplorerActiveProject";
    private const string ExplorerViewerPathKey = "ariaExplorerViewerPath";
    private const string ExplorerViewerRelKey  = "ariaExplorerViewerRelPath";
    private const string ExplorerViewerModalKey = "ariaExplorerViewerModal";
    private bool _explorerStateRestored;

    private async Task SaveExplorerStateAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("ariaInterop.setLocalStorage", ExplorerCollapsedKey, _explorerCollapsed ? "1" : "0");
            // Read the field directly (not GetExplorerActiveProject()) — this must never silently promote a
            // transient null/mismatch to "the default project" and bake that into persisted state.
            await JS.InvokeVoidAsync("ariaInterop.setLocalStorage", ExplorerProjectKey, SessionState.ActiveProject?.Path);

            if (_viewerOpen && _viewerAbsPath != null && _viewerRelPath != null)
            {
                await JS.InvokeVoidAsync("ariaInterop.setLocalStorage", ExplorerViewerPathKey, _viewerAbsPath);
                await JS.InvokeVoidAsync("ariaInterop.setLocalStorage", ExplorerViewerRelKey, _viewerRelPath);
                await JS.InvokeVoidAsync("ariaInterop.setLocalStorage", ExplorerViewerModalKey, _viewerModalMode ? "1" : "0");
            }
            else
            {
                await JS.InvokeVoidAsync("ariaInterop.setLocalStorage", ExplorerViewerPathKey, null);
                await JS.InvokeVoidAsync("ariaInterop.setLocalStorage", ExplorerViewerRelKey, null);
            }
        }
        catch { }
    }

    /// <summary>Runs once per circuit, once projects are available, restoring the explorer's
    /// collapsed state, active project, and any open viewer file from localStorage. A page refresh
    /// is the only path that reaches this with real work to do — new-cogitation and cogitation-switch
    /// don't touch these fields, and a same-session re-render just no-ops via the guard flag.</summary>
    private async Task RestoreExplorerStateAsync()
    {
        string? collapsed, projectPath, viewerPath, viewerRel, viewerModal;
        try
        {
            collapsed   = await JS.InvokeAsync<string?>("ariaInterop.getLocalStorage", ExplorerCollapsedKey);
            projectPath = await JS.InvokeAsync<string?>("ariaInterop.getLocalStorage", ExplorerProjectKey);
            viewerPath  = await JS.InvokeAsync<string?>("ariaInterop.getLocalStorage", ExplorerViewerPathKey);
            viewerRel   = await JS.InvokeAsync<string?>("ariaInterop.getLocalStorage", ExplorerViewerRelKey);
            viewerModal = await JS.InvokeAsync<string?>("ariaInterop.getLocalStorage", ExplorerViewerModalKey);
        }
        catch { return; }

        if (collapsed == "0") _explorerCollapsed = false;

        // If a dossier default already pinned the project for this new chat, don't let localStorage
        // override it.
        if (_explorerProjectFromFolder)
        {
            if (!_explorerCollapsed) await LoadExplorerTreeAsync();
            await InvokeAsync(StateHasChanged);
            return;
        }

        var project = projectPath != null ? SessionState.Projects.FirstOrDefault(p => p.Path == projectPath) : null;
        await SetActiveProjectAsync(project);
        if (!_explorerCollapsed) await LoadExplorerTreeAsync();

        var userId = BridgeUserId();
        if (viewerPath != null && viewerRel != null && userId != null)
        {
            _viewerOpen      = true;
            _viewerModalMode = viewerModal == "1";
            _viewerRelPath   = viewerRel;
            _viewerAbsPath   = viewerPath;
            _viewerLoading   = true;
            await InvokeAsync(StateHasChanged);

            var result = await ProjectFiles.ReadFileAsync(userId, viewerPath, SessionState.AllowedProjectPaths, nodeId: SessionState.ActiveProject?.NodeId, sessionId: SessionState.SessionToken);
            _viewerContent   = result?.Content;
            _viewerTruncated = result?.Truncated ?? false;
            _viewerLoading   = false;
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task ToggleViewerMode()
    {
        _viewerModalMode = !_viewerModalMode;
        await SaveExplorerStateAsync();
    }

    // Reloads (tree or file) always show their loading overlay for at least this long — a real
    // fetch faster than this (typical case: local bridge, small file) would otherwise just flash,
    // which reads as a glitch rather than "it refreshed".
    private const int MinLoadingMs = 500;

    private static async Task EnsureMinLoadingTimeAsync(long startedAtTicks)
    {
        var remaining = MinLoadingMs - (Environment.TickCount64 - startedAtTicks);
        if (remaining > 0) await Task.Delay((int)remaining);
    }

    // A project-files call hit the bridge's Layer B gate and is parked on the node-side approval
    // ceremony (or just left it). Show a hint in the loading overlays so the wait isn't a mystery —
    // the approval page itself auto-opens on the node.
    private async void OnProjectFilesApprovalPending(string userId, bool awaiting)
    {
        if (userId != BridgeUserId()) return;
        _explorerAwaitingApproval = awaiting;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Scans a run's tool-activity sections for ones that just completed and haven't been
    /// handled yet, dispatching each to <see cref="HandleFileToolCompletedAsync"/>. Snapshots under
    /// the run's lock since <paramref name="msg"/>.Sections may be the same list a background
    /// thread is actively appending to.</summary>
    private async Task CheckForFileToolCompletionsAsync(CogitationRun run, MessageEntry msg)
    {
        List<MessageSection> snapshot;
        lock (run.Sync) { snapshot = [.. msg.Sections]; }

        foreach (var section in snapshot)
        {
            if (section.Type != MessageSection.SectionType.ToolActivity) continue;
            var tc = section.ToolCall;
            if (tc?.Result == null) continue;
            if (!_refreshedToolCalls.Add(tc)) continue;

            await HandleFileToolCompletedAsync(tc.Name, tc.ArgsJson);
        }
    }

    private async Task ToggleExplorerAsync()
    {
        _explorerCollapsed = !_explorerCollapsed;
        if (!_explorerCollapsed)
        {
            // Projects are bridge-authoritative and may not have been fetched yet if the bridge was
            // already connected when this circuit initialized (DirectBridgeRegistered fired before we
            // subscribed). Fetch them on demand when the Explorer is opened.
            if (SessionState.Projects.Count == 0 && BridgeUserId() is { } userId && BridgeRegistry.HasBridge(userId))
                await RefreshTerminalProjectsAsync();

            var project = GetExplorerActiveProject();
            if (project != null && _explorerLoadedForPath != project.Path)
                await LoadExplorerTreeAsync();
        }
        await SaveExplorerStateAsync();
    }

    private List<(string Value, string Label)> ExplorerProjectOptions()
    {
        var opts = new List<(string Value, string Label)> { ("", "— select project —") };
        opts.AddRange(SessionState.Projects.Select(p => (p.Path, p.Name)));
        return opts;
    }

    private async Task OnExplorerProjectSelectedAsync(string? path)
    {
        var project = string.IsNullOrWhiteSpace(path)
            ? null
            : SessionState.Projects.FirstOrDefault(p => p.Path == path);
        await SetActiveProjectAsync(project);   // scopes the agent's Terminal tools to this project
        await LoadExplorerTreeAsync();
        await SaveExplorerStateAsync();
    }

    /// <summary>Returns the active project if it is still in the current project list, else null.
    /// Unlike the file-picker helper this does NOT fall back to the first project — "no project"
    /// is a valid state and must stay in sync with the Terminal and agent context.</summary>
    private TerminalProject? GetExplorerActiveProject() =>
        SessionState.ActiveProject is { } p && SessionState.Projects.Any(x => x.Path == p.Path) ? p : null;

    /// <summary>Loads the tree for the active project. <paramref name="forceMinDelay"/> is true only
    /// for the agent-triggered auto-refresh path (<see cref="HandleFileToolCompletedAsync"/>), where
    /// the loading overlay's whole purpose is to visibly confirm "this just changed" — a manual open
    /// or refresh-button click should just be as fast as the bridge allows, no fake delay.</summary>
    private async Task LoadExplorerTreeAsync(bool forceMinDelay = false)
    {
        var project = GetExplorerActiveProject();
        if (project == null)
        {
            _explorerRoot = [];
            return;
        }

        var userId = BridgeUserId();
        if (userId == null) return;

        _explorerLoading = true;
        _explorerExpanded.Clear();
        await InvokeAsync(StateHasChanged);
        var startedAt = Environment.TickCount64;

        var tree = await ProjectFiles.ListTreeAsync(userId, project.Path, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);
        if (forceMinDelay) await EnsureMinLoadingTimeAsync(startedAt);
        _explorerLoadedForPath = project.Path;
        _explorerTruncated = tree?.Truncated ?? false;
        _explorerRoot = tree != null ? BuildExplorerTree(tree.Files, tree.Dirs) : [];
        _explorerLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    private static List<ExplorerNode> BuildExplorerTree(List<string> files, List<string> dirs)
    {
        var nodes = new Dictionary<string, ExplorerNode>(StringComparer.Ordinal);
        var root = new List<ExplorerNode>();

        static string? ParentOf(string relPath)
        {
            var idx = relPath.LastIndexOf('/');
            return idx < 0 ? null : relPath[..idx];
        }

        ExplorerNode GetOrCreateDir(string relPath)
        {
            if (nodes.TryGetValue(relPath, out var existing)) return existing;
            var node = new ExplorerNode { Name = Path.GetFileName(relPath), RelPath = relPath, IsDir = true };
            nodes[relPath] = node;
            var parentPath = ParentOf(relPath);
            (parentPath == null ? root : GetOrCreateDir(parentPath).Children).Add(node);
            return node;
        }

        foreach (var d in dirs) GetOrCreateDir(d);

        foreach (var f in files)
        {
            var parentPath = ParentOf(f);
            var siblings = parentPath == null ? root : GetOrCreateDir(parentPath).Children;
            siblings.Add(new ExplorerNode { Name = Path.GetFileName(f), RelPath = f, IsDir = false });
        }

        void SortChildren(List<ExplorerNode> list)
        {
            list.Sort((a, b) => a.IsDir != b.IsDir
                ? (a.IsDir ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var n in list) if (n.IsDir) SortChildren(n.Children);
        }
        SortChildren(root);

        return root;
    }

    private void ToggleExplorerDir(ExplorerNode node)
    {
        if (!_explorerExpanded.Add(node.RelPath)) _explorerExpanded.Remove(node.RelPath);
    }

    /// <summary>Flattened, depth-first list of files matching the filter — VS Code search style —
    /// used instead of the tree while the filter box is non-empty.</summary>
    private List<ExplorerNode> FilteredExplorerFiles()
    {
        var results = new List<ExplorerNode>();
        void Walk(List<ExplorerNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (!n.IsDir && n.RelPath.Contains(_explorerFilter, StringComparison.OrdinalIgnoreCase))
                    results.Add(n);
                if (n.IsDir) Walk(n.Children);
                if (results.Count >= 300) return;
            }
        }
        Walk(_explorerRoot);
        return results;
    }

    private static string CombineProjectPath(TerminalProject project, string relPath)
    {
        var isWindows = string.Equals(project.Platform, "Windows", StringComparison.OrdinalIgnoreCase);
        var normalizedRel = isWindows ? relPath.Replace('/', '\\') : relPath;
        var sep = isWindows ? '\\' : '/';
        return project.Path.TrimEnd('/', '\\') + sep + normalizedRel;
    }

    private async Task OpenExplorerFileAsync(ExplorerNode node)
    {
        var project = GetExplorerActiveProject();
        var userId = BridgeUserId();
        if (project == null || userId == null) return;

        var absPath = CombineProjectPath(project, node.RelPath);
        if (_viewerEditing && _viewerDirty)
        {
            var abandon = await JS.InvokeAsync<bool>("confirm", "You have unsaved edits. Discard them and open a different file?");
            if (!abandon) return;
        }

        _viewerOpen = true;
        _viewerIsDiff = false;
        _viewerDiffCreated = false;
        _viewerDiffDeleted = false;
        _viewerDiffStaged = false;
        _viewerRevertConfirming = false;
        _viewerEditing = false;
        _viewerDirty = false;
        _viewerEditBuffer = null;
        _viewerBaseHash = null;
        _viewerConflictDialog = false;
        _viewerConflictContent = null;
        _viewerConflictHash = null;
        _viewerConflictDiff = null;
        _viewerRelPath = node.RelPath;
        _viewerAbsPath = absPath;
        _viewerContent = null;
        _viewerLoading = true;
        _viewerTruncated = false;
        await InvokeAsync(StateHasChanged);

        // User-initiated open: no forced minimum — the overlay only shows if the bridge is
        // genuinely slow, unlike the agent-triggered auto-refresh in ReloadViewerContentAsync.
        var result = await ProjectFiles.ReadFileAsync(userId, absPath, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);
        _viewerContent = result?.Content;
        _viewerTruncated = result?.Truncated ?? false;
        _viewerBaseHash = result?.Hash;
        _viewerLoading = false;
        await InvokeAsync(StateHasChanged);
        await SaveExplorerStateAsync();
    }

    private async Task CloseExplorerViewer()
    {
        if (_viewerEditing && _viewerDirty)
        {
            var abandon = await JS.InvokeAsync<bool>("confirm", "You have unsaved edits. Discard them and close the viewer?");
            if (!abandon) return;
        }

        _viewerOpen = false;
        _viewerIsDiff = false;
        _viewerDiffCreated = false;
        _viewerDiffDeleted = false;
        _viewerDiffStaged = false;
        _viewerRevertConfirming = false;
        _viewerEditing = false;
        _viewerDirty = false;
        _viewerEditBuffer = null;
        _viewerBaseHash = null;
        _viewerConflictDialog = false;
        _viewerConflictContent = null;
        _viewerConflictHash = null;
        _viewerConflictDiff = null;
        _viewerRelPath = null;
        _viewerAbsPath = null;
        _viewerContent = null;
        await SaveExplorerStateAsync();
    }

    private void BeginEdit()
    {
        if (_viewerTruncated || _viewerContent == null) return;
        _viewerEditing = true;
        _viewerEditBuffer = _viewerContent;
        _viewerDirty = false;
        _viewerConflictDialog = false;
        _viewerConflictContent = null;
        _viewerConflictHash = null;
    }

    private void OnEditInput(ChangeEventArgs e)
    {
        _viewerEditBuffer = e.Value?.ToString() ?? "";
        _viewerDirty = _viewerEditBuffer != _viewerContent;
    }

    private async Task CancelEditAsync()
    {
        if (_viewerDirty)
        {
            var abandon = await JS.InvokeAsync<bool>("confirm", "You have unsaved edits. Discard them?");
            if (!abandon) return;
        }

        _viewerEditing = false;
        _viewerDirty = false;
        _viewerEditBuffer = null;
        _viewerConflictDialog = false;
        _viewerConflictContent = null;
        _viewerConflictHash = null;
        _viewerConflictDiff = null;
    }

    private async Task SaveEditAsync()
    {
        if (!_viewerEditing || _viewerEditBuffer == null || _viewerAbsPath == null || _viewerBaseHash == null) return;

        _viewerSavePending = true;
        _viewerConflictDialog = false;
        await InvokeAsync(StateHasChanged);

        var project = GetExplorerActiveProject();
        var userId = BridgeUserId();
        if (project == null || userId == null)
        {
            _viewerSavePending = false;
            await InvokeAsync(StateHasChanged);
            return;
        }

        var result = await ProjectFiles.WriteFileAsync(
            userId, _viewerAbsPath, _viewerEditBuffer, _viewerBaseHash, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);

        _viewerSavePending = false;

        if (result?.Conflict == true)
        {
            _viewerConflictContent = result.CurrentContent;
            _viewerConflictHash = result.CurrentHash;
            _viewerConflictDialog = true;
            _viewerConflictDiff = result.Diff;
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (result?.Success == true)
        {
            _viewerContent = _viewerEditBuffer;
            _viewerBaseHash = result.Hash;
            _viewerDirty = false;
            _viewerEditing = false;
            _viewerEditBuffer = null;
            _viewerSavedTick = true;
            await InvokeAsync(StateHasChanged);

            // Refresh the changes tab / tree so the new dirty state is visible.
            if (_changesTabActive && GetExplorerActiveProject() is { } cp && _changesLoadedForPath == cp.Path)
                _ = LoadChangesAsync();
            else
                _changesNeedRefresh = true;

            _ = LoadExplorerTreeAsync();

            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                _viewerSavedTick = false;
                await InvokeAsync(StateHasChanged);
            });
            return;
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task ResolveConflictAsync(bool overwrite)
    {
        if (overwrite)
        {
            _viewerBaseHash = _viewerConflictHash;
            _viewerConflictDialog = false;
            _viewerConflictContent = null;
            _viewerConflictHash = null;
            _viewerConflictDiff = null;
            await SaveEditAsync();
        }
        else
        {
            // Reload their version and drop out of edit mode.
            _viewerConflictDialog = false;
            _viewerConflictContent = null;
            _viewerConflictHash = null;
            _viewerConflictDiff = null;
            _viewerEditing = false;
            _viewerDirty = false;
            _viewerEditBuffer = null;
            await ReloadViewerContentAsync();
        }
    }

    private async Task OnEditKeyDownAsync(KeyboardEventArgs e)
    {
        if (e.Key == "s" && (e.CtrlKey || e.MetaKey))
        {
            await SaveEditAsync();
        }
        else if (e.Key == "Escape")
        {
            await CancelEditAsync();
        }
    }

    private async Task AddExplorerFileReferenceAsync(ExplorerNode node)
    {
        var project = GetExplorerActiveProject();
        if (project == null) return;

        var absPath = CombineProjectPath(project, node.RelPath);
        if (_referencedFiles.All(f => f.AbsPath != absPath))
            _referencedFiles.Add(new ProjectFileEntry(node.RelPath, absPath));

        var sep = _input.Length > 0 && !_input.EndsWith(' ') ? " " : "";
        _input = $"{_input}{sep}#{node.RelPath} ";
        await FocusInputAsync();
        await InvokeAsync(StateHasChanged);
    }

    // ── Auto-refresh on agent file mutations ──────────────────────────────────

    // Shell command tokens (bash/zsh/sh, cmd.exe, PowerShell) that plausibly move/create/delete
    // files. Backstop for bash_exec: the dedicated tools below give an exact path, this doesn't.
    private static readonly string[] MutatingCommandTokens =
    [
        "rm", "rmdir", "mkdir", "md", "mv", "cp", "touch", "unlink", "trash", "rsync",
        "del", "erase", "rd", "move", "ren", "rename", "copy", "xcopy", "robocopy",
        "remove-item", "new-item", "move-item", "rename-item", "copy-item"
    ];

    // Checks only the first token of each command segment (split on ; & | and newlines) against
    // the known list, so e.g. "eslint file.md" or "cat readme.md" don't false-positive on "md".
    private static bool LooksLikeFileMutatingCommand(string command)
    {
        foreach (var seg in command.Split([';', '\n', '&', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed  = seg.TrimStart();
            var spaceIdx = trimmed.IndexOfAny([' ', '\t']);
            var head     = spaceIdx < 0 ? trimmed : trimmed[..spaceIdx];
            var lastSlash = head.LastIndexOfAny(['/', '\\']);
            if (lastSlash >= 0) head = head[(lastSlash + 1)..];
            if (MutatingCommandTokens.Contains(head, StringComparer.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string NormalizeAbsPath(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static bool PathMatchesOrIsUnder(string candidate, string touched)
    {
        var c = NormalizeAbsPath(candidate);
        var t = NormalizeAbsPath(touched);
        return string.Equals(c, t, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(t + "/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ReloadViewerContentAsync()
    {
        var project = GetExplorerActiveProject();
        var userId  = BridgeUserId();
        if (project == null || userId == null || _viewerAbsPath == null || _viewerIsDiff) return;

        // If the user is actively editing and has unsaved changes, don't clobber them with a
        // background reload from an agent mutation. The save flow refreshes explicitly on success.
        if (_viewerEditing && _viewerDirty) return;

        _viewerLoading = true;
        await InvokeAsync(StateHasChanged);
        var startedAt = Environment.TickCount64;

        var result = await ProjectFiles.ReadFileAsync(userId, _viewerAbsPath, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);
        await EnsureMinLoadingTimeAsync(startedAt);
        _viewerContent   = result?.Content;
        _viewerTruncated = result?.Truncated ?? false;
        _viewerBaseHash  = result?.Hash;
        if (_viewerEditing)
        {
            _viewerEditBuffer = _viewerContent;
            _viewerDirty = false;
        }
        _viewerLoading   = false;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Called after every completed tool call. Routes by what the tool actually does:
    /// edit_file only changes content, never structure, so it just reloads the open file view — no
    /// reason to touch the tree. write_file can also create a brand-new file, so it reloads both.
    /// create_dir/delete_file/delete_dir/move_path change the tree's shape, so they refresh the tree
    /// — and if the open viewer's file was the thing removed or moved, it's closed rather than
    /// followed to wherever it ended up (a silently-relocated viewer is more confusing than one that
    /// just closes). bash_exec is a best-effort heuristic since its target path can't be parsed
    /// reliably: it refreshes the tree blind and, if the open file turns out to be unreadable
    /// afterward, closes it — same "gone" outcome as the structural ops, just detected after the fact.</summary>
    private async Task HandleFileToolCompletedAsync(string toolName, string argsJson)
    {
        var project = GetExplorerActiveProject();
        if (project == null || _explorerLoadedForPath != project.Path) return;

        string? touchedPath = null;
        var refreshTree  = false;
        var refreshBlind = false;   // bash_exec: unknown path — refresh tree blind, probe the viewer
        var closeIfMoved = false;   // structural ops: close the viewer rather than follow it

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;

            switch (toolName)
            {
                case "edit_file":
                    if (root.TryGetProperty("path", out var pEdit)) touchedPath = pEdit.GetString();
                    break;

                case "write_file":
                    if (root.TryGetProperty("path", out var pWrite)) touchedPath = pWrite.GetString();
                    refreshTree = true;
                    break;

                case "create_dir":
                    if (root.TryGetProperty("path", out var pCreate)) touchedPath = pCreate.GetString();
                    refreshTree = true;
                    break;

                case "delete_file" or "delete_dir":
                    if (root.TryGetProperty("path", out var pDel)) touchedPath = pDel.GetString();
                    refreshTree  = true;
                    closeIfMoved = true;
                    break;

                case "move_path":
                    if (root.TryGetProperty("source", out var s)) touchedPath = s.GetString();
                    refreshTree  = true;
                    closeIfMoved = true;
                    break;

                case "bash_exec":
                    var command = root.TryGetProperty("command", out var c) ? c.GetString() : null;
                    if (command == null || !LooksLikeFileMutatingCommand(command)) return;
                    refreshTree  = true;
                    refreshBlind = true;
                    break;

                default:
                    return;
            }
        }
        catch { return; }

        if (!refreshBlind && (touchedPath == null || !PathMatchesOrIsUnder(touchedPath, project.Path))) return;

        if (refreshTree)
            await LoadExplorerTreeAsync(forceMinDelay: true);

        if (!_viewerOpen || _viewerAbsPath == null) return;

        var viewerAffected = refreshBlind || (touchedPath != null && PathMatchesOrIsUnder(_viewerAbsPath, touchedPath));
        if (!viewerAffected) return;

        if (closeIfMoved)
        {
            await CloseExplorerViewer();
            await InvokeAsync(StateHasChanged);
            return;
        }

        await ReloadViewerContentAsync();

        // bash_exec is ambiguous — if the reload comes back empty, the file is most likely gone
        // rather than actually empty, so close it (same outcome as the structural ops above).
        if (refreshBlind && _viewerContent == null)
        {
            await CloseExplorerViewer();
            await InvokeAsync(StateHasChanged);
        }

        // File-tool mutations may also affect the Changes tab. Refresh it lazily: if the tab is
        // active now, reload; otherwise set a dirty flag so it refreshes when next opened.
        if (_changesTabActive && GetExplorerActiveProject() is { } cp && _changesLoadedForPath == cp.Path)
        {
            _ = LoadChangesAsync();
        }
        else
        {
            _changesNeedRefresh = true;
        }
    }

    // ── Changes tab (git working state) ───────────────────────────────────────

    private bool _changesTabActive;
    private bool _changesLoading;
    private string? _changesError;
    private string _changesBranch = "";
    private int _changesAhead;
    private int _changesBehind;
    private List<ProjectGitStatusEntry> _changesEntries = [];
    private string _commitMessage = "";
    private readonly HashSet<string> _dirtyPaths = new(StringComparer.Ordinal);
    private string? _changesLoadedForPath;
    private bool _changesNeedRefresh = true;

    private string? _discardConfirmPath;

    private bool _draftingCommitMessage;

    private int StagedCount => _changesEntries.Count(e => e.Staged);
    private int UnstagedCount => _changesEntries.Count(e => !e.Staged);
    private int DirtyCount => _changesEntries.Count;

    private async Task SetChangesTabAsync(bool changes)
    {
        _changesTabActive = changes;
        if (changes)
        {
            var project = GetExplorerActiveProject();
            if (project != null && (_changesNeedRefresh || _changesLoadedForPath != project.Path))
                await LoadChangesAsync();
        }
        await InvokeAsync(StateHasChanged);
    }

    private async Task LoadChangesAsync()
    {
        var project = GetExplorerActiveProject();
        if (project == null)
        {
            _changesEntries = [];
            _dirtyPaths.Clear();
            _changesLoadedForPath = null;
            _changesNeedRefresh = false;
            return;
        }

        var userId = BridgeUserId();
        if (userId == null) return;

        _changesLoading = true;
        _changesError = null;
        await InvokeAsync(StateHasChanged);

        var status = await ProjectFiles.GetGitStatusAsync(
            userId, project.Path, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);

        _changesBranch = status?.Branch ?? "";
        _changesAhead = status?.Ahead ?? 0;
        _changesBehind = status?.Behind ?? 0;
        _changesEntries = status?.Entries ?? [];
        _changesLoadedForPath = project.Path;
        _changesNeedRefresh = false;

        if (status == null)
            _changesError = "Could not read git status. Is this a git repository?";
        else if (!string.IsNullOrWhiteSpace(status.Error))
            _changesError = status.Error;

        ComputeDirtyPaths();
        _changesLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    private void ComputeDirtyPaths()
    {
        _dirtyPaths.Clear();
        foreach (var e in _changesEntries)
        {
            _dirtyPaths.Add(e.Path);
            if (!string.IsNullOrEmpty(e.OriginalPath))
                _dirtyPaths.Add(e.OriginalPath);
        }
    }

    private async Task OpenExplorerDiffAsync(ProjectGitStatusEntry entry)
    {
        var project = GetExplorerActiveProject();
        var userId = BridgeUserId();
        if (project == null || userId == null) return;

        _viewerOpen = true;
        _viewerRelPath = entry.Path;
        _viewerAbsPath = CombineProjectPath(project, entry.Path);
        _viewerContent = null;
        _viewerLoading = true;
        _viewerTruncated = false;
        _viewerIsDiff = true;
        _viewerDiffCreated = entry.StatusCode == "??";
        _viewerDiffDeleted = entry.StatusCode[1] == 'D';
        _viewerDiffStaged = entry.Staged;
        _viewerRevertConfirming = false;
        await InvokeAsync(StateHasChanged);

        var result = await ProjectFiles.GetGitDiffAsync(
            userId, project.Path, entry.Path, entry.Staged, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);

        _viewerContent = result?.Output;
        _viewerTruncated = result?.Truncated ?? false;

        // Untracked files have no git diff; read the file directly so the viewer can render it as new.
        if (string.IsNullOrEmpty(_viewerContent) && _viewerDiffCreated)
        {
            var read = await ProjectFiles.ReadFileAsync(userId, _viewerAbsPath, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);
            _viewerContent = read?.Content;
            _viewerTruncated = read?.Truncated ?? false;
        }

        _viewerLoading = false;
        await InvokeAsync(StateHasChanged);
        await SaveExplorerStateAsync();
    }

    private async Task StageEntryAsync(ProjectGitStatusEntry entry)
    {
        if (entry.Staged) return;
        await StagePathsAsync([entry.Path]);
    }

    private async Task UnstageEntryAsync(ProjectGitStatusEntry entry)
    {
        if (!entry.Staged) return;
        await UnstagePathsAsync([entry.Path]);
    }

    private async Task StageAllAsync()
    {
        var paths = _changesEntries.Where(e => !e.Staged).Select(e => e.Path).ToList();
        if (paths.Count > 0) await StagePathsAsync(paths);
    }

    private async Task UnstageAllAsync()
    {
        var paths = _changesEntries.Where(e => e.Staged).Select(e => e.Path).ToList();
        if (paths.Count > 0) await UnstagePathsAsync(paths);
    }

    private async Task StagePathsAsync(List<string> paths)
    {
        var project = GetExplorerActiveProject();
        var userId = BridgeUserId();
        if (project == null || userId == null || paths.Count == 0) return;

        _changesLoading = true;
        await InvokeAsync(StateHasChanged);

        var result = await ProjectFiles.StageAsync(
            userId, project.Path, paths, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);

        if (result?.Success != true)
            _changesError = result?.Error ?? "Stage failed";

        await LoadChangesAsync();
    }

    private async Task UnstagePathsAsync(List<string> paths)
    {
        var project = GetExplorerActiveProject();
        var userId = BridgeUserId();
        if (project == null || userId == null || paths.Count == 0) return;

        _changesLoading = true;
        await InvokeAsync(StateHasChanged);

        var result = await ProjectFiles.UnstageAsync(
            userId, project.Path, paths, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);

        if (result?.Success != true)
            _changesError = result?.Error ?? "Unstage failed";

        await LoadChangesAsync();
    }

    private void BeginDiscard(ProjectGitStatusEntry entry)
    {
        _discardConfirmPath = entry.Path;
    }

    private void CancelDiscard()
    {
        _discardConfirmPath = null;
    }

    private async Task ConfirmDiscardAsync(ProjectGitStatusEntry entry)
    {
        if (_discardConfirmPath != entry.Path)
            return;

        var project = GetExplorerActiveProject();
        var userId = BridgeUserId();
        if (project == null || userId == null) return;

        _discardConfirmPath = null;
        _changesLoading = true;
        await InvokeAsync(StateHasChanged);

        var result = await ProjectFiles.DiscardAsync(
            userId, project.Path, entry.Path, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);

        if (result?.Success != true)
            _changesError = result?.Error ?? "Discard failed";

        await LoadChangesAsync();
    }

    private async Task CommitAsync()
    {
        var project = GetExplorerActiveProject();
        var userId = BridgeUserId();
        if (project == null || userId == null || StagedCount == 0 || string.IsNullOrWhiteSpace(_commitMessage))
            return;

        _changesLoading = true;
        await InvokeAsync(StateHasChanged);

        var result = await ProjectFiles.CommitAsync(
            userId, project.Path, _commitMessage.Trim(), SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);

        if (result?.Success == true)
            _commitMessage = "";
        else
            _changesError = result?.Error ?? "Commit failed";

        await LoadChangesAsync();
    }

    private async Task DraftCommitMessageAsync()
    {
        if (_draftingCommitMessage) return;
        var project = GetExplorerActiveProject();
        var userId = BridgeUserId();
        if (project == null || userId == null || StagedCount == 0) return;
        if (_agent == null || _session == null || SessionState.CurrentUser == null)
        {
            _changesError = "Start a conversation or select a channel to draft a commit message.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        _draftingCommitMessage = true;
        _commitMessage = "";
        await InvokeAsync(StateHasChanged);

        try
        {
            const int MaxDiffChars = 8000;
            var diffBuilder = new System.Text.StringBuilder();
            foreach (var entry in _changesEntries.Where(e => e.Staged))
            {
                var diff = await ProjectFiles.GetGitDiffAsync(
                    userId, project.Path, entry.Path, staged: true, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);
                if (diff == null) continue;
                diffBuilder.AppendLine($"--- {entry.Path} ---");
                diffBuilder.AppendLine(diff.Output);
                if (diffBuilder.Length > MaxDiffChars) break;
            }

            var stagedDiff = diffBuilder.Length > MaxDiffChars
                ? diffBuilder.ToString(0, MaxDiffChars) + "\n…"
                : diffBuilder.ToString();

            if (string.IsNullOrWhiteSpace(stagedDiff))
            {
                _commitMessage = "chore: update files";
                return;
            }

            var prompt =
                "Write a single git commit message for the staged diff below. " +
                "Use conventional commits style. Subject line only, maximum 72 characters. " +
                "Only add a body if the diff addresses multiple distinct concerns, and keep it brief.\n\n" +
                stagedDiff;

            var text = await StreamHeadlessAsync(prompt);
            _commitMessage = SanitizeCommitMessage(text);
        }
        catch (Exception ex)
        {
            _changesError = $"Draft failed: {ex.Message}";
        }
        finally
        {
            _draftingCommitMessage = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private static string SanitizeCommitMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var first = lines.FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(first)) return "";
        // Remove markdown code fences and backticks that models sometimes wrap messages in.
        first = first.Trim('`').Trim();
        if (first.StartsWith("commit:", StringComparison.OrdinalIgnoreCase))
            first = first["commit:".Length..].Trim();
        if (first.Length > 72)
            first = first[..72];
        return first;
    }

    private async Task<string> StreamHeadlessAsync(string prompt)
    {
        if (_agent == null || _session == null || SessionState.CurrentUser == null)
            throw new InvalidOperationException("No active agent session");

        var capturedAgent = _agent;
        var capturedSession = await capturedAgent.CreateSessionAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var builder = new System.Text.StringBuilder();

        await foreach (var token in AgentService.StreamAsync(
            prompt, capturedAgent, capturedSession, timeout.Token, OnUsage))
        {
            builder.Append(token);
        }

        return builder.ToString();
    }

    private static string GetStatusBadgeClass(string code) => code switch
    {
        "??" => "git-status-untracked",
        "!!" => "git-status-ignored",
        _ => code[0] == 'A' ? "git-status-added"
            : code[0] == 'M' ? "git-status-modified"
            : code[0] == 'D' ? "git-status-deleted"
            : code[0] == 'R' ? "git-status-renamed"
            : code[1] == 'M' ? "git-status-modified"
            : code[1] == 'D' ? "git-status-deleted"
            : "git-status-unknown"
    };

    private static string GetStatusBadgeText(string code) => code switch
    {
        "??" => "??",
        "!!" => "!!",
        _ when code[0] == 'A' => "A",
        _ when code[0] == 'M' => "M",
        _ when code[0] == 'D' => "D",
        _ when code[0] == 'R' => "R",
        _ when code[1] == 'M' => "M",
        _ when code[1] == 'D' => "D",
        _ => "?"
    };

    private async Task RefreshActiveExplorerTabAsync()
    {
        if (_changesTabActive)
            await LoadChangesAsync();
        else
            await LoadExplorerTreeAsync();
    }

    private void BeginViewerRevert()
    {
        if (!_viewerIsDiff || string.IsNullOrEmpty(_viewerRelPath)) return;
        _viewerRevertConfirming = true;
    }

    private void CancelViewerRevert()
    {
        _viewerRevertConfirming = false;
    }

    private async Task ConfirmViewerRevertAsync()
    {
        if (!_viewerIsDiff || string.IsNullOrEmpty(_viewerRelPath)) return;

        var project = GetExplorerActiveProject();
        var userId = BridgeUserId();
        if (project == null || userId == null) return;

        _viewerRevertConfirming = false;
        _viewerLoading = true;
        await InvokeAsync(StateHasChanged);

        if (_viewerDiffStaged)
        {
            // For staged diffs, unstage first then discard working changes.
            await ProjectFiles.UnstageAsync(userId, project.Path, [_viewerRelPath], SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);
        }

        var result = await ProjectFiles.DiscardAsync(userId, project.Path, _viewerRelPath, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);

        if (result?.Success != true)
            _changesError = result?.Error ?? "Revert failed";

        // Refresh the diff if the file still has changes, otherwise close the viewer.
        if (_viewerRelPath != null)
        {
            var entry = _changesEntries.FirstOrDefault(e => e.Path == _viewerRelPath);
            if (entry != null)
            {
                await OpenExplorerDiffAsync(entry);
            }
            else
            {
                await CloseExplorerViewer();
                await InvokeAsync(StateHasChanged);
            }
        }

        await LoadChangesAsync();
    }

    private int CountDiffAdds(string? diff)
    {
        if (string.IsNullOrEmpty(diff)) return 0;
        if (_viewerDiffCreated) return diff.ReplaceLineEndings("\n").Split('\n').Length;
        return diff.CountLinesStartingWith('+');
    }

    private int CountDiffDels(string? diff)
    {
        if (string.IsNullOrEmpty(diff)) return 0;
        if (_viewerDiffDeleted) return diff.ReplaceLineEndings("\n").Split('\n').Length;
        return diff.CountLinesStartingWith('-');
    }

    private async Task OpenDiffFileFromViewerAsync()
    {
        if (_viewerAbsPath == null || _viewerRelPath == null) return;
        var project = GetExplorerActiveProject();
        if (project == null) return;
        _viewerOpen = true;
        _viewerIsDiff = false;
        _viewerLoading = true;
        _viewerContent = null;
        await InvokeAsync(StateHasChanged);

        var userId = BridgeUserId();
        if (userId != null)
        {
            var result = await ProjectFiles.ReadFileAsync(userId, _viewerAbsPath, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);
            _viewerContent = result?.Content;
            _viewerTruncated = result?.Truncated ?? false;
        }
        _viewerLoading = false;
        await InvokeAsync(StateHasChanged);
        await SaveExplorerStateAsync();
    }
}

internal static class DiffCountingExtensions
{
    public static int CountLinesStartingWith(this string? text, char c)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return lines.Count(l => l.Length > 0 && l[0] == c);
    }
}
