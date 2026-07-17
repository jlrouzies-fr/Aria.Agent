using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Aria.Bridge.Endpoints;

public record TerminalCompleteRequest(
    string Line,
    int Cursor,
    string? Cwd = null,
    string? SessionId = null,
    string[]? AllowedPaths = null);

public record TerminalCompleteCandidate(string Text, bool IsDir);

public record TerminalCompleteResponse(
    int ReplaceStart,
    int ReplaceEnd,
    string CommonPrefix,
    IReadOnlyList<TerminalCompleteCandidate> Candidates,
    bool Truncated);

/// <summary>
/// Shell-style Tab completion for the shared terminal panel. Runs natively against the cogitator
/// node's filesystem (no compgen) and respects the same <see cref="SecurityPolicy"/> as execution.
/// </summary>
public static class TerminalCompletion
{
    private const int MaxCandidates = 200;
    private static readonly TimeSpan PathCacheTtl = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PathIndexTtl = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<string, (DateTime At, string[] Entries)> _listingCache = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, string[] Commands)> _pathIndexCache = new();

    private static readonly string[] ShellBuiltins =
    [
        "cd", "export", "unset", "source", ".", "alias", "unalias", "echo", "pwd",
        "exit", "return", "read", "shift", "eval", "exec", "type", "hash", "help"
    ];

    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly bool IsCaseInsensitive = IsWindows || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static TerminalCompleteResponse Complete(
        string line,
        int cursor,
        string cwd,
        SecurityPolicy policy)
    {
        if (string.IsNullOrEmpty(line)) line = "";
        cursor = Math.Clamp(cursor, 0, line.Length);

        var token = Tokenize(line, cursor, out var replaceStart, out var replaceEnd, out var quoting);
        var isFirstToken = IsFirstToken(line, replaceStart);
        var afterCd = IsAfterBareCd(line, replaceStart);

        var candidates = isFirstToken && !LooksLikePath(token)
            ? CompleteCommand(token ?? "")
            : CompletePath(token, cwd, policy,
                directoriesOnly: afterCd,
                executablesOnly: isFirstToken && IsExecutablePathPrefix(token));

        var effectivePrefix = isFirstToken && !LooksLikePath(token)
            ? (token ?? "")
            : GetPathPrefix(token);

        var matches = FilterAndSort(candidates, effectivePrefix);
        var truncated = matches.Count > MaxCandidates;
        if (truncated) matches = matches.Take(MaxCandidates).ToList();

        var commonPrefix = ComputeCommonPrefix(matches, effectivePrefix);

        // For a single directory match with no further extension possible, offer the trailing slash.
        if (matches.Count == 1 && matches[0].IsDir && !commonPrefix.EndsWith('/'))
            commonPrefix += "/";

        // Escaping: backslash-escape spaces in inserted text when the user is not inside quotes.
        var insertablePrefix = quoting == QuoteKind.None ? EscapeForShell(commonPrefix) : commonPrefix;

        return new TerminalCompleteResponse(replaceStart, replaceEnd, insertablePrefix, matches, truncated);
    }

    // ── Tokenizer ─────────────────────────────────────────────────────────────

    private enum QuoteKind { None, Double, Single }

    private static string? Tokenize(string line, int cursor, out int replaceStart, out int replaceEnd, out QuoteKind quoting)
    {
        replaceStart = 0;
        replaceEnd = cursor;
        quoting = QuoteKind.None;

        // Scan backward to find the token start (space not escaped/quoted).
        int i = cursor;
        while (i > 0)
        {
            int prev = i - 1;
            char c = line[prev];
            if (c == ' ')
            {
                // Is it escaped? Walk back over even number of backslashes.
                int bs = 0;
                int j = prev;
                while (j > 0 && line[j - 1] == '\\') { bs++; j--; }
                if (bs % 2 == 0) break; // unescaped space = token boundary
            }
            i = prev;
        }
        replaceStart = i;

        // Determine quoting state by scanning from the token start.
        QuoteKind q = QuoteKind.None;
        for (int k = replaceStart; k < cursor; k++)
        {
            char c = line[k];
            if (c == '\\') { k++; continue; }
            if (q == QuoteKind.None && c == '"') { q = QuoteKind.Double; continue; }
            if (q == QuoteKind.None && c == '\'') { q = QuoteKind.Single; continue; }
            if (q == QuoteKind.Double && c == '"') { q = QuoteKind.None; continue; }
            if (q == QuoteKind.Single && c == '\'') { q = QuoteKind.None; continue; }
        }
        quoting = q;

        var raw = line[replaceStart..cursor];
        // Strip the leading quote if we are inside it.
        if ((q == QuoteKind.Double && raw.StartsWith("\"")) ||
            (q == QuoteKind.Single && raw.StartsWith("'")))
            raw = raw[1..];

        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    private static bool IsFirstToken(string line, int replaceStart)
    {
        for (int i = 0; i < replaceStart; i++)
            if (!char.IsWhiteSpace(line[i])) return false;
        return true;
    }

    private static bool IsAfterBareCd(string line, int replaceStart)
    {
        // Walk back over whitespace to find the previous token; return true only if it is a bare "cd".
        int i = replaceStart - 1;
        while (i >= 0 && char.IsWhiteSpace(line[i])) i--;
        if (i < 0) return false;
        int end = i + 1;
        while (i >= 0 && !char.IsWhiteSpace(line[i])) i--;
        var prev = line[(i + 1)..end];
        return prev.Equals("cd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePath(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        return token.StartsWith('/') || token.StartsWith('~') || token.StartsWith("./") || token.StartsWith("../");
    }

    private static bool IsExecutablePathPrefix(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        return token.StartsWith('/') || token.StartsWith('~') || token.StartsWith("./") || token.StartsWith("../");
    }

    private static string GetPathPrefix(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        var expanded = ExpandTilde(token);
        var lastSlash = expanded.LastIndexOfAny(['/', Path.DirectorySeparatorChar]);
        if (lastSlash < 0) return expanded;
        return expanded[(lastSlash + 1)..];
    }

    // ── Command completion ────────────────────────────────────────────────────

    private static IEnumerable<TerminalCompleteCandidate> CompleteCommand(string prefix)
    {
        var builtins = ShellBuiltins
            .Where(b => PrefixMatches(b, prefix))
            .Select(b => new TerminalCompleteCandidate(b, false));

        var pathCommands = GetPathCommands()
            .Where(c => PrefixMatches(c, prefix))
            .Select(c => new TerminalCompleteCandidate(c, false));

        return builtins.Concat(pathCommands);
    }

    private static string[] GetPathCommands()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var key = path;
        if (_pathIndexCache.TryGetValue(key, out var cached) && cached.At > DateTime.UtcNow - PathIndexTtl)
            return cached.Commands;

        var list = new List<string>();
        var seen = new HashSet<string>(IsCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var extensions = GetPathExtensions();

        foreach (var dir in path.Split(Path.PathSeparator).Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            // Command completion is not gated by AllowedPaths — the policy restricts filesystem
            // access for file/path operations, not which binaries the shell can resolve from PATH.
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        var name = Path.GetFileName(file);
                        if (string.IsNullOrEmpty(name)) continue;
                        if (IsWindows)
                        {
                            var ext = Path.GetExtension(name);
                            if (!extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                            name = Path.GetFileNameWithoutExtension(name);
                        }
                        if (seen.Add(name))
                            list.Add(name);
                    }
                    catch { }
                }
            }
            catch { }
        }

        var arr = list.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray();
        _pathIndexCache[key] = (DateTime.UtcNow, arr);
        return arr;
    }

    private static string[] GetPathExtensions()
    {
        var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".com;.exe;.bat;.cmd";
        return pathext.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(e => e.ToLowerInvariant()).ToArray();
    }

    // ── Path completion ───────────────────────────────────────────────────────

    private static IEnumerable<TerminalCompleteCandidate> CompletePath(
        string? token, string cwd, SecurityPolicy policy, bool directoriesOnly, bool executablesOnly)
    {
        var (dir, prefix) = SplitPathToken(token, cwd);
        // Only expand ~ here; BuiltinTools.Expand also calls Path.GetFullPath on relative paths,
        // which resolves them against the bridge process cwd instead of the terminal session cwd.
        var expandedDir = ExpandTilde(dir);
        var resolvedDir = Path.IsPathRooted(expandedDir)
            ? expandedDir
            : Path.GetFullPath(Path.Combine(ExpandTilde(cwd), expandedDir));

        try
        {
            policy.EnforcePath(resolvedDir);
        }
        catch
        {
            return [];
        }

        if (!Directory.Exists(resolvedDir))
            return [];

        var entries = ListDirectory(resolvedDir);
        var candidates = new List<TerminalCompleteCandidate>();
        var extensions = GetPathExtensions();

        foreach (var full in entries)
        {
            try
            {
                var name = Path.GetFileName(full);
                if (string.IsNullOrEmpty(name)) continue;
                if (!ShowHidden(prefix) && name.StartsWith('.')) continue;

                bool isDir = Directory.Exists(full);
                if (directoriesOnly && !isDir) continue;

                if (executablesOnly && !isDir && !IsExecutableFile(full, extensions))
                    continue;

                var display = isDir ? name + "/" : name;
                candidates.Add(new TerminalCompleteCandidate(display, isDir));
            }
            catch { }
        }

        return candidates;
    }

    private static bool IsExecutableFile(string path, string[] extensions)
    {
        if (OperatingSystem.IsWindows())
        {
            var ext = Path.GetExtension(path);
            return extensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & UnixFileMode.UserExecute) != 0
                || (mode & UnixFileMode.GroupExecute) != 0
                || (mode & UnixFileMode.OtherExecute) != 0;
        }
        catch { return false; }
    }

    private static (string Dir, string Prefix) SplitPathToken(string? token, string cwd)
    {
        if (string.IsNullOrEmpty(token)) return (cwd, "");

        // Expand ~ but leave ./ and ../ for Path.Combine to resolve.
        var expanded = ExpandTilde(token);

        var lastSlash = expanded.LastIndexOfAny(['/', Path.DirectorySeparatorChar]);
        if (lastSlash < 0) return (cwd, expanded);

        var dir = expanded[..lastSlash];
        var prefix = expanded[(lastSlash + 1)..];

        if (string.IsNullOrEmpty(dir))
            dir = "/"; // absolute root

        return (dir, prefix);
    }

    private static string ExpandTilde(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~") return home;
        if (path.StartsWith("~/") || path.StartsWith("~\\"))
            return Path.Combine(home, path[2..]);
        return path;
    }

    private static string[] ListDirectory(string dir)
    {
        var key = dir;
        if (_listingCache.TryGetValue(key, out var cached) && cached.At > DateTime.UtcNow - PathCacheTtl)
            return cached.Entries;

        try
        {
            var files = Directory.EnumerateFileSystemEntries(dir).ToArray();
            _listingCache[key] = (DateTime.UtcNow, files);
            return files;
        }
        catch
        {
            return [];
        }
    }

    // ── Filtering / common prefix ─────────────────────────────────────────────

    private static List<TerminalCompleteCandidate> FilterAndSort(IEnumerable<TerminalCompleteCandidate> candidates, string prefix)
    {
        var matches = candidates
            .Where(c => PrefixMatches(c.Text, prefix))
            .ToList();

        matches.Sort((a, b) =>
        {
            // Directories first, then alphabetical.
            int dirCmp = b.IsDir.CompareTo(a.IsDir);
            if (dirCmp != 0) return dirCmp;
            return string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);
        });
        return matches;
    }

    private static bool PrefixMatches(string candidate, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return true;
        var compare = IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.StartsWith(prefix, compare);
    }

    private static string ComputeCommonPrefix(IReadOnlyList<TerminalCompleteCandidate> matches, string typedPrefix)
    {
        if (matches.Count == 0) return "";
        var compare = IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var first = matches[0].Text;
        var length = first.Length;
        foreach (var m in matches.Skip(1))
        {
            int i = 0;
            while (i < length && i < m.Text.Length &&
                   string.Equals(first[i].ToString(), m.Text[i].ToString(), compare))
                i++;
            length = i;
        }
        var common = first[..length];
        if (common.StartsWith(typedPrefix, compare))
            return common[typedPrefix.Length..];
        return common;
    }

    private static bool ShowHidden(string prefix) => prefix.StartsWith('.');

    private static string EscapeForShell(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == ' ' || c == '\\' || c == '"' || c == '\'' || c == ';' || c == '&' || c == '|' || c == '<' || c == '>' || c == '$' || c == '`')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
