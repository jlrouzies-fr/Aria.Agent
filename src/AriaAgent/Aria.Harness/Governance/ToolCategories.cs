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
        "write_file", "edit_file", "bash_exec", "create_dir", "delete_file", "delete_dir", "move_path"
    };

    // Mutations consequential enough to warrant a node-signed Seal in Paranoid mode.
    // delete_dir joins bash_exec here: a recursive directory delete can destroy far more in one
    // call than write_file/edit_file/delete_file (each bounded to a single file).
    public static readonly HashSet<string> HighStakes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash_exec", "delete_dir"
    };

    // File-system reads — these are what the per-turn read budget bounds (the "walked the folder" problem).
    public static readonly HashSet<string> FileReads = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "list_dir", "glob"
    };

    public static bool IsMutating(string name)   => Mutating.Contains(name);
    public static bool IsHighStakes(string name) => HighStakes.Contains(name);
    public static bool IsFileRead(string name)   => FileReads.Contains(name);
}
