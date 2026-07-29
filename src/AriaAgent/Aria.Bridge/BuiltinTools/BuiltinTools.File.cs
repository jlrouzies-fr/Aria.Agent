using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge;

public static partial class BuiltinTools
{
    private static IEnumerable<BridgeToolInfo> FileToolInfos()
    {
        yield return new("read_file",
            "Read a text file. Optionally limit to a line range (1-based, inclusive). Lines are returned with line numbers prefixed.",
            Js("""
               {"type":"object",
                "properties":{
                  "path":       {"type":"string","description":"Absolute path to the file."},
                  "start_line": {"type":"integer","description":"First line to return (1-based, inclusive). Optional."},
                  "end_line":   {"type":"integer","description":"Last line to return (1-based, inclusive). Optional."}
                },
                "required":["path"]}
               """));

        yield return new("write_file",
            "Write text content to a file. Creates the file and any missing parent directories. Overwrites existing content.",
            Js("""
               {"type":"object",
                "properties":{
                  "path":    {"type":"string","description":"Absolute path to the file."},
                  "content": {"type":"string","description":"Text content to write."}
                },
                "required":["path","content"]}
               """));

        yield return new("edit_file",
            "Replace an exact string in a file with new text. Fails if old_string is not found or appears more than once — in that case widen the context until it is unique.",
            Js("""
               {"type":"object",
                "properties":{
                  "path":       {"type":"string","description":"Absolute path to the file."},
                  "old_string": {"type":"string","description":"Exact text to replace. Must appear exactly once in the file."},
                  "new_string": {"type":"string","description":"Replacement text."}
                },
                "required":["path","old_string","new_string"]}
               """));

        yield return new("multi_edit",
            "Apply several exact-string replacements to one file in a single call. Edits apply sequentially in order; each old_string must appear exactly once at the moment it is applied. Atomic: if any edit fails, the file is left unchanged and the failing edit index is reported.",
            Js("""
               {"type":"object",
                "properties":{
                  "path":  {"type":"string","description":"Absolute path to the file."},
                  "edits": {"type":"array",
                            "description":"Replacements to apply in order.",
                            "items":{"type":"object",
                                     "properties":{
                                       "old_string":{"type":"string","description":"Exact text to replace. Must appear exactly once when this edit is applied."},
                                       "new_string":{"type":"string","description":"Replacement text."}
                                     },
                                     "required":["old_string","new_string"]}}
                },
                "required":["path","edits"]}
               """));

        yield return new("undo_file",
            "Restore a file to its state before the most recent recorded mutation (write_file, edit_file, multi_edit, delete_file, …). The undo is itself recorded, so it can be undone again. Fails cleanly when no snapshot exists for the path.",
            Js("""
               {"type":"object",
                "properties":{
                  "path": {"type":"string","description":"Absolute path to the file to restore."}
                },
                "required":["path"]}
               """));

        yield return new("list_dir",
            "List entries (files and subdirectories) in a directory.",
            Js("""
               {"type":"object",
                "properties":{
                  "path": {"type":"string","description":"Absolute path to the directory."}
                },
                "required":["path"]}
               """));

        yield return new("glob",
            "Find files matching a glob pattern. Use ** to recurse (e.g. **/*.cs, src/**/*.ts). Returns up to 500 absolute paths.",
            Js("""
               {"type":"object",
                "properties":{
                  "pattern":  {"type":"string","description":"Glob pattern. ** recurses into subdirectories."},
                  "base_dir": {"type":"string","description":"Directory to search from. Defaults to user home."}
                },
                "required":["pattern"]}
               """));

        yield return new("create_dir",
            "Create a directory, including any missing parent directories. Prefer this over a shell mkdir command. No-op if it already exists.",
            Js("""
               {"type":"object",
                "properties":{
                  "path": {"type":"string","description":"Absolute path to the directory to create."}
                },
                "required":["path"]}
               """));

        yield return new("delete_file",
            "Delete a single file. Prefer this over a shell rm/del command.",
            Js("""
               {"type":"object",
                "properties":{
                  "path": {"type":"string","description":"Absolute path to the file to delete."}
                },
                "required":["path"]}
               """));

        yield return new("delete_dir",
            "Delete a directory. Prefer this over a shell rm -rf/rmdir command. Fails if the directory is not empty unless recursive is true.",
            Js("""
               {"type":"object",
                "properties":{
                  "path":      {"type":"string","description":"Absolute path to the directory to delete."},
                  "recursive": {"type":"boolean","description":"Delete all contents too. Defaults to false — a non-empty directory fails without it."}
                },
                "required":["path"]}
               """));

        yield return new("move_path",
            "Move or rename a file or directory. Prefer this over a shell mv/move command.",
            Js("""
               {"type":"object",
                "properties":{
                  "source":      {"type":"string","description":"Absolute path to the existing file or directory."},
                  "destination": {"type":"string","description":"Absolute destination path."},
                  "overwrite":   {"type":"boolean","description":"Overwrite an existing file at the destination. Defaults to false. Ignored for directories."}
                },
                "required":["source","destination"]}
               """));
    }

