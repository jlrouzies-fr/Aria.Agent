using System.Text.Json;

namespace Aria.Harness.Core;

public sealed partial class Harness
{
    // ── Memory / terminal prompt addenda ──────────────────────────────────────

    // Injected only when the bridge actually registered run_tests this session (see the gate at the
    // assembly site). Steers the model to the structured runner for the edit → verify → fix loop —
    // raw bash_exec output made the model re-read and grep failure dumps itself.
    private const string RunTestsAddendum = """


        ## Structured Test & Build Runs

        Prefer **run_tests** over bash_exec for build/test/lint/run verification: it infers the project's own command (or takes an explicit one), maps a plain test-name filter to the ecosystem's native flag, and returns structured results — pass/fail counts plus failing test names with file:line — instead of a raw stdout dump. Use bash_exec only for commands run_tests cannot express.
        """;

    // Injected only when the memory tools were actually registered this session (see the hasMemoryTools
    // gate at the assembly site). Gives the model concrete save/recall triggers — the tool descriptions
    // alone proved too weak, especially for small local models, and the Minimal Action Principle
    // otherwise suppresses self-initiated Inscribe calls.
    private static string BuildMemoryAddendum() => """


        ## Memory (Noosphere)

        You have persistent memory that survives across sessions. Saving to it is ALWAYS permitted — it is exempt from the Minimal Action Principle and needs no explicit request from the user.

        Inscribe proactively when the user reveals something with value beyond this session:
        - Preferences, constraints, or standing rules ("I prefer X", "never do Y", "from now on…")
        - Decisions and deferrals ("let's do Z later", "we'll go with option B") — record what was decided or deferred, and why, so a future session can resume it
        - Corrections ("no, use W not V") — record the corrected fact
        - Durable facts about the user's machines, projects, servers, accounts, or environment quirks
        - Named tools, technologies, or people the user clearly intends to revisit

        Do NOT inscribe: ephemeral task progress, anything already recorded in the project/repo itself, secrets or credentials, and small talk.

        The archive merges duplicates and links entities automatically — never hold back an Inscribe because the fact might already exist. When a fact changes, simply inscribe the new version.

        Probe memory whenever a request may depend on earlier sessions — recurring project or person names, "what did we decide about…", or any task that resumes prior work.
        """;

    private static string BuildTerminalAddendum(
        (string Name, string Path, string Description, string? NodeId, string? Platform)[] projects,
        IReadOnlyDictionary<string?, string> nodePlatforms,
        string? activeProjectPath = null)
    {
        static string Norm(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return p; }
        }

        var distinctPlatforms = projects.Select(p => p.Platform).Concat(nodePlatforms.Values)
            .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mixedPlatforms = distinctPlatforms.Count > 1;

        var platform = nodePlatforms.Values.FirstOrDefault(p => !string.IsNullOrEmpty(p))
                    ?? projects.Select(p => p.Platform).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        var isWindows = platform?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true;

        var shellName   = mixedPlatforms ? "per project — see Allowed Projects below" : (isWindows ? "cmd.exe / PowerShell" : "bash");
        var homeMacro   = "`~` expands to the home directory";
        var sepHint     = mixedPlatforms
            ? "Path style follows each project's OS: `\\` and drive letters on Windows, `/` elsewhere."
            : (isWindows ? "Paths use `\\\\` separators." : "Paths use `/` separators.");
        var homeExample = isWindows ? "`C:\\Users\\<user>`" : "`/home/<user>`";

        // Tag each project with its OS when projects span machines, so the model keeps Windows
        // path syntax for Windows projects and POSIX syntax for the rest — and never blends them.
        string ProjectLabel((string Name, string Path, string Description, string? NodeId, string? Platform) p)
        {
            var os = mixedPlatforms && !string.IsNullOrEmpty(p.Platform) ? $" [{p.Platform}]" : "";
            return string.IsNullOrWhiteSpace(p.Description)
                ? $"- **{p.Name}**{os}: `{p.Path}`"
                : $"- **{p.Name}**{os} (`{p.Path}`): {p.Description}";
        }

