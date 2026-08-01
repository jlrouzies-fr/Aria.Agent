namespace Aria.Harness.Core;

/// <summary>
/// Formats an active project's <c>AGENTS.md</c> for injection into the agent system prompt.
/// Pure helpers — the Harness loads the file via Benign <c>read_file</c>; this class only
/// shapes the text the model sees.
/// </summary>
public static class AgentsMdPrompt
{
    /// <summary>Hard cap so a runaway AGENTS.md cannot dominate the context window.</summary>
    public const int MaxChars = 48_000;

    /// <summary>
    /// Build the charter addendum, or null when <paramref name="content"/> is empty after cleanup.
    /// Accepts either raw file text or the numbered form returned by the bridge's <c>read_file</c>.
    /// </summary>
    public static string? BuildAddendum(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var body = StripReadFileLineNumbers(content).Trim();
        if (body.Length == 0) return null;

        var truncated = false;
        if (body.Length > MaxChars)
        {
            body = body[..MaxChars].TrimEnd();
            truncated = true;
        }

        var note = truncated
            ? $"\n\n[AGENTS.md truncated to {MaxChars:N0} characters for context budget — open the file for the remainder.]"
            : "";

        return $"""


            ## Project Charter (AGENTS.md)

            The active project ships an `AGENTS.md` at its root. Treat the guidance below as binding for this session — it overrides generic habits when they conflict. Do not re-read the file unless the user asks or you suspect it changed.

            ---
            {body}{note}
            ---
            """;
    }

    /// <summary>
    /// Bridge <c>read_file</c> prefixes every line with <c>{n}\t</c>. Strip that so the charter
    /// reads as the human wrote it. Lines without the prefix pass through unchanged.
    /// </summary>
    public static string StripReadFileLineNumbers(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var prefix = line.AsSpan(0, tab);
            var allDigits = true;
            foreach (var c in prefix)
            {
                if (!char.IsAsciiDigit(c)) { allDigits = false; break; }
            }
            if (allDigits) lines[i] = line[(tab + 1)..];
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Join <c>AGENTS.md</c> onto a project root using the separator already present in the path,
    /// so a Windows project path stays Windows-shaped when the harness process is on another OS.
    /// </summary>
    public static string ResolvePath(string projectRoot)
    {
        var trimmed = projectRoot.TrimEnd('/', '\\');
        var sep = trimmed.Contains('\\') && !trimmed.Contains('/') ? '\\' : '/';
        return trimmed + sep + "AGENTS.md";
    }
}