    private static ToolCallResponse ReadFile(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy, int? contextWindow)
    {
        var path = Expand(args.Str("path") ?? throw new ArgumentException("'path' is required"));
        policy?.EnforcePath(path);

        // File.ReadAllLines on a directory throws UnauthorizedAccessException ("Access to the path is
        // denied") — a misleading dead end the model can't recover from. Redirect it to list_dir instead.
        if (Directory.Exists(path))
            return Err($"'{path}' is a directory, not a file. Use list_dir to see its contents, then read_file on a specific file.");
        if (!File.Exists(path))
            return Err($"File not found: {path}");

        var startLine = args.Int("start_line");
        var endLine   = args.Int("end_line");

        // Known-window guard: if the file would eat more than 25% of the budget, return only the first
        // ~200 lines and guide the model toward range reads. With an assumed window, keep today's
        // behaviour (whole file) so we do not silently change existing sessions.
        if (contextWindow.HasValue && !startLine.HasValue && !endLine.HasValue)
        {
            var info = new FileInfo(path);
            var estimatedTokens = (info.Length + 3) / 4; // chars/4, rounded up
            var budget = contextWindow.Value / 4L;       // 25% of the window
            if (estimatedTokens > budget)
            {
                var lines = File.ReadAllLines(path);
                var cap = Math.Min(200, lines.Length);
                var slice = lines[..cap];
                var text = string.Join('\n', slice.Select((l, i) => $"{i + 1}\t{l}"));
                return new ToolCallResponse(
                    text + $"\n\n[FILE TRUNCATED] This file is estimated at ~{estimatedTokens:N0} tokens, " +
                    $"which exceeds 25% of the known context window ({contextWindow.Value:N0} tokens). " +
                    "Use start_line/end_line to read specific ranges rather than the whole file.",
                    false);
            }
        }

        var linesAll = File.ReadAllLines(path);

        var from = startLine.HasValue ? Math.Max(0, startLine.Value - 1) : 0;
        var to   = endLine.HasValue   ? Math.Min(linesAll.Length - 1, endLine.Value - 1) : linesAll.Length - 1;

        var sliceAll = linesAll[from..(to + 1)];
        var textAll  = string.Join('\n', sliceAll.Select((l, i) => $"{from + i + 1}\t{l}"));
        return new ToolCallResponse(textAll, false);
    }

