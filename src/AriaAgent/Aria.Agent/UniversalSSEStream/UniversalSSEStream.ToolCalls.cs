using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aria.Agent;

public partial class UniversalSSEStream : Stream
{
    // ── Tool-call state ───────────────────────────────────────────────────────
    private readonly StringBuilder _toolCallBuffer = new();
    private bool          _inToolCall      = false;
    private string?       _activeToolClose = null;   // closing tag we expect
    private ToolCallFormat _activeToolFmt  = ToolCallFormat.Unknown;
    private bool          _hadToolCalls    = false;   // set only by our own tag/XML rewriters (EmitRawToolCall)
    private bool          _sawNativeToolCalls = false; // set when the model streams native OpenAI tool_calls deltas unmodified
    private int           _toolCallIndex   = 0;
    private string        _completionId    = "chatcmpl-universal";
    private string?       _deferredFinishReasonLine = null;

    // Mistral: [TOOL_CALLS] prefix — rest of response is the JSON array
    private bool          _inMistralToolCalls = false;
    private readonly StringBuilder _mistralBuffer = new();

    // GLM: accumulate arg_key/arg_value pairs without a wrapper
    private readonly Dictionary<string, string> _glmArgs    = new();
    private string?       _glmCurrentKey = null;
    private string        _glmToolName   = "";

    // ── Tool-call tag patterns (open → close → format) ────────────────────────
    private record ToolPattern(string Open, string Close, ToolCallFormat Fmt);
    private static readonly ToolPattern[] ToolPatterns =
    [
        new("<tool_call>",                  "</tool_call>",                 ToolCallFormat.ToolCallTag),
        new("<start_function_call>",        "<end_function_call>",          ToolCallFormat.StartFunctionCall),
        new("<minimax:tool_call>",          "</minimax:tool_call>",         ToolCallFormat.MinimaxToolCall),
        new("<longcat_tool_call>",          "</longcat_tool_call>",         ToolCallFormat.Longcat),
        new("<|tool_calls_section_begin|>", "<|tool_calls_section_end|>",  ToolCallFormat.KimiK2),
        new("<|tool_call>",                 "<tool_call|>",                 ToolCallFormat.Gemma4ToolCall),
    ];
    // ── Tool-call tag filtering ───────────────────────────────────────────────

    private string FilterInsideToolCall(string content)
    {
        var closeTag = _activeToolClose ?? "</tool_call>";
        int end = content.IndexOf(closeTag, StringComparison.OrdinalIgnoreCase);
        if (end < 0) { _toolCallBuffer.Append(content); return ""; }

        _toolCallBuffer.Append(content[..end]);
        _inToolCall = false; _activeToolClose = null;

        // KimiK2 may have multiple tool calls separated by inner tags
        if (_activeToolFmt == ToolCallFormat.KimiK2)
            EmitKimiToolCalls(_toolCallBuffer.ToString());
        else
            EmitParsedToolCall();

        return FilterContent(content[(end + closeTag.Length)..]);
    }
    // ── GLM <arg_key>/<arg_value> filtering ───────────────────────────────────

