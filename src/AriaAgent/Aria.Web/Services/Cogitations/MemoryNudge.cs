using System.Text.RegularExpressions;

namespace Aria.Web.Services.Cogitations;

/// <summary>
/// Per-turn memory nudge (AutoMemoryMode.ModelAuto only). The system-prompt memory policy alone
/// loses to in-context history: once a conversation contains a few "user defers → agent just
/// acknowledges" turns, the model copies that pattern instead of calling Inscribe. Appending a
/// bracketed note directly to the user turn when the text smells like a preference / decision /
/// deferral / correction breaks the pattern at the exact moment it matters — the same mechanism
/// as <see cref="CogitationRunRegistry"/>'s ContextRetryNudge. The note is only sent to the model;
/// the persisted/displayed user message keeps the original text.
/// </summary>
public static partial class MemoryNudge
{
    public const string NudgeText =
        "\n\n[SYSTEM NOTE — not part of the user's message]: this message may contain a preference, " +
        "decision, correction, or deferred work worth persisting. If it does and memory tools are " +
        "available in this session, call Inscribe now — with the fact and, for deferred work, what " +
        "was deferred and why — before replying. If nothing here has value beyond this session, " +
        "ignore this note.";

    // English + French triggers, grouped by intent. Kept deliberately phrase-level (not single
    // words like "always" or "instead") so ordinary requests don't nudge on every turn.
    [GeneratedRegex(
        @"\b(later|another time|some other time|another day|revisit|come back to|circle back|park (this|that|it)|not now|remind me|for another time)\b" +
        @"|\b(i prefer|i'd rather|i like|i don't like|i hate|from now on|going forward|i always|you always|never do|don't ever)\b" +
        @"|\b(no,? actually|actually,? (use|it's|it is)|not that one|stop (using|doing))\b" +
        @"|\b(remember (this|that|it|to)|don't forget|keep in mind|worth remembering|take note)\b" +
        @"|\b(plus tard|on verra|je préfère|souviens-toi|rappelle-moi|n'oublie pas|à partir de maintenant)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TriggerPattern();

    // Very short messages ("ok", "later!") carry nothing worth persisting even when they match.
    private const int MinLength = 12;

    public static bool ShouldNudge(string? userText) =>
        !string.IsNullOrWhiteSpace(userText)
        && userText.Length >= MinLength
        && TriggerPattern().IsMatch(userText);
}
