using System.Text;

namespace Aria.Agent;

public partial class UniversalSSEStream : Stream
{
    // ── Functionary v3.x parsing (human-forced only) ──────────────────────────────
    // Functionary (via LM Studio) emits tool calls as delimiter-less content:
    //
    //     get_time
    //     {"arg": 1}
    //
    // i.e. a bare tool NAME, a newline, then the JSON arguments — with finish_reason=stop and NO
    // native tool_calls, no <tool_call> tag, nothing to auto-detect. So this path is only reached
    // when a human explicitly selects ToolCallFormat.Functionary in the override modal. The only
    // reliable signal is that the leading line is EXACTLY one of the tool names we sent the model
    // (KnownToolNames), so that is what we key on — normal prose practically never opens with an
    // exact tool name on its own line followed by '{'.

    private readonly StringBuilder _fnSniff = new();   // leading bytes held while deciding name vs prose
    private readonly StringBuilder _fnArgs  = new();   // JSON argument bytes once a call is recognised
    private string  _fnName        = "";
    private bool    _fnInArgs      = false;            // currently buffering the JSON object
    private bool    _fnPlain       = false;            // decided: this turn is prose — pass through
    private int     _fnBraceDepth  = 0;
    private bool    _fnInString    = false;
    private bool    _fnEscape      = false;

    private string FilterFunctionaryContent(string content)
    {
        if (_fnPlain)   return PassFunctionaryPlain(content);
        if (_fnInArgs)  return AppendFunctionaryArgs(content);

        _fnSniff.Append(content);
        var probe = _fnSniff.ToString().TrimStart();
        if (probe.StartsWith(">>>")) probe = probe[3..].TrimStart();  // canonical recipient marker, if present

        int nl = probe.IndexOf('\n');
        int br = probe.IndexOf('{');

        // Not enough yet to tell a name from prose — keep waiting (bounded, so runaway prose still flushes).
        if (nl < 0 && br < 0)
            return probe.Length < 80 ? "" : FlushFunctionaryPlain();

        // The candidate name is whatever precedes the first newline (or the first '{' if it comes first).
        int cut = (nl >= 0 && (br < 0 || nl < br)) ? nl : br;
        var candidate = probe[..cut].Trim();

        // "all" is functionary's prose recipient; anything not in the tool set is prose too.
        if (candidate.Length == 0 || candidate.Equals("all", StringComparison.OrdinalIgnoreCase)
            || _knownToolNamesLower is null || !_knownToolNamesLower.Contains(candidate.ToLowerInvariant()))
            return FlushFunctionaryPlain();

        // Recognised tool name → the JSON object starts at the first '{'.
        _fnName   = candidate;
        _fnInArgs = true;
        _fnSniff.Clear();
        _log?.WriteLine($"[Functionary] tool call → {_fnName}");
        int braceStart = probe.IndexOf('{');
        return braceStart >= 0 ? AppendFunctionaryArgs(probe[braceStart..]) : "";
    }

    // Feed characters into the JSON args buffer, tracking string/brace state so braces inside string
    // literals don't fool the depth counter. When the top-level object closes, emit the call; any tail
    // after it re-enters the sniff path (functionary can chain blocks).
    private string AppendFunctionaryArgs(string chunk)
    {
        for (int i = 0; i < chunk.Length; i++)
        {
            char c = chunk[i];
            _fnArgs.Append(c);

            if (_fnInString)
            {
                if (_fnEscape)            _fnEscape = false;
                else if (c == '\\')       _fnEscape = true;
                else if (c == '"')        _fnInString = false;
                continue;
            }

            switch (c)
            {
                case '"': _fnInString = true; break;
                case '{': _fnBraceDepth++;    break;
                case '}':
                    _fnBraceDepth--;
                    if (_fnBraceDepth == 0)
                    {
                        var args = _fnArgs.ToString().Trim();
                        EmitRawToolCall(_fnName, string.IsNullOrEmpty(args) ? "{}" : args);
                        ResetFunctionaryState();
                        var tail = chunk[(i + 1)..];
                        return string.IsNullOrEmpty(tail) ? "" : FilterFunctionaryContent(tail);
                    }
                    break;
            }
        }
        return "";  // still mid-object — nothing surfaces to the user as content
    }

    // This turn is prose: stop sniffing, hand back what we buffered (minus any leading recipient
    // marker) and pass everything afterwards straight through.
    private string FlushFunctionaryPlain()
    {
        _fnPlain = true;
        var buf = _fnSniff.ToString();
        _fnSniff.Clear();
        return PassFunctionaryPlain(buf, initial: true);
    }

    private string PassFunctionaryPlain(string content, bool initial = false)
    {
        if (!initial) return content;
        // Strip a leading ">>>all\n" / ">>>" / "all\n" recipient marker from the very first prose chunk.
        var s = content;
        var lead = s.TrimStart();
        int consumed = s.Length - lead.Length;
        if (lead.StartsWith(">>>")) { lead = lead[3..]; consumed += 3; }
        if (lead.StartsWith("all\n", StringComparison.OrdinalIgnoreCase)) consumed += 4;
        else if (lead.StartsWith("all\r\n", StringComparison.OrdinalIgnoreCase)) consumed += 5;
        return consumed > 0 && consumed <= content.Length ? content[consumed..] : content;
    }

    private void ResetFunctionaryState()
    {
        _fnArgs.Clear();
        _fnName       = "";
        _fnInArgs     = false;
        _fnBraceDepth = 0;
        _fnInString   = false;
        _fnEscape     = false;
        // _fnPlain intentionally left as-is: once prose, stay prose for the rest of the turn.
    }

    // Lower-cased copy of KnownToolNames for case-insensitive matching, computed lazily.
    private IReadOnlySet<string>? _knownToolNamesLowerCache;
    private IReadOnlySet<string>? _knownToolNamesLower =>
        _knownToolNamesLowerCache ??= KnownToolNames is null
            ? null
            : KnownToolNames.Select(n => n.ToLowerInvariant()).ToHashSet();
}