    private string FilterGlmContent(string content)
    {
        // GLM format: optional function name line, then <arg_key>…</arg_key><arg_value>…</arg_value>
        // We accumulate pairs until there are no more
        var result = new StringBuilder();
        var remaining = content;

        while (remaining.Length > 0)
        {
            int keyOpen = remaining.IndexOf("<arg_key>",   StringComparison.OrdinalIgnoreCase);
            int valOpen = remaining.IndexOf("<arg_value>", StringComparison.OrdinalIgnoreCase);

            if (keyOpen >= 0)
            {
                result.Append(remaining[..keyOpen]);
                int keyClose = remaining.IndexOf("</arg_key>", keyOpen, StringComparison.OrdinalIgnoreCase);
                if (keyClose < 0) { _glmCurrentKey = remaining[(keyOpen + 9)..]; return result.ToString(); }
                _glmCurrentKey = remaining[(keyOpen + 9)..keyClose];
                remaining = remaining[(keyClose + 10)..];
                continue;
            }
            if (valOpen >= 0 && _glmCurrentKey != null)
            {
                result.Append(remaining[..valOpen]);
                int valClose = remaining.IndexOf("</arg_value>", valOpen, StringComparison.OrdinalIgnoreCase);
                if (valClose < 0) { _glmArgs[_glmCurrentKey] = remaining[(valOpen + 11)..]; return result.ToString(); }
                _glmArgs[_glmCurrentKey] = remaining[(valOpen + 11)..valClose];
                remaining = remaining[(valClose + 12)..];
                _glmCurrentKey = null;
                continue;
            }

            result.Append(remaining);
            break;
        }

        // If we collected args, emit the tool call
        if (_glmArgs.Count > 0)
        {
            var argsNode = new JsonObject();
            foreach (var kv in _glmArgs) argsNode[kv.Key] = kv.Value;
            EmitRawToolCall(string.IsNullOrEmpty(_glmToolName) ? "glm_function" : _glmToolName,
                            argsNode.ToJsonString());
            _glmArgs.Clear();
        }

        return result.ToString();
    }
    // ── Kimi K2 multi-call parsing ────────────────────────────────────────────

    private void EmitKimiToolCalls(string block)
    {
        // Kimi K2 inner format: <|tool_call_begin|>function<|tool_sep|>name\njson<|tool_call_end|>
        const string innerOpen  = "<|tool_call_begin|>";
        const string innerSep   = "<|tool_sep|>";
        const string innerClose = "<|tool_call_end|>";

        _toolCallBuffer.Clear();

        // If the block has inner markers, parse each call individually
        if (block.Contains(innerOpen, StringComparison.OrdinalIgnoreCase))
        {
            var rest = block;
            while (true)
            {
                int s = rest.IndexOf(innerOpen, StringComparison.OrdinalIgnoreCase);
                if (s < 0) break;
                int e = rest.IndexOf(innerClose, s, StringComparison.OrdinalIgnoreCase);
                if (e < 0) break;
                var inner = rest[(s + innerOpen.Length)..e];
                int sep   = inner.IndexOf(innerSep, StringComparison.OrdinalIgnoreCase);
                var body  = sep >= 0 ? inner[(sep + innerSep.Length)..] : inner;
                _toolCallBuffer.Clear();
                _toolCallBuffer.Append(body.Trim());
                EmitParsedToolCall();
                rest = rest[(e + innerClose.Length)..];
            }
        }
        else
        {
            // Treat entire block as JSON (fallback)
            _toolCallBuffer.Clear();
            _toolCallBuffer.Append(block.Trim());
            EmitParsedToolCall();
        }
    }
    // ── Mistral [TOOL_CALLS] buffer flush ─────────────────────────────────────

