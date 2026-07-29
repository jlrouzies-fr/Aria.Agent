namespace Aria.Harness.Context;

/// <summary>What a parsed <c>/rewind</c> chat command asks for.</summary>
public enum RewindCommandKind { MostRecent, NthRecent, Invalid }

/// <summary>Parsed form of a <c>/rewind</c> command — pure data, no host concerns.</summary>
public sealed record RewindCommandParse(
    RewindCommandKind Kind,
    int?              Steps = null,
    string?           Error = null);

/// <summary>
/// Parses the argument text of the <c>/rewind</c> chat command: bare rewinds the most recent
/// mutating turn, and <c>/rewind &lt;n&gt;</c> rewinds the nth recent mutating turn back (1-based,
/// capped by the caller). Pure logic so the chat layer stays a thin executor and the grammar is
/// unit-testable.
/// </summary>
public static class RewindCommand
{
    public const string Usage =
        "usage: /rewind — revert the most recent mutating turn · /rewind <n> — revert the nth recent mutating turn";

    public static RewindCommandParse Parse(string? args)
    {
        var text = (args ?? "").Trim();
        if (text.Length == 0)
            return new(RewindCommandKind.MostRecent, Steps: 1);

        if (int.TryParse(text, out var n) && n > 0)
            return new(RewindCommandKind.NthRecent, Steps: n);

        return new(RewindCommandKind.Invalid, Error: Usage);
    }
}
