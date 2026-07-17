using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// Lists and reads files under the user's declared project locations (Terminal › Allowed Projects).
/// Backs the chat "#" file reference picker. Access is gated by the <em>node-authoritative</em> policy
/// (<see cref="NodeTerminalPolicy.ResolveAsync"/>): the node's declared Allowed Paths are the maximum
/// scope and a server-supplied request may only narrow them. An empty node list blocks every path,
/// so a compromised server can never read outside a declared project — even by supplying its own paths.
/// Over the tunnel these paths are additionally classified Sensitive (Layer B, §4): the bridge refuses
/// them unless a human-approved session context grant is live — the same grant the chat agent's
/// sensitive ops require, shared between the two surfaces.
/// </summary>
public static class ProjectFileEndpoints
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // Noise directories never worth surfacing in a file picker.
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", "bin", "obj", ".vs", ".idea", "dist", "build", "target", ".next" };

    private const int ScanCap = 20000;       // bounded walk so huge trees return promptly
    private const int MaxReadBytes = 2 * 1024 * 1024;

    public static void MapProjectFileEndpoints(this WebApplication app)
    {
        // POST /project-files/list — enumerate files under a declared project root, filtered by name.
        app.MapPost("/project-files/list", async (ProjectFilesListRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Root))
                return Results.BadRequest("root required");

            var root = Path.GetFullPath(req.Root);
            try { (await NodeTerminalPolicy.ResolveAsync(db, req.AllowedPaths)).EnforcePath(root); }
            catch (TerminalSecurityException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }

            if (!Directory.Exists(root))
                return Results.NotFound($"Directory not found: {root}");

            var filter = (req.Filter ?? "").Trim();
            var limit  = req.Limit is > 0 and <= 200 ? req.Limit.Value : 50;
            var cmp    = IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            var results = new List<(string Rel, string Abs)>();
            var scanned = 0;
            var stack   = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0 && scanned < ScanCap)
            {
                var dir = stack.Pop();
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(dir); }
                catch { continue; } // unreadable dir — skip

                foreach (var entry in entries)
                {
                    if (scanned++ >= ScanCap) break;
                    var name = Path.GetFileName(entry);
                    if (name.StartsWith('.')) continue; // dotfiles / dotdirs

                    if (Directory.Exists(entry))
                    {
                        if (!SkipDirs.Contains(name)) stack.Push(entry);
                        continue;
                    }

                    var rel = Path.GetRelativePath(root, entry);
                    if (filter.Length == 0 || rel.Contains(filter, cmp))
                        results.Add((rel, entry));
                }
            }

            // Rank: filename starts with the filter first, then shorter relative paths, then alpha.
            var ranked = results
                .OrderByDescending(r => filter.Length > 0 && Path.GetFileName(r.Rel).StartsWith(filter, cmp))
                .ThenBy(r => r.Rel.Length)
                .ThenBy(r => r.Rel, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(r => new { relPath = r.Rel.Replace('\\', '/'), absPath = r.Abs })
                .ToList();

            return Results.Ok(new { files = ranked, scanned, truncated = scanned >= ScanCap });
        });

        // POST /project-files/tree — full recursive listing (files + dirs) under a project root, for
        // building an explorer tree client-side. No filter/limit/ranking, unlike /list.
        app.MapPost("/project-files/tree", async (ProjectFilesTreeRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Root))
                return Results.BadRequest("root required");

            var root = Path.GetFullPath(req.Root);
            try { (await NodeTerminalPolicy.ResolveAsync(db, req.AllowedPaths)).EnforcePath(root); }
            catch (TerminalSecurityException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }

            if (!Directory.Exists(root))
                return Results.NotFound($"Directory not found: {root}");

            var files   = new List<string>();
            var dirs    = new List<string>();
            var scanned = 0;
            var stack   = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0 && scanned < ScanCap)
            {
                var dir = stack.Pop();
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(dir); }
                catch { continue; } // unreadable dir — skip

                foreach (var entry in entries)
                {
                    if (scanned++ >= ScanCap) break;
                    var name = Path.GetFileName(entry);
                    if (name.StartsWith('.')) continue; // dotfiles / dotdirs

                    if (Directory.Exists(entry))
                    {
                        if (SkipDirs.Contains(name)) continue;
                        dirs.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
                        stack.Push(entry);
                        continue;
                    }

                    files.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
                }
            }

            return Results.Ok(new { files, dirs, scanned, truncated = scanned >= ScanCap });
        });

        // POST /project-files/read — read a file's text content (size-capped), gated by AllowedPaths.
        app.MapPost("/project-files/read", async (ProjectFileReadRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest("path required");

            var path = Path.GetFullPath(req.Path);
            try { (await NodeTerminalPolicy.ResolveAsync(db, req.AllowedPaths)).EnforcePath(path); }
            catch (TerminalSecurityException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }

            if (!File.Exists(path))
                return Results.NotFound($"File not found: {path}");

            var info      = new FileInfo(path);
            var truncated = info.Length > MaxReadBytes;

            string content;
            if (truncated)
            {
                var buf = new char[MaxReadBytes];
                using var reader = new StreamReader(path);
                var read = reader.Read(buf, 0, MaxReadBytes);
                content = new string(buf, 0, read);
            }
            else
            {
                content = File.ReadAllText(path);
            }

            return Results.Ok(new ReadResponse(path, content, truncated, ComputeHash(content)));
        });

        // POST /project-files/write — write text content back to a file, gated by AllowedPaths.
        // Uses optimistic concurrency: the client must send the hash of the content it loaded;
        // if the file on disk has changed, we return 409 with the current content + hash.
        // On success we capture a FileUndo row so user edits are revertible like agent edits.
        app.MapPost("/project-files/write", async (ProjectFileWriteRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest("path required");

            var path = Path.GetFullPath(req.Path);
            try { (await NodeTerminalPolicy.ResolveAsync(db, req.AllowedPaths)).EnforcePath(path); }
            catch (TerminalSecurityException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }

            if (!File.Exists(path))
                return Results.NotFound($"File not found: {path}");

            var currentContent = File.ReadAllText(path);
            var currentHash = ComputeHash(currentContent);

            if (currentHash != req.BaseHash)
            {
                var conflictDiff = DiffTools.ComputeUnifiedDiff(
                    req.Content.ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray(),
                    currentContent.ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray(),
                    Path.GetFileName(path)).Diff;

                return Results.Conflict(new WriteConflictResponse(path, currentContent, currentHash, "File changed on disk since it was loaded.", conflictDiff));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, req.Content);

            var newHash = ComputeHash(req.Content);

            db.FileUndos.Add(new FileUndo
            {
                Id = Guid.NewGuid().ToString("N"),
                Path = path,
                PreContent = currentContent,
                PostHash = newHash,
                ToolName = "user_edit",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            PruneFileUndos(db);

            return Results.Ok(new WriteResponse(path, newHash));
        });

        // POST /project-files/revert — restore a file to its pre-mutation state using an undo token.
        app.MapPost("/project-files/revert", async (RevertRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.UndoToken))
                return Results.BadRequest("undoToken required");

            var undo = await db.FileUndos.FirstOrDefaultAsync(u => u.Id == req.UndoToken);
            if (undo == null)
                return Results.NotFound(new { error = "Undo token not found" });

            if (undo.RevertedAt.HasValue)
                return Results.Conflict(new { error = "Already reverted" });

            var path = undo.Path;
            try { (await NodeTerminalPolicy.ResolveAsync(db, req.AllowedPaths)).EnforcePath(path); }
            catch (TerminalSecurityException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }

            var currentExists = File.Exists(path);
            var currentHash = currentExists ? ComputeHash(File.ReadAllText(path)) : "";
            var expectedHash = undo.PostHash;

            if (currentHash != expectedHash && !req.Force)
                return Results.Conflict(new
                {
                    error = "File has changed since this mutation. Force revert to overwrite anyway.",
                    hashMismatch = true
                });

            string? reverseDiff = null;
            try
            {
                if (undo.ToolName == "delete_file")
                {
                    // Recreate the deleted file from pre-content.
                    if (undo.PreContent != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllText(path, undo.PreContent);
                        reverseDiff = DiffTools.ComputeUnifiedDiff(
                            Array.Empty<string?>(),
                            undo.PreContent.ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray(),
                            Path.GetFileName(path)).Diff;
                    }
                }
                else if (undo.ToolName == "move_path" && undo.DestinationPath != null)
                {
                    // Restore source, remove destination.
                    if (undo.PreContent != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllText(path, undo.PreContent);
                    }
                    if (File.Exists(undo.DestinationPath))
                        File.Delete(undo.DestinationPath);
                    reverseDiff = $"Restored {path} and removed {undo.DestinationPath}";
                }
                else
                {
                    // write_file / edit_file: restore pre-content (or delete if it was created).
                    if (undo.PreContent == null)
                    {
                        var before = currentExists ? File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray() : Array.Empty<string?>();
                        if (File.Exists(path)) File.Delete(path);
                        reverseDiff = DiffTools.ComputeUnifiedDiff(
                            before,
                            Array.Empty<string?>(),
                            Path.GetFileName(path)).Diff;
                    }
                    else
                    {
                        var before = currentExists ? File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray() : Array.Empty<string?>();
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllText(path, undo.PreContent);
                        reverseDiff = DiffTools.ComputeUnifiedDiff(
                            before,
                            undo.PreContent.ReplaceLineEndings("\n").Split('\n').Cast<string?>().ToArray(),
                            Path.GetFileName(path)).Diff;
                    }
                }

                undo.RevertedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                return Results.Ok(new
                {
                    path,
                    reverted = true,
                    reverseDiff
                });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Revert failed: {ex.Message}");
            }
        });
    }

    private static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

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

public record ProjectFilesListRequest(string Root, string? Filter, int? Limit, string[]? AllowedPaths);
public record ProjectFilesTreeRequest(string Root, string[]? AllowedPaths);
public record ProjectFileReadRequest(string Path, string[]? AllowedPaths);
public record RevertRequest(string UndoToken, string[]? AllowedPaths, bool Force = false);
public record ProjectFileWriteRequest(string Path, string Content, string BaseHash, string[]? AllowedPaths);
public record ReadResponse(string Path, string Content, bool Truncated, string Hash);
public record WriteResponse(string Path, string Hash);
public record WriteConflictResponse(string Path, string Content, string Hash, string Error, string? Diff);
