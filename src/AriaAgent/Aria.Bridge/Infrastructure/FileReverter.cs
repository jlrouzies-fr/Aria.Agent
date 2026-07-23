using Aria.Bridge.Data;

namespace Aria.Bridge.Infrastructure;

/// <summary>
/// Applies a <see cref="FileUndo"/> snapshot to disk, reversing the recorded mutation. Shared by
/// the Explorer UI revert endpoint (/project-files/revert) and the agent-facing undo_file builtin
/// so both surfaces restore files identically. Callers own the policy check, the hash-mismatch
/// guard, and marking the row reverted.
/// </summary>
internal static class FileReverter
{
    /// <summary>Restores the file(s) affected by the mutation. Returns a human-readable reverse
    /// diff (or a short description when a diff doesn't apply, e.g. move_path).</summary>
    public static string? Apply(FileUndo undo)
    {
        var path = undo.Path;
        var currentExists = File.Exists(path);

        if (undo.ToolName == "delete_file")
        {
            // Recreate the deleted file from pre-content.
            if (undo.PreContent == null) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, undo.PreContent);
            return DiffTools.ComputeUnifiedDiff(
                Array.Empty<string?>(),
                undo.PreContent.ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray(),
                Path.GetFileName(path)).Diff;
        }

        if (undo.ToolName == "move_path" && undo.DestinationPath != null)
        {
            // Restore source, remove destination.
            if (undo.PreContent != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, undo.PreContent);
            }
            if (File.Exists(undo.DestinationPath))
                File.Delete(undo.DestinationPath);
            return $"Restored {path} and removed {undo.DestinationPath}";
        }

        // write_file / edit_file / multi_edit / undo_file: restore pre-content (or delete the file
        // if the mutation created it).
        var before = currentExists
            ? File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray()
            : Array.Empty<string?>();

        if (undo.PreContent == null)
        {
            if (File.Exists(path)) File.Delete(path);
            return DiffTools.ComputeUnifiedDiff(
                before,
                Array.Empty<string?>(),
                Path.GetFileName(path)).Diff;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, undo.PreContent);
        return DiffTools.ComputeUnifiedDiff(
            before,
            undo.PreContent.ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray(),
            Path.GetFileName(path)).Diff;
    }
}
