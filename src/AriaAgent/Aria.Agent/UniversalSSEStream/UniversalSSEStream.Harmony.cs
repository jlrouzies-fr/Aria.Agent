using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aria.Agent;

/// <summary>
/// Harmony (OpenAI GPT-OSS) parsing support for <see cref="UniversalSSEStream"/>.
///
/// GPT-OSS emits a channel-based envelope inside the <c>content</c> delta:
///   <|channel|>analysis<|message|>internal monologue<|end|>
///   <|start|>assistant<|channel|>final<|message|>visible answer<|end|>
///   <|start|>assistant<|channel|>commentary to=functions.Name <|constrain|>json<|message|>{"arg":1}<|call|>
///
/// We treat <c>analysis</c> as reasoning, <c>final</c> as visible content, and any
/// <c>commentary</c> / <c>analysis</c> channel with <c>to=functions.X</c> as a tool call.
/// </summary>
public partial class UniversalSSEStream : Stream
{
    // ── Harmony state ─────────────────────────────────────────────────────────
    private bool          _inHarmony;          // Harmony envelope detected
    private string        _harmonyChannel = ""; // current channel name (analysis/commentary/final/...)
    private string?       _harmonyToolName;     // function name when channel is "to=functions.X"
    private readonly StringBuilder _harmonyToolArgs = new();
    private string        _harmonyPending = ""; // partial control-token tail held across chunks
    private bool          _harmonyInConstrain;  // dropping text after <|constrain|> until <|message|>

    // ── Public entry point used by FilterContent ──────────────────────────────

    private string FilterHarmonyContent(string content)
    {
        _inHarmony = true;

        if (_harmonyPending.Length > 0)
        {
            content = _harmonyPending + content;
            _harmonyPending = "";
        }

        var output = new StringBuilder();
        var remaining = content;

        while (remaining.Length > 0)
        {
            if (!TryFindHarmonyControlToken(remaining, 0, out var idx, out var token, out var len))
            {
                var (main, tail) = StripPartialHarmonyTail(remaining);
                AppendHarmonyText(main, output);
                _harmonyPending = tail;
                break;
            }

            // Text before the control token belongs to the current channel.
            AppendHarmonyText(remaining[..idx], output);
            remaining = remaining[(idx + len)..];

            switch (token.ToLowerInvariant())
            {
                case "channel":
                    // Channel declaration runs until the next control token. If the declaration
                    // itself is split across SSE chunks, hold it back intact so we don't mis-parse
                    // a partial "to=functions." prefix.
                    if (!TryFindHarmonyControlToken(remaining, 0, out var declIdx, out _, out _))
                    {
                        _harmonyPending = "<|channel|>" + remaining;
                        remaining = "";
                    }
                    else
                    {
                        ParseHarmonyChannelDeclaration(remaining[..declIdx]);
                        remaining = remaining[declIdx..];
                    }
                    break;

                case "call":
                    // End of a Harmony tool call.
                    FlushHarmonyToolCall();
                    break;

                case "message":
                    _harmonyInConstrain = false;
                    break;
                case "end":
                case "start":
                    break;
                case "constrain":
                    _harmonyInConstrain = true;
                    break;
            }
        }

        return output.ToString();
    }

    // ── Harmony control-token scanner ─────────────────────────────────────────

    private static bool TryFindHarmonyControlToken(ReadOnlySpan<char> s, int start, out int index, out string token, out int length)
    {
        index = s.Slice(start).IndexOf("<|".AsSpan());
        if (index < 0) { token = ""; length = 0; return false; }
        index += start;

        var afterOpen = s.Slice(index + 2);
        int end = afterOpen.IndexOf("|>".AsSpan());
        if (end < 0) { token = ""; length = 0; return false; }

        token = s.Slice(index + 2, end).ToString();
        length = end + 4; // include "<|" and "|>"
        return true;
    }

    private static (string main, string tail) StripPartialHarmonyTail(string s)
    {
        int lastOpen = s.LastIndexOf("<|", StringComparison.Ordinal);
        if (lastOpen >= 0 && s.IndexOf("|>", lastOpen, StringComparison.Ordinal) < 0)
            return (s[..lastOpen], s[lastOpen..]);

        if (s.Length > 0 && s[^1] == '<')
            return (s[..^1], "<");

        return (s, "");
    }

    // ── Channel routing ───────────────────────────────────────────────────────

    private void AppendHarmonyText(string text, StringBuilder output)
    {
        if (text.Length == 0) return;

        // Constraint text (e.g. "json" after <|constrain|>) is not part of the message.
        if (_harmonyInConstrain)
            return;

        // Structural text outside any channel (e.g. "assistant" after <|start|>) is dropped.
        if (string.IsNullOrEmpty(_harmonyChannel))
            return;

        if (_harmonyToolName != null)
        {
            _harmonyToolArgs.Append(text);
            return;
        }

        if (_harmonyChannel.Equals("analysis", StringComparison.OrdinalIgnoreCase))
        {
            AppendThink(text);
            return;
        }

        // final / commentary without a tool recipient / unknown channel → visible content
        output.Append(text);
    }

    private void ParseHarmonyChannelDeclaration(string declaration)
    {
        var decl = declaration.Trim();
        _log?.WriteLine($"[Harmony] channel decl: {decl}");

        // Flush any previous channel first and end any active constraint.
        FlushHarmonyChannel();
        _harmonyInConstrain = false;

        // Commentary / analysis channel aimed at a function.
        var fnMatch = Regex.Match(decl, @"^(analysis|commentary)\s+to=functions\.(\S+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (fnMatch.Success)
        {
            _harmonyChannel = fnMatch.Groups[1].Value;
            _harmonyToolName = fnMatch.Groups[2].Value;
            return;
        }

        // Strip any trailing qualifiers (e.g. commentary with no recipient).
        var channelName = decl.Split([' ', '\t'], 2)[0];
        _harmonyChannel = channelName;
        _harmonyToolName = null;
    }

    private void FlushHarmonyChannel()
    {
        if (_harmonyToolName != null)
        {
            FlushHarmonyToolCall();
        }
        else if (_harmonyChannel.Equals("analysis", StringComparison.OrdinalIgnoreCase))
        {
            FlushThinkBuf();
        }

        _harmonyChannel = "";
        _harmonyToolName = null;
        _harmonyInConstrain = false;
    }

    private void FlushHarmonyToolCall()
    {
        var name = _harmonyToolName;
        var rawArgs = _harmonyToolArgs.ToString().Trim();
        _harmonyToolArgs.Clear();
        _harmonyToolName = null;

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(rawArgs))
            return;

        // Args may be bare JSON, optionally wrapped in the channel text.
        var args = rawArgs;
        if (!args.StartsWith("{") && !args.StartsWith("["))
        {
            var braceAt = args.IndexOf('{');
            var bracketAt = args.IndexOf('[');
            var startAt = braceAt >= 0 && (bracketAt < 0 || braceAt < bracketAt)
                ? braceAt
                : bracketAt;
            if (startAt >= 0)
                args = args[startAt..];
        }

        // Validate JSON; if invalid, fall back to the raw string so the caller sees something.
        try { JsonDocument.Parse(args); }
        catch { args = rawArgs; }

        _log?.WriteLine($"[Harmony] emitting tool_call {name} args={args.Replace("\n", "\\n")[..Math.Min(120, args.Length)]}");
        EmitRawToolCall(name, args);
    }
}
