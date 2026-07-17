using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aria.Agent;

/// <summary>
/// Intercepts FoundryLocal streaming completions and handles two Qwen quirks:
/// 1. &lt;think&gt;...&lt;/think&gt; blocks — buffered and delivered via OnThinkContent.
/// 2. &lt;tool_call&gt;...&lt;/tool_call&gt; — parsed and re-emitted as OpenAI tool_calls deltas.
///
/// Rendering is injected via OnThinkContent so callers decide how to display
/// think blocks (Spectre panel in console, log entry in web, etc.).
/// </summary>
public class FoundryLocalReasoningHandler : DelegatingHandler
{
    private readonly bool _qwenToolCalls;

    /// <summary>Called once per think block with the full buffered reasoning text.</summary>
    public Action<string>? OnThinkContent { get; set; }

    public FoundryLocalReasoningHandler(bool qwenToolCalls = false)
    {
        _qwenToolCalls = qwenToolCalls;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);

        if (request.RequestUri?.AbsolutePath.Contains("chat/completions") == true)
        {
            var originalStream = await response.Content.ReadAsStreamAsync(ct);
            var wrapped = new FoundryLocalSSEStream(originalStream, _qwenToolCalls) { OnThinkContent = OnThinkContent };
            var replacement = new StreamContent(wrapped);

            foreach (var header in response.Content.Headers)
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);

            response.Content = replacement;
        }

        return response;
    }
}