        var projectsSection = projects.Length > 0
            ? "\n\n### Allowed Projects\n" +
              string.Join("\n", projects.Select(ProjectLabel)) +
              "\n\nYou may only access files and run commands within these paths. Use these exact absolute paths — do not guess or infer other locations. " +
              "If the user names a project by its name or path above, use it directly without asking for clarification. " +
              "Project names may be partial, lowercase, or abbreviated — match them case-insensitively and by prefix/substring (e.g. 'spectra' → 'Spectra.MLX'). " +
              "The user can switch the active project at any time by typing `/project`." +
              (mixedPlatforms
                  ? " Projects live on DIFFERENT machines: every tool call is executed on the machine that owns the path you pass, so always copy the project's path prefix verbatim (drive letter and separators included) and never rewrite it into another OS's style."
                  : "")
            : "\n\n### Allowed Projects\nNo terminal projects are currently available. Do not access the filesystem.";

        var otherProjects = activeProjectPath is null
            ? []
            : projects.Where(p => !string.Equals(
                Norm(p.Path), Norm(activeProjectPath), StringComparison.OrdinalIgnoreCase)).ToArray();
        var otherSection = otherProjects.Length > 0
            ? "\n\n### Other known projects (not currently active)\n" +
              string.Join("\n", otherProjects.Select(ProjectLabel)) +
              "\n\nIf the user asks about one of these, tell them which project is currently active and that they can switch to it with `/project` — do not attempt to read or list files outside the Allowed Projects list."
            : "";

        return $"""


        ## Terminal Access

        You have direct shell and filesystem access on the user's machine via built-in tools. The target environment is **{(mixedPlatforms ? "mixed — each project lists its OS" : platform ?? "unknown OS")}**; adapt your commands accordingly:
        - Shell: **{shellName}**
        - {sepHint} {homeMacro} (e.g., {homeExample}).
        - **bash_exec** — run any shell command (returns JSON with exit_code, stdout, stderr)
        - **run_background** — start a long-running command detached (dev server, watcher, etc.)
        - **wait_for** — wait for a port, URL, or log pattern to become ready
        - **process_output** — read the log of a tracked background job
        - **process_kill** — stop a tracked background job
        - **read_file** — read file contents (supports line ranges; returns numbered lines)
        - **write_file** — write/create a file (creates parent directories automatically)
        - **edit_file** — replace an exact string in a file (old_string must appear exactly once; widen context if ambiguous)
        - **list_dir** — list directory entries with types and sizes
        - **glob** — find files by pattern (supports ** recursion, e.g. `**/*.cs`, `src/**/*.ts`)
        - **commands_index** — get build/run/test commands for any language or framework{projectsSection}{otherSection}

        ### Workflow guidelines
        - **Act minimally**: take only the steps the user explicitly requested. If asked to read one file, read that one file — do not explore directories or read other files unless required.
        - Use absolute paths.
        - **Before editing (not reading)**: use `list_dir` or `glob` to locate a file if its exact path is unknown, and `read_file` to confirm exact content before calling `edit_file`.
        - **edit_file requires uniqueness**: if `old_string` is not found or appears multiple times, the call will fail — add more surrounding lines to make it unique.
        - **Check exit codes**: `bash_exec` returns `exit_code`; treat non-zero as an error and inspect `stderr`.
        - **Long-running process loop**: for dev servers, watchers, and similar, use `run_background`; wait for readiness with `wait_for` (port, URL, or log pattern); stream logs with `process_output`; stop with `process_kill`. If a foreground `bash_exec` exceeds `timeout_seconds`, it is converted to a background job instead of being killed.
        - Call `commands_index(topic="rust")` (or python, go, dotnet, docker, git, etc.) before running unfamiliar build commands.
        """;
    }

    private static string[] ParseConfigLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
            .ToArray();

    private static (string Name, string Path, string Description, string? NodeId, string? Platform)[] ParseNamedPaths(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Select(e => (
                    Name:        e.TryGetProperty("name",        out var n) ? n.GetString() ?? "" : "",
                    // Users paste paths with surrounding quotes ("C:\...") — strip them, or the
                    // bridge's allowed-path prefix check can never match.
                    Path:        (e.TryGetProperty("path",       out var p) ? p.GetString() ?? "" : "").Trim().Trim('"', '\''),
                    Description: e.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    NodeId:      e.TryGetProperty("nodeId",      out var nid) ? nid.GetString() : null,
                    Platform:    e.TryGetProperty("platform",    out var plf) ? plf.GetString() : null))
                .Where(e => !string.IsNullOrWhiteSpace(e.Path))
                .ToArray();
        }
        catch { return []; }
    }
}
