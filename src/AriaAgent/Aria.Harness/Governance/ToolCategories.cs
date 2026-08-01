using System.Text.RegularExpressions;

namespace Aria.Harness.Governance;

/// <summary>
/// Static classification of tool names into behavioural categories. Centralised so the
/// classifier and the per-turn counters agree on what counts as a read / mutation / high-stakes call.
/// </summary>
internal static class ToolCategories
{
    // Actions that change state on the user's machine or the outside world.
    public static readonly HashSet<string> Mutating = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "edit_file", "bash_exec", "run_background", "run_tests", "create_dir", "delete_file", "delete_dir", "move_path",
        "git_stage", "git_commit", "git_discard", "install_software",
        "process_kill", "multi_edit", "undo_file"
    };

    // Mutations consequential enough to warrant a node-signed Seal in Paranoid mode.
    // delete_dir joins bash_exec here: a recursive directory delete can destroy far more in one
    // call than write_file/edit_file/delete_file (each bounded to a single file). git_discard
    // destroys uncommitted work irreversibly, so it sits in the same class. install_software
    // writes to system locations outside any allowed path, so it is high-stakes too. run_tests
    // executes arbitrary inferred/override shell commands, so it shares bash_exec's class exactly.
    public static readonly HashSet<string> HighStakes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash_exec", "run_background", "run_tests", "delete_dir", "git_discard", "install_software"
    };

    // File-system reads — these are what the per-turn read budget bounds (the "walked the folder" problem).
    public static readonly HashSet<string> FileReads = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "list_dir", "glob", "grep", "git_status", "git_diff", "git_log", "system_info",
        "process_list", "process_output", "read_image", "wait_for", "project_info"
    };

    // Tools that always ask a human first in every governed mode — even the lax ones (Balanced,
    // Coding) that let ordinary mutations run. Plan still blocks them outright (BlockMutations)
    // and Paranoid escalates them to a Seal (HighStakes); only Off runs them unchecked.
    public static readonly HashSet<string> RequiresApproval = new(StringComparer.OrdinalIgnoreCase)
    {
        "install_software"
    };

    // File mutations whose prospective diff the bridge can produce without writing (POST
    // /tools/preview) — fetched when such a call pauses for approval so the human sees the
    // actual change. Deletes and non-file mutations keep the plain args preview.
    public static readonly HashSet<string> DiffPreviewable = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "edit_file", "multi_edit"
    };

    // File mutations whose SUCCESSFUL completion feeds the post-mutation verify nudge's per-turn
    // counter. Deliberately narrower than Mutating: bash_exec can't be classified reliably (any
    // shell command may or may not write files) and git ops are their own checkpoint, so neither
    // nudges. undo_file reverts a mutation — it undoes work rather than adding unverified work.
    public static readonly HashSet<string> FileMutations = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "edit_file", "multi_edit", "delete_file", "delete_dir", "move_path", "create_dir"
    };

    // bash_exec commands that count as verification for the post-mutation nudge: once one runs,
    // the nudge goes quiet for the rest of the turn. run_tests is recognised separately, by its
    // structured result header (GovernedTool). The list mirrors the ecosystems project_info /
    // run_tests can infer.
    private static readonly Regex VerificationCommand = new(
        @"\b(?:dotnet\s+(?:test|build)|pytest|npm\s+(?:test|run\s+(?:build|test))|cargo\s+test|go\s+test|make\s+test)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsMutating(string name)   => Mutating.Contains(name);
    public static bool IsHighStakes(string name) => HighStakes.Contains(name);
    public static bool IsFileRead(string name)   => FileReads.Contains(name);
    public static bool RequiresHumanApproval(string name) => RequiresApproval.Contains(name);
    public static bool IsDiffPreviewable(string name)     => DiffPreviewable.Contains(name);
    public static bool IsFileMutation(string name)        => FileMutations.Contains(name);
    public static bool IsVerificationCommand(string? command) =>
        command != null && VerificationCommand.IsMatch(command);
}