public class FoundryLocalSSEStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _qwenToolCalls;

    public Action<string>? OnThinkContent { get; set; }

    // ── Raw SSE line assembly ────────────────────────────────────────────────
    private readonly StringBuilder _lineBuffer = new();

    // ── <think> state ────────────────────────────────────────────────────────
    private readonly StringBuilder _thinkBuffer = new();
    private bool _inThinkBlock = true;
    private bool _thinkOpen    = true;

    // ── <tool_call> state ────────────────────────────────────────────────────
    private readonly StringBuilder _toolCallBuffer = new();
    private bool   _inToolCallBlock = false;
    private bool   _hadToolCalls    = false;
    private int    _toolCallIndex   = 0;
    private string _completionId    = "chatcmpl-foundry";

    private string? _deferredFinishReasonLine = null;

    // ── Processed-output queue ───────────────────────────────────────────────
    private readonly Queue<ArraySegment<byte>> _pending = new();
    private int _pendingOffset = 0;

    private readonly StreamWriter? _log;
    private const string LogPath = "foundry-sse-debug.log";

    public FoundryLocalSSEStream(Stream inner, bool qwenToolCalls = false)
    {
        _inner = inner;
        _qwenToolCalls = qwenToolCalls;
        try
        {
            _log = new StreamWriter(LogPath, append: true, Encoding.UTF8) { AutoFlush = true };
            _log.WriteLine($"\n=== FoundryLocal SSE session {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
                           $"(qwenToolCalls={qwenToolCalls}) ===");
        }
        catch { _log = null; }
    }

    // ── ReadAsync / Read ─────────────────────────────────────────────────────

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int drained = DrainPending(buffer);
        if (drained > 0) return drained;

        while (_pending.Count == 0)
        {
            byte[] temp = new byte[Math.Max(4096, buffer.Length)];
            int n = await _inner.ReadAsync(temp, ct);
            if (n == 0) return 0;
            InterceptChunk(temp.AsSpan(0, n));
        }

        return DrainPending(buffer);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int drained = DrainPending(buffer.AsMemory(offset, count));
        if (drained > 0) return drained;

        while (_pending.Count == 0)
        {
            byte[] temp = new byte[Math.Max(4096, count)];
            int n = _inner.Read(temp, 0, temp.Length);
            if (n == 0) return 0;
            InterceptChunk(temp.AsSpan(0, n));
        }

        return DrainPending(buffer.AsMemory(offset, count));
    }

    // ── Pending queue helpers ────────────────────────────────────────────────

    private int DrainPending(Memory<byte> buffer)
    {
        int written = 0;
        while (_pending.Count > 0 && written < buffer.Length)
        {
            var chunk = _pending.Peek();
            int available = chunk.Count - _pendingOffset;
            int toWrite = Math.Min(available, buffer.Length - written);
            chunk.AsMemory(_pendingOffset, toWrite).CopyTo(buffer[written..]);
            written += toWrite;
            _pendingOffset += toWrite;
            if (_pendingOffset >= chunk.Count) { _pending.Dequeue(); _pendingOffset = 0; }
        }
        return written;
    }

    private void Enqueue(string line) =>
        _pending.Enqueue(new ArraySegment<byte>(Encoding.UTF8.GetBytes(line + "\n")));

    // ── SSE line reconstruction ───────────────────────────────────────────────

    private void InterceptChunk(ReadOnlySpan<byte> bytes)
    {
        _lineBuffer.Append(Encoding.UTF8.GetString(bytes));
        int nl;
        while ((nl = _lineBuffer.ToString().IndexOf('\n')) >= 0)
        {
            var line = _lineBuffer.ToString(0, nl).TrimEnd('\r');
            _lineBuffer.Remove(0, nl + 1);
            _log?.WriteLine(string.IsNullOrEmpty(line) ? "<blank line>" : line);
            ProcessSSELine(line);
        }
    }

    // ── Per-line processing ───────────────────────────────────────────────────

    private void ProcessSSELine(string line)
    {
        if (!line.StartsWith("data: ")) { Enqueue(line); return; }

        var json = line["data: ".Length..];

        if (json == "[DONE]")
        {
            if (_inThinkBlock && _thinkBuffer.Length > 0)
            {
                _log?.WriteLine($"[DONE] no </think> seen — emitting {_thinkBuffer.Length} chars as content");
                var buffered = _thinkBuffer.ToString();
                _thinkBuffer.Clear();
                _inThinkBlock = false;
                _thinkOpen    = false;
                EmitContentSSE(buffered);
            }
            else
            {
                FlushThinkBlock();
            }

            if (_deferredFinishReasonLine != null)
            {
                Enqueue(_deferredFinishReasonLine);
                _deferredFinishReasonLine = null;
            }

            Enqueue(line);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (_completionId == "chatcmpl-foundry" &&
                doc.RootElement.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString();
                if (!string.IsNullOrEmpty(id)) _completionId = id;
            }

            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) { Enqueue(line); return; }

            var choice = choices[0];

            if (_qwenToolCalls && _hadToolCalls &&
                choice.TryGetProperty("finish_reason", out var fr) &&
                fr.GetString() == "stop")
            {
                var rewritten = RewriteNode(json, root =>
                {
                    if (root?["choices"]?[0] is JsonObject c)
                        c["finish_reason"] = "tool_calls";
                });
                _log?.WriteLine("[finish_reason] stop → tool_calls");
                Enqueue(rewritten != null ? "data: " + rewritten : line);
                return;
            }

            if (_inThinkBlock &&
                choice.TryGetProperty("finish_reason", out var finishReasonProp) &&
                finishReasonProp.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(finishReasonProp.GetString()))
            {
                _log?.WriteLine($"[defer] finish_reason={finishReasonProp.GetString()} held until after content emit");
                _deferredFinishReasonLine = line;
                return;
            }

            var delta = choice.GetProperty("delta");
            if (!delta.TryGetProperty("content", out var contentProp))
            {
                Enqueue(line);
                return;
            }

            var raw      = contentProp.GetString() ?? "";
            var filtered = FilterContent(raw);

            _log?.WriteLine($"[filter] raw={raw.Length}ch filtered={filtered.Length}ch " +
                            $"inThink={_inThinkBlock} inTool={_inToolCallBlock}");

            if (filtered == raw)
            {
                Enqueue(line);
            }
            else if (!string.IsNullOrEmpty(filtered))
            {
                var rewritten = RewriteNode(json, root =>
                {
                    if (root?["choices"]?[0]?["delta"] is JsonObject d)
                        d["content"] = filtered;
                });
                Enqueue(rewritten != null ? "data: " + rewritten : line);
            }
        }
        catch (JsonException ex) { _log?.WriteLine($"[json error] {ex.Message}"); Enqueue(line); }
        catch (Exception ex)     { _log?.WriteLine($"[error] {ex.GetType().Name}: {ex.Message}"); Enqueue(line); }
    }

    // ── Content filtering ────────────────────────────────────────────────────

    private string FilterContent(string content)
    {
        if (_inThinkBlock)    return FilterInsideThink(content);
        if (_inToolCallBlock) return FilterInsideToolCall(content);

        content = Regex.Replace(content, @"</think>", "", RegexOptions.IgnoreCase);

        int thinkAt = content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        int toolAt  = _qwenToolCalls
            ? content.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase)
            : -1;

        if (thinkAt < 0 && toolAt < 0) return content;

        if (toolAt >= 0 && (thinkAt < 0 || toolAt < thinkAt))
        {
            string before = content[..toolAt];
            _inToolCallBlock = true;
            return before + FilterInsideToolCall(content[(toolAt + 11)..]);
        }
        else
        {
            string before = content[..thinkAt];
            _inThinkBlock = true;
            _thinkOpen    = true;
            return before + FilterInsideThink(content[(thinkAt + 7)..]);
        }
    }

    private string FilterInsideThink(string content)
    {
        int nestedOpen = content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        int end        = content.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);

        if (nestedOpen >= 0 && (end < 0 || nestedOpen < end))
        {
            _thinkBuffer.Append(content[..nestedOpen]);
            return FilterInsideThink(content[(nestedOpen + 7)..]);
        }

        if (end < 0) { _thinkBuffer.Append(content); return ""; }

        _thinkBuffer.Append(content[..end]);
        _inThinkBlock = false;
        FlushThinkBlock();
        return FilterContent(content[(end + 8)..]);
    }

    private string FilterInsideToolCall(string content)
    {
        int end = content.IndexOf("</tool_call>", StringComparison.OrdinalIgnoreCase);
        if (end < 0) { _toolCallBuffer.Append(content); return ""; }

        _toolCallBuffer.Append(content[..end]);
        _inToolCallBlock = false;
        EmitParsedToolCall();
        return FilterContent(content[(end + 12)..]);
    }

    // ── Tool-call parsing ────────────────────────────────────────────────────

    private void EmitParsedToolCall()
    {
        var raw = _toolCallBuffer.ToString().Trim();
        _toolCallBuffer.Clear();

        _log?.WriteLine($"[tool_call raw] {raw.Replace("\n", "\\n")}");

        var (name, arguments) = ParseToolCallText(raw);
        if (string.IsNullOrEmpty(name)) { _log?.WriteLine("[tool_call] no name — skipping"); return; }

        _log?.WriteLine($"[tool_call] name={name} arguments={arguments}");

        var callId = "call_" + Guid.NewGuid().ToString("N")[..15];

        var functionNode = new JsonObject { ["name"] = name, ["arguments"] = arguments };
        var toolCallNode = new JsonObject
        {
            ["index"] = _toolCallIndex,
            ["id"]    = callId,
            ["type"]  = "function",
            ["function"] = functionNode
        };
        var root = new JsonObject
        {
            ["id"]      = _completionId,
            ["object"]  = "chat.completion.chunk",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray { toolCallNode } }
                }
            }
        };

        Enqueue("data: " + root.ToJsonString());
        _log?.WriteLine($"[emit] tool_call delta: {name}");

        _hadToolCalls = true;
        _toolCallIndex++;
    }

    private static (string name, string arguments) ParseToolCallText(string raw)
    {
        raw = raw.Trim();

        if (raw.StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var name = doc.RootElement.GetProperty("name").GetString() ?? "";
                var arguments = doc.RootElement.TryGetProperty("arguments", out var args)
                    ? (args.ValueKind == JsonValueKind.String ? args.GetString()! : args.GetRawText())
                    : "{}";
                return (name, arguments);
            }
            catch { }
        }

        var m = Regex.Match(raw, @"<function[=]?([^>]+)>(.*?)</function>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var name     = m.Groups[1].Value.Trim();
            var argsText = m.Groups[2].Value.Trim();
            if (string.IsNullOrEmpty(argsText)) return (name, "{}");
            return (name, XmlParamsToJson(argsText));
        }

        return ("", "{}");
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

    // ── Synthetic SSE emission ────────────────────────────────────────────────

    private void EmitContentSSE(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var root = new JsonObject
        {
            ["id"]      = _completionId,
            ["object"]  = "chat.completion.chunk",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject { ["content"] = text }
                }
            }
        };
        Enqueue("data: " + root.ToJsonString());
    }

    // ── Think block callback ─────────────────────────────────────────────────

    private void FlushThinkBlock()
    {
        if (!_thinkOpen) return;

        _thinkOpen = false;
        var reasoning = _thinkBuffer.ToString().TrimEnd();
        _thinkBuffer.Clear();

        if (!string.IsNullOrWhiteSpace(reasoning))
            OnThinkContent?.Invoke(reasoning);
    }

    // ── JSON rewriting helper ─────────────────────────────────────────────────

    private static string? RewriteNode(string json, Action<JsonNode?> mutate)
    {
        try { var root = JsonNode.Parse(json); mutate(root); return root?.ToJsonString(); }
        catch { return null; }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing) _log?.Dispose();
        base.Dispose(disposing);
    }

    // ── Stream boilerplate ────────────────────────────────────────────────────

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value)                => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
