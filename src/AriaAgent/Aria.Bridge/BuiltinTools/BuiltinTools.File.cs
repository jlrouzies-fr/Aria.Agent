using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
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
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var path = Expand(args.Str("path") ?? throw new ArgumentException("'path' is required"));
        policy?.EnforcePath(path);

        // File.ReadAllLines on a directory throws UnauthorizedAccessException ("Access to the path is
        // denied") — a misleading dead end the model can't recover from. Redirect it to list_dir instead.
        if (Directory.Exists(path))
            return Err($"'{path}' is a directory, not a file. Use list_dir to see its contents, then read_file on a specific file.");
        if (!File.Exists(path))
            return Err($"File not found: {path}");

        var lines     = File.ReadAllLines(path);
        var startLine = args.Int("start_line");
        var endLine   = args.Int("end_line");

        var from = startLine.HasValue ? Math.Max(0, startLine.Value - 1) : 0;
        var to   = endLine.HasValue   ? Math.Min(lines.Length - 1, endLine.Value - 1) : lines.Length - 1;

        var slice = lines[from..(to + 1)];
        var text  = string.Join('\n', slice.Select((l, i) => $"{from + i + 1}\t{l}"));
        return new ToolCallResponse(text, false);
    }

    private static ToolCallResponse WriteFile(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy, BridgeDbContext? db)
    {
        var path    = Expand(args.Str("path")    ?? throw new ArgumentException("'path' is required"));
        var content = args.Str("content") ?? throw new ArgumentException("'content' is required");
        policy?.EnforcePath(path);

        var preContent = ReadPreImage(path, out var existed);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        var metadata = BuildFileMutationMetadata(db, path, preContent, content, "write_file", out var _, created: !existed);
        return new ToolCallResponse($"Wrote {content.Length} chars to {path}", false, MetadataJson: metadata);
    }

    private static ToolCallResponse EditFile(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy, BridgeDbContext? db)
    {
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

        var preContent = text;
        var postContent = text.Replace(oldStr, newStr, StringComparison.Ordinal);
        File.WriteAllText(path, postContent);
        var metadata = BuildFileMutationMetadata(db, path, preContent, postContent, "edit_file", out var _);
        return new ToolCallResponse($"Replaced 1 occurrence in {path}", false, MetadataJson: metadata);
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
        var metadata = BuildFileMutationMetadata(db, path, preContent, "", "delete_file", out var _, deleted: true);
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
        var metadata = BuildFileMutationMetadata(db, source, preContent, postContent, "move_path", out var undoToken, destination: destination);
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
        bool deleted = false,
        bool created = false,
        string? destination = null)
    {
        undoToken = "";
        if (db == null) return null;

        var capExceeded = ExceedsDiffCap(preContent);

        var beforeLines = preContent == null || capExceeded
            ? Array.Empty<string?>()
            : preContent.ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray();
        var afterLines = deleted || postContent == ""
            ? Array.Empty<string?>()
            : postContent.ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray();

        var diffResult = DiffTools.ComputeUnifiedDiff(beforeLines, afterLines, Path.GetFileName(path));

        undoToken = Guid.NewGuid().ToString("N");
        db.FileUndos.Add(new FileUndo
        {
            Id = undoToken,
            Path = path,
            DestinationPath = destination,
            PreContent = preContent,
            PostHash = deleted ? "" : ComputeContentHash(postContent),
            ToolName = toolName,
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
}
