using System.Text.RegularExpressions;
using Aria.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Pages;

public partial class Chat
{
    [Inject] private ProjectFilesClient ProjectFiles { get; set; } = null!;

    private DotNetObjectReference<Chat>? _pickerRef;

    private bool _commandPaletteOpen;
    private bool _filePickerOpen;
    private bool _projectPickerOpen;
    private bool _gitPickerOpen;
    private bool AnyPickerOpen => _commandPaletteOpen || _filePickerOpen || _projectPickerOpen || _gitPickerOpen;

    private static readonly string[] GitModes = ["diff", "status", "log"];

    private List<ProjectFileEntry> _fileResults    = [];
    private List<SlashCommand>     _commandResults = [];
    private List<string>           _gitModeResults = [];
    private List<(string Path, string Name)> _projectPickerOptions = [];
    private int    _pickerSel;
    private int    _tokenStart = -1;        // index of the active '#' in _input
    private CancellationTokenSource? _pickerCts;

    // True while a file listing is in flight (e.g. project lives on a remote bridge node and the
    // round trip is slow). The picker keeps showing the previous results underneath so it doesn't
    // flash empty; this only drives a small "Loading…" hint.
    private bool _filePickerLoading;

    // Files the user referenced via "#"; shown as chips and resolved to content on send.
    private readonly List<ProjectFileEntry> _referencedFiles = [];

    // Matches a "#path" token at the start of input or after whitespace (path has no spaces/#).
    private static readonly Regex _refTokenRx = new(@"(?<=^|\s)#([^\s#]+)", RegexOptions.Compiled);

    private sealed record SlashCommand(string Name, string Description);

    // The "/" palette is the live subset of ChatCatalog — only commands wired up today.
    // The full catalog (incl. planned ones) is documented in the left-menu INDEX panel.
    private static readonly SlashCommand[] AllCommands =
        ChatCatalog.Commands
            .Where(c => c.Status == CatalogStatus.Available)
            .Select(c => new SlashCommand(c.Name, c.Description))
            .ToArray();

    private async Task InitChatInputInteropAsync()
    {
        _pickerRef ??= DotNetObjectReference.Create(this);
        try { await JS.InvokeVoidAsync("ariaInterop.initChatInput", "chatInput", _pickerRef); } catch { }
        // Auto-grow + hybrid expand: lift the textarea into its own row above the control strip when
        // it wraps, instead of inflating the whole input bar. Idempotent (JS guards via _composerInit).
        try { await JS.InvokeVoidAsync("ariaInterop.initChatComposer", "chatInput"); } catch { }
    }

    /// <summary>Re-evaluates the input for a "/command", or a trailing "#fragment" token, opening
    /// or refreshing the matching popup. Called from OnInput after _input changes.</summary>
    private async Task UpdatePickersAsync()
    {
        var text = _input;

        // "/command" palette — the whole input is a single "/word" being typed.
        if (text.StartsWith('/') && !text.Contains(' '))
        {
            OpenCommandPalette(text);
            await InvokeAsync(StateHasChanged);
            return;
        }

        // Trailing "#fragment": at start or preceded by whitespace, no whitespace after the '#'.
        var hash = text.LastIndexOf('#');
        if (hash >= 0 && (hash == 0 || char.IsWhiteSpace(text[hash - 1])))
        {
            var frag = text[(hash + 1)..];
            if (!frag.Contains(' ') && !frag.Contains('#'))
            {
                _tokenStart = hash;

                // "folder:"/"dir:" reference a directory tree directly by typed path — there's
                // nothing to fuzzy-search, so no picker popup; the user just keeps typing.
                if (frag.StartsWith("folder:", StringComparison.OrdinalIgnoreCase) ||
                    frag.StartsWith("dir:", StringComparison.OrdinalIgnoreCase))
                {
                    await ClosePickersAsync();
                    return;
                }

                // "git:" picks from a fixed, tiny set of modes — no fuzzy file search needed.
                if (frag.StartsWith("git:", StringComparison.OrdinalIgnoreCase))
                {
                    OpenGitPicker(frag["git:".Length..]);
                    await InvokeAsync(StateHasChanged);
                    return;
                }

                await OpenFilePickerAsync(frag);
                return;
            }
        }

        await ClosePickersAsync();
    }

