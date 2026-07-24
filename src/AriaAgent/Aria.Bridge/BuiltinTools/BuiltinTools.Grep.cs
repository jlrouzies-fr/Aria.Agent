using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aria.Bridge;

public static partial class BuiltinTools
{
    // Caps mirror the glob philosophy: bounded output for the model, bounded work for the bridge.
    private const int GrepMaxMatches       = 200;
    private const int GrepMaxMatchesPerFile = 20;
    private const int GrepMaxFilesVisited  = 20_000;
    private const long GrepMaxFileBytes    = 2 * 1024 * 1024;
    private static readonly TimeSpan GrepRegexTimeout = TimeSpan.FromSeconds(2);

    // Dependency/build directories that are noise for code search. Overridable via include_ignored.
    private static readonly HashSet<string> GrepSkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj"
    };

    private static IEnumerable<BridgeToolInfo> GrepToolInfos()
    {
        yield return new("grep",
            "Search file contents for a regex (or plain substring) recursively. Returns matching lines as path:line:text, up to 200 matches. Skips binary files and .git/node_modules/bin/obj unless include_ignored is true.",
            Js("""
               {"type":"object",
                "properties":{
                  "pattern":        {"type":"string","description":"Regex (default) or literal text to search for."},
                  "path":           {"type":"string","description":"File or directory to search. Directories are searched recursively. Defaults to user home."},
                  "include":        {"type":"string","description":"Optional file-name glob filter (e.g. *.cs). Only simple file patterns, not paths."},
                  "is_regex":       {"type":"boolean","description":"Treat pattern as a regular expression. Defaults to true; set false for a plain substring search."},
                  "ignore_case":    {"type":"boolean","description":"Case-insensitive matching. Defaults to false."},
                  "context_lines":  {"type":"integer","description":"Lines of context to show around each match (0-5). Defaults to 0."},
                  "include_ignored":{"type":"boolean","description":"Also search .git, node_modules, bin and obj directories. Defaults to false."}
                },
                "required":["pattern"]}
               """));
    }

    private static ToolCallResponse GrepSearch(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var pattern     = args.Str("pattern") ?? throw new ArgumentException("'pattern' is required");
        var path        = Expand(args.Str("path") ?? "~");
        var include     = args.Str("include");
        var isRegex     = args.Bool("is_regex") ?? true;
        var ignoreCase  = args.Bool("ignore_case") ?? false;
        var contextLines = Math.Clamp(args.Int("context_lines") ?? 0, 0, 5);
        var includeIgnored = args.Bool("include_ignored") ?? false;
        policy?.EnforcePath(path);

        Regex? regex = null;
        if (isRegex)
        {
            try
            {
                regex = new Regex(pattern,
                    (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Compiled,
                    GrepRegexTimeout);
            }
            catch (RegexParseException ex)
            {
                return Err($"Invalid regex: {ex.Message}");
            }
        }
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool IsMatch(string line) =>
            regex != null ? regex.IsMatch(line) : line.Contains(pattern, comparison);

        var filePattern = "*";
        if (!string.IsNullOrWhiteSpace(include))
        {
            // Only simple file-name globs (*.cs) are supported; strip any path segments.
            filePattern = include.Replace('\\', '/');
            filePattern = filePattern[(filePattern.LastIndexOf('/') + 1)..];
            if (string.IsNullOrEmpty(filePattern)) filePattern = "*";
        }

        var sb = new StringBuilder();
        int totalMatches = 0, filesWithMatches = 0, filesVisited = 0;
        var truncated = false;

        if (File.Exists(path))
        {
            GrepFile(path, IsMatch, contextLines, sb, ref totalMatches, ref filesWithMatches, ref truncated);
        }
        else if (Directory.Exists(path))
        {
            // Manual stack walk so skipped directories are never descended into (a plain
            // EnumerateFiles(AllDirectories) would still crawl every node_modules it later filters).
            var dirs = new Stack<string>();
            dirs.Push(path);
            while (dirs.Count > 0 && totalMatches < GrepMaxMatches && filesVisited < GrepMaxFilesVisited)
            {
                var dir = dirs.Pop();
                IEnumerable<string> files, subDirs;
                try
                {
                    files   = Directory.EnumerateFiles(dir, filePattern);
                    subDirs = Directory.EnumerateDirectories(dir);
                }
                catch { continue; } // inaccessible — same IgnoreInaccessible spirit as glob

                foreach (var file in files)
                {
                    if (totalMatches >= GrepMaxMatches || filesVisited >= GrepMaxFilesVisited)
                    { truncated = true; break; }
                    filesVisited++;
                    GrepFile(file, IsMatch, contextLines, sb, ref totalMatches, ref filesWithMatches, ref truncated);
                }

                foreach (var sub in subDirs)
                {
                    if (!includeIgnored && GrepSkippedDirs.Contains(Path.GetFileName(sub)))
                        continue;
                    dirs.Push(sub);
                }
            }
            if (totalMatches >= GrepMaxMatches || filesVisited >= GrepMaxFilesVisited)
                truncated = true;
        }
        else
        {
            return Err($"Path not found: {path}");
        }

        if (totalMatches == 0)
            return new ToolCallResponse($"No matches for '{pattern}' under {path}", false);

        var header = $"{totalMatches} match(es) in {filesWithMatches} file(s)";
        if (truncated) header += " (truncated — result caps reached; narrow with 'path' or 'include')";
        return new ToolCallResponse(header + "\n\n" + sb, false);
    }

    private static void GrepFile(
        string file,
        Func<string, bool> isMatch,
        int contextLines,
        StringBuilder sb,
        ref int totalMatches,
        ref int filesWithMatches,
        ref bool truncated)
    {
        try
        {
            if (new FileInfo(file).Length > GrepMaxFileBytes) return;
            if (IsBinaryFile(file)) return;

            var lines = File.ReadAllLines(file);
            var matchIndices = new List<int>();
            try
            {
                for (var i = 0; i < lines.Length && matchIndices.Count < GrepMaxMatchesPerFile; i++)
                    if (isMatch(lines[i]))
                        matchIndices.Add(i);
            }
            catch (RegexMatchTimeoutException) { return; } // pathological regex on this file — skip it

            if (matchIndices.Count == 0) return;

            // Build merged [from,to] ranges so context of adjacent matches doesn't repeat lines.
            var ranges = new List<(int From, int To)>();
            foreach (var idx in matchIndices)
            {
                var from = Math.Max(0, idx - contextLines);
                var to   = Math.Min(lines.Length - 1, idx + contextLines);
                if (ranges.Count > 0 && from <= ranges[^1].To + 1)
                    ranges[^1] = (ranges[^1].From, Math.Max(ranges[^1].To, to));
                else
                    ranges.Add((from, to));
            }

            var matchesInFile = 0;
            foreach (var (from, to) in ranges)
            {
                for (var i = from; i <= to; i++)
                {
                    var matched = matchIndices.Contains(i);
                    // grep convention: ':' for matching lines, '-' for context lines.
                    sb.Append(file).Append(matched ? ':' : '-').Append(i + 1).Append(matched ? ':' : '-')
                      .Append(' ').AppendLine(lines[i]);
                    if (matched)
                    {
                        matchesInFile++;
                        totalMatches++;
                        if (totalMatches >= GrepMaxMatches) { truncated = true; break; }
                    }
                }
                if (totalMatches >= GrepMaxMatches) break;
            }
            filesWithMatches++;
            if (matchesInFile < matchIndices.Count || matchIndices.Count >= GrepMaxMatchesPerFile)
                truncated = true;
        }
        catch { /* unreadable file — skip */ }
    }

    // Cheap binary sniff: a NUL byte in the first 8KB means not text.
    private static bool IsBinaryFile(string file)
    {
        var buf = new byte[8192];
        using var fs = File.OpenRead(file);
        var read = fs.Read(buf, 0, buf.Length);
        for (var i = 0; i < read; i++)
            if (buf[i] == 0) return true;
        return false;
    }
}