    private void FlushMistralBuffer()
    {
        _inMistralToolCalls = false;
        var raw = _mistralBuffer.ToString().Trim();
        _mistralBuffer.Clear();
        _log?.WriteLine($"[Mistral] flushing {raw.Length}ch");

        // Format: [{"name":"func","arguments":"{}"}]
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var args = el.TryGetProperty("arguments", out var a)
                    ? (a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText())
                    : "{}";
                if (!string.IsNullOrEmpty(name))
                    EmitRawToolCall(name, args);
            }
        }
        catch (JsonException ex) { _log?.WriteLine($"[Mistral] parse error: {ex.Message}"); }
    }
    // ── Tool-call parsing and emission ────────────────────────────────────────

    private void EmitParsedToolCall()
    {
        var raw = _toolCallBuffer.ToString().Trim();
        _toolCallBuffer.Clear();
        _log?.WriteLine($"[tool_call raw] {raw.Replace("\n", "\\n")[..Math.Min(120, raw.Length)]}");

        var (name, args) = ParseToolCallText(raw);
        if (string.IsNullOrEmpty(name)) { _log?.WriteLine("[tool_call] no name — skipping"); return; }
        EmitRawToolCall(name, args);
    }
    private void EmitRawToolCall(string name, string arguments)
    {
        _log?.WriteLine($"[emit tool_call] name={name}");
        var callId = "call_" + Guid.NewGuid().ToString("N")[..15];
        var root = new JsonObject
        {
            ["id"]      = _completionId,
            ["object"]  = "chat.completion.chunk",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject
                    {
                        ["tool_calls"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["index"]    = _toolCallIndex,
                                ["id"]       = callId,
                                ["type"]     = "function",
                                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments }
                            }
                        }
                    }
                }
            }
        };
        Enqueue("data: " + root.ToJsonString() + "\n");
        _hadToolCalls = true;
        _toolCallIndex++;
    }
    private static (string name, string arguments) ParseToolCallText(string raw)
    {
        raw = raw.Trim();

        // JSON object: {"name": "...", "arguments": {...}}
        if (raw.StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var args = doc.RootElement.TryGetProperty("arguments", out var a)
                    ? (a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText())
                    : "{}";
                if (!string.IsNullOrEmpty(name)) return (name, args);
            }
            catch { }
        }

        // XML <function=Name> ... </function>  or  <function>Name …
        var mXml = Regex.Match(raw, @"<function[=]?([^>]+)>(.*?)</function>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (mXml.Success)
        {
            var name     = mXml.Groups[1].Value.Trim();
            var argsText = mXml.Groups[2].Value.Trim();
            return (name, string.IsNullOrEmpty(argsText) ? "{}" : XmlParamsToJson(argsText));
        }

        // GLM <arg_key>…</arg_key><arg_value>…</arg_value> pairs (no wrapper)
        if (raw.Contains("<arg_key>", StringComparison.OrdinalIgnoreCase))
        {
            var pairs = Regex.Matches(raw,
                @"<arg_key>\s*(.*?)\s*</arg_key>\s*<arg_value>\s*(.*?)\s*</arg_value>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (pairs.Count > 0)
            {
                var obj = new JsonObject();
                foreach (Match pm in pairs) obj[pm.Groups[1].Value] = pm.Groups[2].Value;
                return ("glm_function", obj.ToJsonString());
            }
        }

        // Gemma 4: call:server:tool_name{args}  — keys may be unquoted
        if (raw.StartsWith("call:", StringComparison.OrdinalIgnoreCase))
        {
            var braceAt = raw.IndexOf('{');
            if (braceAt > 5)
            {
                var namePart = raw[5..braceAt]; // strip "call:"
                var toolName = namePart.Contains(':')
                    ? namePart[(namePart.LastIndexOf(':') + 1)..].Trim()
                    : namePart.Trim();
                var argsRaw  = raw[braceAt..].Trim();
                var args     = FixUnquotedJsonKeys(argsRaw);
                if (!string.IsNullOrEmpty(toolName)) return (toolName, args);
            }
        }

        // Newline-separated: first line = function name, rest = JSON args
        var lines = raw.Split('\n', 2, StringSplitOptions.TrimEntries);
        if (lines.Length == 2)
        {
            var possibleName = lines[0].Trim('`', ' ', '\r');
            var possibleArgs = lines[1].Trim();
            if (!string.IsNullOrEmpty(possibleName) && possibleArgs.StartsWith("{"))
            {
                try { JsonDocument.Parse(possibleArgs); return (possibleName, possibleArgs); }
                catch { }
            }
        }

        return ("", "{}");
    }
    // Gemma 4 emits JSON args with bare (unquoted) keys, e.g. {queries:[…]}.
    // Quotes every bare word immediately after { or , before a colon.
    private static string FixUnquotedJsonKeys(string json)
    {
        try { JsonDocument.Parse(json); return json; } catch { }
        var fixed_ = Regex.Replace(json,
            @"(?<=[{,]\s*)([A-Za-z_]\w*)(?=\s*:)",
            "\"$1\"",
            RegexOptions.None);
        try { JsonDocument.Parse(fixed_); return fixed_; } catch { return json; }
    }
    private static string XmlParamsToJson(string argsText)
    {
        var matches = Regex.Matches(argsText, @"<parameter=(\w+)>\s*(.*?)\s*</parameter>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (matches.Count == 0) return argsText;
        var obj = new JsonObject();
        foreach (Match pm in matches)
            obj[pm.Groups[1].Value] = pm.Groups[2].Value;
        return obj.ToJsonString();
    }
}