    private void OpenCommandPalette(string typed)
    {
        // Match on the command name prefix, or — once past the leading "/" — anywhere in the name,
        // so "/mc" surfaces "/mcp" and "ag" surfaces "/agents".
        var needle = typed.TrimStart('/');
        _commandResults = AllCommands
            .Where(c => c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
                     || (needle.Length > 0 && c.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (_commandResults.Count == 0) { _ = ClosePickersAsync(); return; }

        _commandPaletteOpen = true;
        _filePickerOpen     = false;
        _projectPickerOpen  = false;
        _gitPickerOpen      = false;
        _pickerSel          = 0;
        _ = SetPickerOpenAsync(true);
    }

    private void OpenProjectPicker()
    {
        _projectPickerOpen  = true;
        _commandPaletteOpen = false;
        _filePickerOpen     = false;
        _gitPickerOpen      = false;
        _pickerSel          = 0;
        _projectPickerOptions = new List<(string Path, string Name)> { ("", "— select project —") };
        _projectPickerOptions.AddRange(SessionState.Projects.Select(p => (p.Path, p.Name)));
        _ = SetPickerOpenAsync(true);
    }

    // "#git:<mode>" picks from a fixed set of modes (diff/status/log) — no fuzzy search needed.
    private void OpenGitPicker(string typed)
    {
        _gitModeResults = GitModes
            .Where(m => m.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (_gitModeResults.Count == 0) { _ = ClosePickersAsync(); return; }

        _gitPickerOpen      = true;
        _commandPaletteOpen = false;
        _filePickerOpen     = false;
        _projectPickerOpen  = false;
        _pickerSel          = 0;
        _ = SetPickerOpenAsync(true);
    }

    private async Task OpenFilePickerAsync(string fragment)
    {
        var project = GetPickerActiveProject();
        _filePickerOpen     = true;
        _commandPaletteOpen = false;
        _projectPickerOpen  = false;
        _gitPickerOpen      = false;
        await SetPickerOpenAsync(true);

        if (project == null)
        {
            _fileResults = [];
            _filePickerLoading = false;
            await InvokeAsync(StateHasChanged);
            return;
        }

        // Debounce: cancel any in-flight listing so fast typing only issues the latest query.
        _pickerCts?.Cancel();
        var cts = _pickerCts = new CancellationTokenSource();
        _filePickerLoading = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            await Task.Delay(150, cts.Token);
            var userId = BridgeUserId();
            if (userId == null)
            {
                _filePickerLoading = false;
                await InvokeAsync(StateHasChanged);
                return;
            }
            if (cts.IsCancellationRequested) return;

            var files = await ProjectFiles.ListFilesAsync(
                userId, project.Path, fragment, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);
            if (cts.IsCancellationRequested) return;

            _fileResults = files;
            _pickerSel   = 0;
            _filePickerLoading = false;
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) { }
    }

    private async Task ClosePickersAsync()
    {
        _pickerCts?.Cancel();
        if (!AnyPickerOpen) return;
        _commandPaletteOpen = false;
        _filePickerOpen     = false;
        _projectPickerOpen  = false;
        _gitPickerOpen      = false;
        _fileResults        = [];
        _commandResults     = [];
        _gitModeResults     = [];
        _filePickerLoading  = false;
        await SetPickerOpenAsync(false);
        await InvokeAsync(StateHasChanged);
    }

    private async Task SetPickerOpenAsync(bool open)
    {
        try { await JS.InvokeVoidAsync("ariaInterop.setPickerOpen", "chatInput", open); } catch { }
    }

    /// <summary>Returns the active project if it is still in the current project list, else null.
    /// No silent fallback — "no project" is a valid state and must stay in sync across UI surfaces.</summary>
    private TerminalProject? GetPickerActiveProject() =>
        SessionState.ActiveProject is { } p && SessionState.Projects.Any(x => x.Path == p.Path) ? p : null;

    // Invoked from the JS keydown interceptor while a picker is open.
    [JSInvokable]
    public async Task OnPickerKey(string key)
    {
        if (!AnyPickerOpen) return;
        var count = _commandPaletteOpen ? _commandResults.Count
                  : _projectPickerOpen  ? _projectPickerOptions.Count
                  : _gitPickerOpen      ? _gitModeResults.Count
                  :                       _fileResults.Count;

        switch (key)
        {
            case "ArrowDown": if (count > 0) _pickerSel = (_pickerSel + 1) % count; break;
            case "ArrowUp":   if (count > 0) _pickerSel = (_pickerSel - 1 + count) % count; break;
            case "Escape":    await ClosePickersAsync(); return;
            // In the command palette, Tab only completes the highlighted command into the input;
            // Enter runs it. (File/project pickers keep Tab = accept.)
            case "Tab":
                if (_commandPaletteOpen) { await CompleteCommandAsync(_pickerSel); return; }
                await AcceptPickerAsync(_pickerSel); return;
            case "Enter":     await AcceptPickerAsync(_pickerSel); return;
        }
        await InvokeAsync(StateHasChanged);
    }

    // Tab in the command palette: complete the input to the highlighted command without running it.
    // The palette refreshes to the now-exact match so a following Enter runs it.
    private async Task CompleteCommandAsync(int index)
    {
        if (index < 0 || index >= _commandResults.Count) return;
        _input = _commandResults[index].Name;
        await UpdatePickersAsync();
        await FocusInputAsync();
    }

    private async Task AcceptPickerAsync(int index)
    {
        if (_commandPaletteOpen)
        {
            if (index >= 0 && index < _commandResults.Count)
            {
                var cmd = _commandResults[index];
                await ClosePickersAsync();
                await RunCommandAsync(cmd.Name);
            }
            return;
        }

        if (_projectPickerOpen)
        {
            if (index >= 0 && index < _projectPickerOptions.Count)
            {
                var selected = _projectPickerOptions[index];
                var project = string.IsNullOrWhiteSpace(selected.Path)
                    ? null
                    : SessionState.Projects.FirstOrDefault(p => p.Path == selected.Path);
                await SetActiveProjectAsync(project);   // also scopes the agent's Terminal tools
            }
            _input = "";                      // clear the "/project" command
            await ClosePickersAsync();
            await FocusInputAsync();
            return;
        }

        if (_filePickerOpen && index >= 0 && index < _fileResults.Count && _tokenStart >= 0)
        {
            var file = _fileResults[index];
            if (_referencedFiles.All(f => f.AbsPath != file.AbsPath))
                _referencedFiles.Add(file);

            // Replace the trailing "#fragment" with "#relPath " (trailing space closes the token).
            var before = _input[.._tokenStart];
            _input = $"{before}#{file.RelPath} ";
        }

        if (_gitPickerOpen && index >= 0 && index < _gitModeResults.Count && _tokenStart >= 0)
        {
            var mode = _gitModeResults[index];
            var before = _input[.._tokenStart];
            _input = $"{before}#git:{mode} ";
        }

        await ClosePickersAsync();
        await FocusInputAsync();
    }

    private async Task RunCommandAsync(string name)
    {
        _input = "";   // consume the typed "/command"

        switch (name)
        {
            // The "/project" picker stays inline in the input zone.
            case "/project":
                if (SessionState.Projects.Count > 0)
                {
                    OpenProjectPicker();
                    await InvokeAsync(StateHasChanged);
                    return;
                }
                break;

            case "/clear":   SessionState.RequestNewChat();           break;
            case "/compact":
                _compactConfirmOpen = true;
                await InvokeAsync(StateHasChanged);
                return;
            case "/help":
            case "/index":   SessionState.RequestOpenPanel("index");  break;

            // Commands that simply summon the matching left-menu panel.
            case "/tools":
            case "/mcp":     SessionState.RequestOpenPanel("tools");   break;
            case "/agents":  SessionState.RequestOpenPanel("agents");  break;
            case "/skills":  SessionState.RequestOpenPanel("skills");  break;
            case "/hive":    SessionState.RequestOpenPanel("hive");    break;
            case "/soul":    SessionState.RequestOpenPanel("souls");   break;
            case "/devices": SessionState.RequestOpenPanel("devices"); break;

            case "/vigil":   SessionState.OpenVigilModal();           break;
            case "/vox":     await ToggleVoxAsync();                  break;
            case "/wargame": Nav.NavigateTo("/wargame");             break;
        }

        await FocusInputAsync();
        await InvokeAsync(StateHasChanged);
    }

    private void RemoveReference(ProjectFileEntry file) => _referencedFiles.Remove(file);

    private async Task FocusInputAsync()
    {
        try { await JS.InvokeVoidAsync("ariaInterop.focusElement", "chatInput"); } catch { }
    }

    /// <summary>
    /// Resolves "#" tokens in the outgoing message and returns a note injecting their content /
    /// location for the agent. Plain tokens (no colon, back-compat) resolve to a file's absolute
    /// path — the agent reads it with its own file tools (no content upload). Files chosen from the
    /// picker (<paramref name="picked"/>) carry a reliable absolute path; hand-typed ones resolve
    /// under the active project. "folder:"/"dir:" tokens inject a directory tree directly. Tokens
    /// that can't be resolved (no project / no match) are returned in <paramref name="unresolved"/>.
    /// </summary>
    private async Task<string> BuildReferenceNote(
        string message, List<ProjectFileEntry> picked, List<string> unresolved)
    {
        var matches = _refTokenRx.Matches(message);
        if (matches.Count == 0) return "";

        var project = GetPickerActiveProject();   // no silent fallback — "no project" is valid
        var seen    = new HashSet<string>(StringComparer.Ordinal);
        var fileLines   = new List<string>();
        var otherBlocks = new List<string>();

        foreach (Match m in matches)
        {
            var token = m.Groups[1].Value;
            if (!seen.Add(token)) continue;

            if (token.StartsWith("folder:", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("dir:", StringComparison.OrdinalIgnoreCase))
            {
                var block = await ResolveFolderReferenceAsync(token, project);
                if (block == null) { unresolved.Add(token); continue; }
                otherBlocks.Add(block);
                continue;
            }

            if (token.StartsWith("git:", StringComparison.OrdinalIgnoreCase))
            {
                var block = await ResolveGitReferenceAsync(token, project);
                if (block == null) { unresolved.Add(token); continue; }
                otherBlocks.Add(block);
                continue;
            }

            var hit     = picked.FirstOrDefault(f => f.RelPath == token);
            var absPath = hit?.AbsPath
                ?? (project != null ? Path.GetFullPath(Path.Combine(project.Path, token)) : null);
            if (absPath == null) { unresolved.Add(token); continue; }

            fileLines.Add($"  {token} → {absPath}");
        }

        var note = "";
        if (fileLines.Count > 0)
            note += "[The user referenced these project files. Read them with your file tools when needed " +
                    "— the absolute path is given; do not search the filesystem for them:\n" +
                    string.Join("\n", fileLines) + "\n]\n\n";
        foreach (var block in otherBlocks)
            note += block + "\n\n";
        return note;
    }

    /// <summary>Resolves a "#folder:&lt;dir&gt;" / "#dir:&lt;dir&gt;" token into an injected directory
    /// tree via the already-implemented <c>/project-files/tree</c> bridge endpoint. Returns null if
    /// there's no active project or the bridge call fails.</summary>
    private async Task<string?> ResolveFolderReferenceAsync(string token, TerminalProject? project)
    {
        var colon = token.IndexOf(':');
        var rel   = token[(colon + 1)..];
        var userId = BridgeUserId();
        if (project == null || userId == null) return null;

        var absPath = Path.GetFullPath(Path.Combine(project.Path, rel));
        var tree = await ProjectFiles.ListTreeAsync(
            userId, absPath, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);
        if (tree == null) return null;

        var lines = new List<string> { $"[Directory tree for #{token} ({absPath}):" };
        foreach (var d in tree.Dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase)) lines.Add($"  {d}/");
        foreach (var f in tree.Files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) lines.Add($"  {f}");
        if (tree.Truncated) lines.Add("  … (truncated)");
        lines.Add("Read individual files with your file tools as needed — paths are relative to the directory above.]");
        return string.Join("\n", lines);
    }

    /// <summary>Resolves a "#git:diff|status|log" token by running the read-only git command on the
    /// bridge (<c>/project-git/run</c>) against the active project's root. Returns null if there's
    /// no active project, the mode is unrecognised, or the bridge call fails (e.g. not a repo).</summary>
    private async Task<string?> ResolveGitReferenceAsync(string token, TerminalProject? project)
    {
        var mode   = token["git:".Length..].Trim().ToLowerInvariant();
        var userId = BridgeUserId();
        if (project == null || userId == null || !GitModes.Contains(mode)) return null;

        var result = await ProjectFiles.RunGitAsync(
            userId, project.Path, mode, SessionState.AllowedProjectPaths, nodeId: project.NodeId, sessionId: SessionState.SessionToken);
        if (result == null) return null;

        var body = string.IsNullOrWhiteSpace(result.Output) ? "(no output)" : result.Output.Trim();
        var truncNote = result.Truncated ? "\n… (truncated)" : "";
        return $"[git {mode} for {project.Path}:\n{body}{truncNote}\n]";
    }
}
