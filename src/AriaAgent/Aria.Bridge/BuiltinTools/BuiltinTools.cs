using System.Runtime.InteropServices;
using System.Text.Json;
using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge;

/// <summary>
/// Native implementations of shell + file tools exposed as virtual MCP tools
/// when command == "__aria_builtin__". No child process is spawned.
/// </summary>
public static partial class BuiltinTools
{
    private static readonly bool IsWindows =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // ── Tool manifest ─────────────────────────────────────────────────────────

    public static List<BridgeToolInfo> GetToolInfos() =>
    [
        .. ShellToolInfos(),
        .. BackgroundToolInfos(),
        .. ProcessToolInfos(),
        .. FileToolInfos(),
        .. GrepToolInfos(),
        .. GitToolInfos(),
        .. ProjectInfoToolInfos(),
        .. RunTestsToolInfos(),
        .. CommandsIndexToolInfos(),
        .. InstallToolInfos(),
        .. WebToolInfos(),
        .. ScreenshotToolInfos()
    ];

    // ── Dispatcher ────────────────────────────────────────────────────────────

    // Turn checkpoint stamped onto FileUndo rows for /rewind. Set for the duration of one
    // InvokeAsync so every mutation helper (write/edit/multi_edit/…) picks it up without threading
    // an extra parameter through every call site.
    private static readonly AsyncLocal<string?> CurrentCheckpoint = new();

    public static async Task<ToolCallResponse> InvokeAsync(
        string toolName,
        Dictionary<string, JsonElement>? args,
        SecurityPolicy? policy,
        BridgeDbContext? db = null,
        int? contextWindow = null,
        string? checkpoint = null)
    {
        var previous = CurrentCheckpoint.Value;
        CurrentCheckpoint.Value = checkpoint;
        try
        {
            args ??= [];
            try
            {
                // The agent's shell is part of the Projects capability (opt-in per node). If Projects is
                // off, refuse bash_exec, run_background and run_tests even when the server-side Terminal tool is toggled on.
                if ((toolName == "bash_exec" || toolName == "run_background" || toolName == "run_tests") && !await IsProjectsEnabledAsync(db))
                    return Err("Agent Projects not enabled on this bridge. Open http://localhost:5741 → Terminal / Projects and enable Agent Projects.");

                return toolName switch
                {
                    "bash_exec"      => await BashExecAsync(args, policy),
                    "run_background" => await RunBackgroundAsync(args, policy),
                    "wait_for"       => await WaitForAsync(args),
                    "process_list"   => ProcessList(),
                    "process_output" => ProcessOutput(args),
                    "process_kill"   => await ProcessKillAsync(args),
                    "read_file"      => ReadFile(args, policy, contextWindow),
                    "write_file"     => WriteFile(args, policy, db),
                    "edit_file"      => EditFile(args, policy, db),
                    "multi_edit"     => MultiEdit(args, policy, db),
                    "undo_file"      => UndoFile(args, policy, db),
                    "list_dir"       => ListDir(args, policy),
                    "glob"           => GlobFiles(args, policy),
                    "grep"           => GrepSearch(args, policy),
                    "git_status"     => await GitStatusAsync(args, policy),
                    "git_diff"       => await GitDiffAsync(args, policy),
                    "git_log"        => await GitLogAsync(args, policy),
                    "git_stage"      => await GitStageAsync(args, policy),
                    "git_commit"     => await GitCommitAsync(args, policy),
                    "git_discard"    => await GitDiscardAsync(args, policy),
                    "create_dir"     => CreateDir(args, policy),
                    "delete_file"    => DeleteFile(args, policy, db),
                    "delete_dir"     => DeleteDir(args, policy),
                    "move_path"      => MovePath(args, policy, db),
                    "project_info"   => await ProjectInfoAsync(args, policy),
                    "run_tests"      => await RunTestsAsync(args, policy),
                    "commands_index" => CommandsIndex(args),
                    "install_software" => await InstallSoftwareAsync(args, policy),
                    "system_info"    => await SystemInfoAsync(),
                    "GetCurrentDateTime" => GetCurrentDateTime(),
                    "SearchWeb"          => await SearchWebAsync(args, db),
                    "Inscribe"           => await InscribeToolAsync(args),
                    "Probe"              => await ProbeToolAsync(args),
                    "Contemplate"        => await ContemplateToolAsync(args),
                    "TakeScreenshot"     => await TakeScreenshotAsync(args),
                    "http_request"       => await HttpRequestAsync(args),
                    "read_image"         => ReadImage(args, policy),
                    _ => Err($"Unknown built-in tool: {toolName}")
                };
            }
            catch (TerminalSecurityException ex)  { return Err($"BLOCKED: {ex.Message}"); }
            catch (FileNotFoundException ex)      { return Err($"NOT FOUND: {ex.Message}"); }
            catch (DirectoryNotFoundException ex){ return Err($"NOT FOUND: {ex.Message}"); }
            catch (UnauthorizedAccessException ex){ return Err($"ACCESS DENIED: {ex.Message}"); }
            catch (Exception ex)                  { return Err($"ERROR: {ex.Message}"); }
        }
        finally
        {
            CurrentCheckpoint.Value = previous;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static string Expand(string path)
    {
        // Small models (e.g. gpt-oss-20b) sometimes emit path arguments with stray whitespace or
        // wrapping quotes (" /Users/x", "'/Users/x'"). A leading space makes an absolute path look
        // RELATIVE to Path.GetFullPath, silently resolving it against the bridge's CWD — the mangled
        // result then fails the allowed-paths check with a baffling "outside allowed directories"
        // error for a path the user did allow. Normalize before any resolution.
        path = path.Trim().Trim('"', '\'').Trim();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~") return home;
        if (path.StartsWith("~/") || path.StartsWith("~\\"))
            return Path.Combine(home, path[2..]);
        return Path.GetFullPath(path);
    }

    private static async Task<bool> IsProjectsEnabledAsync(BridgeDbContext? db)
    {
        if (db == null) return false;
        var soul = await db.Souls.AsNoTracking().FirstOrDefaultAsync(x => x.Name != "");
        if (soul != null) return soul.ProjectsEnabled;
        soul = await db.Souls.AsNoTracking().FirstOrDefaultAsync();
        return soul?.ProjectsEnabled ?? false;
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(pattern, i, StringComparison.Ordinal)) >= 0)
        { n++; i += pattern.Length; }
        return n;
    }

    private static JsonElement Js(string json) => JsonDocument.Parse(json).RootElement;

    private static ToolCallResponse Err(string msg) => new(msg, IsError: true);
}

// Local extension helpers for reading tool args
internal static class ArgExt
{
    public static string? Str(this Dictionary<string, JsonElement> a, string key)
        => a.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(this Dictionary<string, JsonElement> a, string key)
        => a.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    public static bool? Bool(this Dictionary<string, JsonElement> a, string key)
        => a.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static string[]? StrArray(this Dictionary<string, JsonElement> a, string key)
        => a.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToArray()
            : null;
}