    private static ToolCallResponse WriteFile(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy, BridgeDbContext? db)
    {
        var plan = PlanWriteFile(args, policy);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Path)!);
        File.WriteAllText(plan.Path, plan.PostContent);
        var metadata = BuildFileMutationMetadata(db, plan.Path, plan.PreContent, plan.PostContent, "write_file",
            out var _, out var diff, created: plan.PreContent == null);
        return new ToolCallResponse(
            AppendDiffFeedback($"Wrote {plan.PostContent.Length} chars to {plan.Path}", diff), false, MetadataJson: metadata);
    }

    private static ToolCallResponse EditFile(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy, BridgeDbContext? db)
    {
        if (TryPlanEditFile(args, policy, out var plan) is { } error)
            return error;

        File.WriteAllText(plan!.Path, plan.PostContent);
        var metadata = BuildFileMutationMetadata(db, plan.Path, plan.PreContent, plan.PostContent, "edit_file",
            out var _, out var diff);
        return new ToolCallResponse(
            AppendDiffFeedback($"Replaced 1 occurrence in {plan.Path}", diff), false, MetadataJson: metadata);
    }

    private static ToolCallResponse MultiEdit(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy, BridgeDbContext? db)
    {
        if (TryPlanMultiEdit(args, policy, out var plan, out var editCount) is { } error)
            return error;

        File.WriteAllText(plan!.Path, plan.PostContent);
        var metadata = BuildFileMutationMetadata(db, plan.Path, plan.PreContent, plan.PostContent, "multi_edit",
            out var _, out var diff);
        return new ToolCallResponse(
            AppendDiffFeedback($"Applied {editCount} edit(s) to {plan.Path}", diff), false, MetadataJson: metadata);
    }

    // ── Pure planners (shared by the mutation handlers above and /tools/preview) ──
    // Each validates args + scope + the CURRENT file bytes and returns the pre/post images
    // WITHOUT writing anything — the handler writes, the preview endpoint only diffs.

    private sealed record MutationPlan(string Path, string? PreContent, string PostContent);

    private static MutationPlan PlanWriteFile(Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var path    = Expand(args.Str("path")    ?? throw new ArgumentException("'path' is required"));
        var content = args.Str("content") ?? throw new ArgumentException("'content' is required");
        policy?.EnforcePath(path);
        return new MutationPlan(path, ReadPreImage(path, out _), content);
    }

    private static ToolCallResponse? TryPlanEditFile(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy, out MutationPlan? plan)
    {
        plan = null;
        var path   = Expand(args.Str("path")       ?? throw new ArgumentException("'path' is required"));
        var oldStr = args.Str("old_string") ?? throw new ArgumentException("'old_string' is required");
        var newStr = args.Str("new_string") ?? throw new ArgumentException("'new_string' is required");
        policy?.EnforcePath(path);

        var text  = File.ReadAllText(path);
        var count = CountOccurrences(text, oldStr);

        if (count == 0)
            return Err($"old_string not found in {path}. Use read_file to verify the exact text.");
        if (count > 1)
            return Err($"old_string is ambiguous ({count} occurrences in {path}). Add more surrounding context to make it unique.");

        plan = new MutationPlan(path, text, text.Replace(oldStr, newStr, StringComparison.Ordinal));
        return null;
    }

    private static ToolCallResponse? TryPlanMultiEdit(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy, out MutationPlan? plan, out int editCount)
    {
        plan = null;
        editCount = 0;
        var path = Expand(args.Str("path") ?? throw new ArgumentException("'path' is required"));
        policy?.EnforcePath(path);

        if (!args.TryGetValue("edits", out var editsEl) || editsEl.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("'edits' is required");

        var edits = new List<(string Old, string New)>();
        foreach (var e in editsEl.EnumerateArray())
        {
            var oldStr = e.TryGetProperty("old_string", out var ov) && ov.ValueKind == JsonValueKind.String ? ov.GetString() : null;
            var newStr = e.TryGetProperty("new_string", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() : null;
            if (oldStr == null || newStr == null)
                return Err($"edits[{edits.Count}]: 'old_string' and 'new_string' are required");
            edits.Add((oldStr, newStr));
        }
        if (edits.Count == 0)
            return Err("'edits' must contain at least one edit");

        // Apply the whole batch against an in-memory copy first — the file is written only when
        // every edit succeeds, so a failure anywhere leaves the file untouched (atomic per file).
        // Each old_string must be unique AT THE TIME it applies: earlier edits can create or
        // destroy occurrences for later ones.
        var preContent = File.ReadAllText(path);
        var working = preContent;
        for (var i = 0; i < edits.Count; i++)
        {
            var count = CountOccurrences(working, edits[i].Old);
            if (count == 0)
                return Err($"Edit {i} failed: old_string not found. Edits apply sequentially — earlier edits may have changed the text. No changes were written to {path}.");
            if (count > 1)
                return Err($"Edit {i} failed: old_string is ambiguous ({count} occurrences at that point). Add more surrounding context to make it unique. No changes were written to {path}.");
            working = working.Replace(edits[i].Old, edits[i].New, StringComparison.Ordinal);
        }

        plan = new MutationPlan(path, preContent, working);
        editCount = edits.Count;
        return null;
    }

    private static ToolCallResponse UndoFile(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy, BridgeDbContext? db)
    {
        var path = Expand(args.Str("path") ?? throw new ArgumentException("'path' is required"));
        policy?.EnforcePath(path);

        if (db == null)
            return Err("undo_file is unavailable: no bridge database in this context.");

        // Most recent not-yet-reverted snapshot for this path — reverted rows are skipped so
        // repeated undo_file calls walk the mutation history like a stack.
        var undo = db.FileUndos
            .Where(u => u.Path == path && u.RevertedAt == null)
            .OrderByDescending(u => u.CreatedAt)
            .FirstOrDefault();
        if (undo == null)
            return Err($"No undo snapshot found for {path}.");

        // Same guard as the Explorer revert endpoint: refuse to clobber content that changed
        // after the snapshot was taken.
        var currentExists = File.Exists(path);
        var currentHash = currentExists ? ComputeContentHash(File.ReadAllText(path)) : "";
        if (currentHash != undo.PostHash)
            return Err($"Refusing to undo: {path} has changed since the {undo.ToolName} mutation being reverted. Inspect it with read_file first.");

        var preContent = currentExists ? File.ReadAllText(path) : null;
        FileReverter.Apply(undo);
        undo.RevertedAt = DateTime.UtcNow;

        // The undo is itself a mutation — record it (BuildFileMutationMetadata also persists the
        // RevertedAt mark above) so undo_file can be undone in turn.
        var restoredExists = File.Exists(path);
        var postContent = restoredExists ? File.ReadAllText(path) : "";
        var metadata = BuildFileMutationMetadata(db, path, preContent, postContent, "undo_file",
            out var _, out var _, deleted: !restoredExists);
        return new ToolCallResponse(
            $"Restored {path} to its state before the {undo.ToolName} mutation from {undo.CreatedAt:u}",
            false, MetadataJson: metadata);
    }

    private static ToolCallResponse ListDir(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var path = Expand(args.Str("path") ?? throw new ArgumentException("'path' is required"));
        policy?.EnforcePath(path);

        if (!Directory.Exists(path))
            return Err($"Directory not found: {path}");

        var entries = Directory.GetFileSystemEntries(path)
            .Select(e =>
            {
                var isDir = Directory.Exists(e);
                long? size = isDir ? null : new FileInfo(e).Length;
                return new { name = Path.GetFileName(e), type = isDir ? "dir" : "file", size };
            })
            .OrderBy(e => e.type).ThenBy(e => e.name);

        return new ToolCallResponse(JsonSerializer.Serialize(entries), false);
    }

    private static ToolCallResponse GlobFiles(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var pattern = args.Str("pattern") ?? throw new ArgumentException("'pattern' is required");
        var baseDir = Expand(args.Str("base_dir") ?? "~");
        policy?.EnforcePath(baseDir);

        if (!Directory.Exists(baseDir))
            return Err($"Base directory not found: {baseDir}");

        // Strip leading **/ so .NET EnumerateFiles gets just the file pattern.
        var filePattern = pattern.TrimStart('*', '/').TrimStart('/');
        if (string.IsNullOrEmpty(filePattern)) filePattern = "*";

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = pattern.Contains("**"),
            MatchCasing  = IsWindows ? MatchCasing.CaseInsensitive : MatchCasing.CaseSensitive,
            IgnoreInaccessible = true,
        };

        var matches = Directory.EnumerateFiles(baseDir, filePattern, opts)
            .Take(500)
            .ToArray();

        return new ToolCallResponse(JsonSerializer.Serialize(matches), false);
    }

    private static ToolCallResponse CreateDir(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var path = Expand(args.Str("path") ?? throw new ArgumentException("'path' is required"));
        policy?.EnforcePath(path);

        Directory.CreateDirectory(path);
        return new ToolCallResponse($"Created directory {path}", false);
    }

    private static ToolCallResponse DeleteFile(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy, BridgeDbContext? db)
    {
        var path = Expand(args.Str("path") ?? throw new ArgumentException("'path' is required"));
        policy?.EnforcePath(path);

        if (!File.Exists(path))
            return Err($"File not found: {path}");

        var preContent = File.ReadAllText(path);
        File.Delete(path);
        var metadata = BuildFileMutationMetadata(db, path, preContent, "", "delete_file", out var _, out var _, deleted: true);
        return new ToolCallResponse($"Deleted {path}", false, MetadataJson: metadata);
    }

    private static ToolCallResponse DeleteDir(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var path      = Expand(args.Str("path") ?? throw new ArgumentException("'path' is required"));
        var recursive = args.Bool("recursive") ?? false;
        policy?.EnforcePath(path);

        if (!Directory.Exists(path))
            return Err($"Directory not found: {path}");

        Directory.Delete(path, recursive);
        return new ToolCallResponse($"Deleted directory {path}", false);
    }

    private static ToolCallResponse MovePath(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy, BridgeDbContext? db)
    {
        var source      = Expand(args.Str("source")      ?? throw new ArgumentException("'source' is required"));
        var destination = Expand(args.Str("destination") ?? throw new ArgumentException("'destination' is required"));
        var overwrite   = args.Bool("overwrite") ?? false;
        policy?.EnforcePath(source);
        policy?.EnforcePath(destination);

        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
            return new ToolCallResponse($"Moved directory {source} → {destination}", false);
        }

        if (!File.Exists(source))
            return Err($"Source not found: {source}");

        var preContent = File.ReadAllText(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination, overwrite);
        var postContent = File.ReadAllText(destination);
        var metadata = BuildFileMutationMetadata(db, source, preContent, postContent, "move_path", out var undoToken, out var _, destination: destination);
        return new ToolCallResponse($"Moved {source} → {destination}", false, MetadataJson: metadata);
    }

    // ── File mutation diff / undo helpers ──────────────────────────────────────

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string? ReadPreImage(string path, out bool existed)
    {
        existed = File.Exists(path);
        if (!existed) return null;
        return File.ReadAllText(path);
    }

    private static bool ExceedsDiffCap(string? content) =>
        content != null && Encoding.UTF8.GetByteCount(content) > DiffTools.PreImageCap;

    private static string ComputeContentHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string? BuildFileMutationMetadata(
        BridgeDbContext? db,
        string path,
        string? preContent,
        string postContent,
        string toolName,
        out string undoToken,
        out DiffResult? diff,
        bool deleted = false,
        bool created = false,
        string? destination = null)
    {
        undoToken = "";
        diff = null;
        if (db == null) return null;

        var capExceeded = ExceedsDiffCap(preContent);

        var beforeLines = preContent == null || capExceeded
            ? Array.Empty<string?>()
            : preContent.ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray();
        var afterLines = deleted || postContent == ""
            ? Array.Empty<string?>()
            : postContent.ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray();

        var diffResult = DiffTools.ComputeUnifiedDiff(beforeLines, afterLines, Path.GetFileName(path));
        if (!capExceeded) diff = diffResult;

        undoToken = Guid.NewGuid().ToString("N");
        db.FileUndos.Add(new FileUndo
        {
            Id = undoToken,
            Path = path,
            DestinationPath = destination,
            PreContent = preContent,
            PostHash = deleted ? "" : ComputeContentHash(postContent),
            ToolName = toolName,
            Checkpoint = CurrentCheckpoint.Value,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
        PruneFileUndos(db);

        return JsonSerializer.Serialize(new
        {
            kind = "file_mutation",
            path,
            destination,
            diff = capExceeded ? null : diffResult.Diff,
            adds = capExceeded ? 0 : diffResult.Adds,
            dels = capExceeded ? 0 : diffResult.Dels,
            // Above the pre-image cap the mutation is applied but no diff preview is produced — say
            // so explicitly instead of silently omitting the diff. The FileUndo row IS still written,
            // so undo via revert remains available.
            warning = capExceeded
                ? $"File exceeds the diff cap ({DiffTools.PreImageCap / 1024}KB): no diff preview is available for this mutation — undo via revert is still available."
                : (string?)null,
            undoToken,
            checkpoint = CurrentCheckpoint.Value,
            created,
            deleted
        }, MetadataJsonOptions);
    }

    private static void PruneFileUndos(BridgeDbContext db)
    {
        var keep = db.FileUndos
            .OrderByDescending(u => u.CreatedAt)
            .Take(200)
            .Select(u => u.Id)
            .ToList();
        db.FileUndos.Where(u => !keep.Contains(u.Id)).ExecuteDelete();
    }

    // ── Diff feedback (AgentTools:DiffFeedback) ──────────────────────────────
    // Default ON: after a file mutation the unified diff is appended to the model-facing result
    // text (head-truncated to the cap) so the model can self-verify the edit without spending a
    // re-read. Off → exactly the old one-line confirmations. Surfaced read-only on the bridge
    // status page next to the Projects toggle.

    public sealed record DiffFeedbackOptions(bool Enabled = true, int MaxChars = 4000);

    private static DiffFeedbackOptions _diffFeedback = new();

    public static void ConfigureDiffFeedback(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        var section = config.GetSection("AgentTools:DiffFeedback");
        var cap = section.GetValue<int?>("MaxChars");
        ConfigureDiffFeedback(
            section.GetValue<bool?>("Enabled") ?? true,
            cap is > 0 ? cap.Value : 4000);
    }

    // Direct value overload — tests toggle the knob through this.
    internal static void ConfigureDiffFeedback(bool enabled, int maxChars = 4000) =>
        _diffFeedback = new DiffFeedbackOptions(enabled, maxChars);

    internal static DiffFeedbackOptions DiffFeedback => _diffFeedback;

    // The model-facing half of a mutation result: the confirmation line plus the same diff the
    // UI card gets. Skipped when no diff was computed (pre-image cap, no bridge db) or the edit
    // was a no-op (no hunks — the diff would be just the ---/+++ headers).
    private static string AppendDiffFeedback(string text, DiffResult? diff)
    {
        if (!_diffFeedback.Enabled || diff == null || (diff.Adds == 0 && diff.Dels == 0))
            return text;
        return text + "\n\n" + TruncateDiff(diff.Diff, _diffFeedback.MaxChars).Text;
    }

    // Head-biased truncation: keep whole diff lines while they fit the char cap, then a marker
    // naming the elided line count — the model sees the start of the diff and knows it was cut.
    internal static (string Text, bool Truncated) TruncateDiff(string diff, int maxChars)
    {
        var lines = diff.ReplaceLineEndings("\n").Split('\n');
        // ComputeUnifiedDiff output ends with a trailing newline — drop the empty tail element.
        if (lines.Length > 0 && lines[^1] == "") lines = lines[..^1];

        var kept = 0;
        var used = 0;
        foreach (var line in lines)
        {
            var cost = line.Length + 1; // + the newline that separates it
            if (kept > 0 && used + cost > maxChars) break;
            used += cost;
            kept++;
        }

        if (kept >= lines.Length)
            return (string.Join('\n', lines), false);

        return (string.Join('\n', lines[..kept]) + $"\n… diff truncated ({lines.Length - kept} more lines)", true);
    }

    // ── /tools/preview — prospective diff, nothing written ───────────────────

    public sealed record ToolPreviewResponse(bool Ok, string? Diff, bool Truncated, string? Reason);

    /// <summary>
    /// Read-only twin of <see cref="InvokeAsync"/> for the file-mutation tools: runs the same arg
    /// validation, scope enforcement and apply logic against an in-memory copy and returns the
    /// unified diff the mutation WOULD produce. Nothing is written and no undo row is recorded —
    /// a failure comes back exactly as the real call would report it. Any other tool answers
    /// "no-preview" so the caller falls back to the plain args preview.
    /// </summary>
    public static ToolPreviewResponse Preview(
        string toolName,
        Dictionary<string, JsonElement>? args,
        SecurityPolicy? policy)
    {
        args ??= [];
        try
        {
            MutationPlan? plan;
            switch (toolName)
            {
                case "write_file":
                    plan = PlanWriteFile(args, policy);
                    break;
                case "edit_file":
                    if (TryPlanEditFile(args, policy, out plan) is { } editError)
                        return new ToolPreviewResponse(false, null, false, editError.Text);
                    break;
                case "multi_edit":
                    if (TryPlanMultiEdit(args, policy, out plan, out _) is { } multiError)
                        return new ToolPreviewResponse(false, null, false, multiError.Text);
                    break;
                default:
                    return new ToolPreviewResponse(false, null, false, "no-preview");
            }

            if (ExceedsDiffCap(plan!.PreContent))
                return new ToolPreviewResponse(false, null, false,
                    $"File exceeds the diff cap ({DiffTools.PreImageCap / 1024}KB): no diff preview is available for this mutation.");

            var beforeLines = plan.PreContent == null
                ? Array.Empty<string?>()
                : plan.PreContent.ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray();
            var afterLines = plan.PostContent == ""
                ? Array.Empty<string?>()
                : plan.PostContent.ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray();

            var diff = DiffTools.ComputeUnifiedDiff(beforeLines, afterLines, Path.GetFileName(plan.Path));
            if (diff.Adds == 0 && diff.Dels == 0)
                return new ToolPreviewResponse(true, "", false, null); // no-op edit — nothing to show

            var (text, truncated) = TruncateDiff(diff.Diff, _diffFeedback.MaxChars);
            return new ToolPreviewResponse(true, text, truncated, null);
        }
        catch (TerminalSecurityException ex)  { return new ToolPreviewResponse(false, null, false, $"BLOCKED: {ex.Message}"); }
        catch (FileNotFoundException ex)      { return new ToolPreviewResponse(false, null, false, $"NOT FOUND: {ex.Message}"); }
        catch (DirectoryNotFoundException ex) { return new ToolPreviewResponse(false, null, false, $"NOT FOUND: {ex.Message}"); }
        catch (UnauthorizedAccessException ex){ return new ToolPreviewResponse(false, null, false, $"ACCESS DENIED: {ex.Message}"); }
        catch (Exception ex)                  { return new ToolPreviewResponse(false, null, false, $"ERROR: {ex.Message}"); }
    }
}
