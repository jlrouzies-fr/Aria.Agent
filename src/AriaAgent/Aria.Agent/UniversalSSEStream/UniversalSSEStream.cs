using System.Text;
using System.Text.Json;

namespace Aria.Agent;

public partial class UniversalSSEStream : Stream
{
    private readonly Stream _inner;
    public Action<string>? OnReasoningContent { get; set; }
    public bool StreamThinkingLive { get; set; } = false;
    private bool _thinkLiveStreamed = false;  // did we already stream the current think block live?

    /// <summary>
    /// When set to <see cref="ToolCallFormat.Functionary"/>, the model emits delimiter-less
    /// <c>name\n{args}</c> tool calls. Because there is no marker to auto-detect, this is only ever
    /// set by an explicit human format override; <see cref="KnownToolNames"/> supplies the tool-name
    /// set the leading content is matched against. See UniversalSSEStream.Functionary.
    /// </summary>
    public ToolCallFormat ForcedToolFormat { get; set; } = ToolCallFormat.Unknown;
    public IReadOnlySet<string>? KnownToolNames { get; set; }

    /// <summary>
    /// True when a StartsInThinkMode stream ended with the model still inside a think block
    /// (no closing tag and finish_reason=stop). The buffered thinking was internal monologue,
    /// not a real answer; it has been discarded rather than emitted as content so it cannot
    /// poison the assistant's history.
    /// </summary>
    public bool EndedWithUnresolvedThinking { get; private set; }
    private readonly Action<bool>? _onEndedWithUnresolvedThinking;

    // ── SSE line assembly ─────────────────────────────────────────────────────
    private readonly StringBuilder _lineBuffer = new();

    private readonly Queue<ArraySegment<byte>> _pending = new();
    private int _pendingOffset = 0;

    // ── Debug log ─────────────────────────────────────────────────────────────
    private readonly StreamWriter? _log;
    private const string LogPath = "DebugLogs/universal-sse-debug.log";

    public UniversalSSEStream(Stream inner, bool startsInThinkMode = false, Action<bool>? onEndedWithUnresolvedThinking = null)
    {
        _inner                          = inner;
        _startsInThinkMode              = startsInThinkMode;
        _inThink                        = startsInThinkMode;
        _onEndedWithUnresolvedThinking  = onEndedWithUnresolvedThinking;
        try
        {
            _log = new StreamWriter(LogPath, append: true, Encoding.UTF8) { AutoFlush = true };
            _log.WriteLine($"\n=== UniversalSSEStream {DateTime.Now:yyyy-MM-dd HH:mm:ss} startsInThink={startsInThinkMode} ===");
        }
        catch { _log = null; }
    }

    // ── ReadAsync / Read ──────────────────────────────────────────────────────

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int d = DrainPending(buffer);
        if (d > 0) return d;
        while (_pending.Count == 0)
        {
            byte[] tmp = new byte[Math.Max(4096, buffer.Length)];
            int n = await _inner.ReadAsync(tmp, ct);
            if (n == 0) return 0;
            InterceptChunk(tmp.AsSpan(0, n));
        }
        return DrainPending(buffer);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int d = DrainPending(buffer.AsMemory(offset, count));
        if (d > 0) return d;
        while (_pending.Count == 0)
        {
            byte[] tmp = new byte[Math.Max(4096, count)];
            int n = _inner.Read(tmp, 0, tmp.Length);
            if (n == 0) return 0;
            InterceptChunk(tmp.AsSpan(0, n));
        }
        return DrainPending(buffer.AsMemory(offset, count));
    }
    // ── Queue helpers ─────────────────────────────────────────────────────────

    private int DrainPending(Memory<byte> buf)
    {
        int w = 0;
        while (_pending.Count > 0 && w < buf.Length)
        {
            var ch  = _pending.Peek();
            int av  = ch.Count - _pendingOffset;
            int wt  = Math.Min(av, buf.Length - w);
            ch.AsMemory(_pendingOffset, wt).CopyTo(buf[w..]);
            w += wt; _pendingOffset += wt;
            if (_pendingOffset >= ch.Count) { _pending.Dequeue(); _pendingOffset = 0; }
        }
        return w;
    }
    private void Enqueue(string text) =>
        _pending.Enqueue(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)));

    // ── SSE line assembly ─────────────────────────────────────────────────────

    private void InterceptChunk(ReadOnlySpan<byte> bytes)
    {
        _lineBuffer.Append(Encoding.UTF8.GetString(bytes));
        int nl;
        while ((nl = _lineBuffer.ToString().IndexOf('\n')) >= 0)
        {
            var line = _lineBuffer.ToString(0, nl).TrimEnd('\r');
            _lineBuffer.Remove(0, nl + 1);
            _log?.WriteLine(string.IsNullOrEmpty(line) ? "<blank>" : line);
            ProcessSSELine(line);
        }
    }
    // ── Per-line logic ────────────────────────────────────────────────────────

    private void ProcessSSELine(string line)
    {
        if (!line.StartsWith("data: ")) { Enqueue(line + "\n"); return; }

        var json = line["data: ".Length..];

        if (json == "[DONE]")
        {
            FlushReasoningBuf();

            // StartsInThinkMode: if </think> never arrived, emit as regular content —
            // BUT if the deferred finish_reason is tool_calls the think buffer is internal
            // monologue; emitting it as content creates an invalid assistant message
            // (content + tool_calls) that downstream models reject on the next request.
            if (_startsInThinkMode && _inThink && !_thinkEverClosed && _thinkBuf.Length > 0)
            {
                // Must parse the JSON to get the real finish_reason — FoundryLocal always
                // includes "tool_calls":[] in every delta, so string-matching "tool_calls"
                // would incorrectly match stop-finish chunks too.
                var isToolCallFinish = false;
                if (_deferredFinishReasonLine != null)
                {
                    try
                    {
                        var djson = _deferredFinishReasonLine.StartsWith("data: ")
                            ? _deferredFinishReasonLine["data: ".Length..]
                            : _deferredFinishReasonLine;
                        using var d = JsonDocument.Parse(djson);
                        var dc = d.RootElement.GetProperty("choices");
                        if (dc.GetArrayLength() > 0 &&
                            dc[0].TryGetProperty("finish_reason", out var fr))
                            isToolCallFinish = fr.GetString() == "tool_calls";
                    }
                    catch { }
                }

                if (isToolCallFinish)
                {
                    _log?.WriteLine($"[DONE implicit think→DISCARD — tool_calls finish] {_thinkBuf.Length}ch");
                    _thinkBuf.Clear(); _inThink = false;
                }
                else
                {
                    // The model stopped while still in a think block. The buffered text is internal
                    // monologue, not a final answer. Emitting it as content pollutes the assistant
                    // history and trains later turns to reply in monologue. Keep it as thinking only
                    // (already streamed live if StreamThinkingLive is on) and flag the caller so it
                    // can ask for a proper answer.
                    _log?.WriteLine($"[DONE implicit think→DISCARD — unresolved stop] {_thinkBuf.Length}ch");
                    EndedWithUnresolvedThinking = true;
                    _onEndedWithUnresolvedThinking?.Invoke(true);
                    _thinkBuf.Clear(); _inThink = false;
                }
            }
            else { FlushThinkBuf(); }

            // Flush model-specific buffers
            if (_inMistralToolCalls && _mistralBuffer.Length > 0)
                FlushMistralBuffer();
            if (_inHarmony)
                FlushHarmonyChannel();

            // Functionary override: short prose that never reached a newline/'{' is still held in the
            // sniff buffer (it never matched a tool name). Surface it as content so the reply isn't lost.
            if (ForcedToolFormat == ToolCallFormat.Functionary && !_fnInArgs && !_fnPlain && _fnSniff.Length > 0)
            {
                var pending = FlushFunctionaryPlain();
                if (pending.Length > 0)
                {
                    var escaped = JsonSerializer.Serialize(pending);
                    _log?.WriteLine($"[Functionary] flushing {pending.Length}ch prose at DONE");
                    Enqueue($"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":{escaped}}},\"finish_reason\":null}}]}}\n\n");
                }
            }

            // Pure-reasoning-content model (LM Studio routing all output into reasoning_content,
            // never emitting a content field). Re-emit what we mirrored as actual content so the
            // chat reply isn't blank. The thinking panel already showed it live; this just ensures
            // the message bubble has something too. Skip entirely if a tool call happened — native
            // or client-parsed — since then the missing "content" just means the model went
            // straight into a tool call (content arrives later, after the tool result), not that
            // it's a reasoning-only model; re-emitting here would just duplicate the thinking text.
            if (!_contentFieldEverSeen && !_hadToolCalls && !_sawNativeToolCalls && _reasoningMirrorBuf.Length > 0)
            {
                var text    = _reasoningMirrorBuf.ToString();
                var escaped = JsonSerializer.Serialize(text);
                _log?.WriteLine($"[DONE pure-reasoning-model] re-emitting {text.Length}ch as content");
                Enqueue($"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":{escaped}}},\"finish_reason\":null}}]}}\n\n");
            }
            _reasoningMirrorBuf.Clear();

            if (_deferredFinishReasonLine != null)
            {
                Enqueue(_deferredFinishReasonLine + "\n\n");
                _deferredFinishReasonLine = null;
            }

            Enqueue(line + "\n");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);

            // Capture completion ID
            if (_completionId == "chatcmpl-universal" &&
                doc.RootElement.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString();
                if (!string.IsNullOrEmpty(id)) _completionId = id;
            }

            if (!doc.RootElement.TryGetProperty("choices", out var choices))
            {
                // Some SSE lines may not have choices (e.g., system messages)
                // Pass them through unchanged
                Enqueue(line + "\n");
                return;
            }
            if (choices.GetArrayLength() == 0) { Enqueue(line + "\n"); return; }

            var choice = choices[0];

            // Rewrite finish_reason stop → tool_calls when we emitted XML tool calls
            if (_hadToolCalls &&
                choice.TryGetProperty("finish_reason", out var fr) &&
                fr.GetString() == "stop")
            {
                var rw = RewriteFinishReason(json, "tool_calls");
                _log?.WriteLine("[finish_reason] stop → tool_calls");
                Enqueue((rw != null ? "data: " + rw : line) + "\n");
                return;
            }

            // Defer finish_reason while inside a think block
            if (_inThink &&
                choice.TryGetProperty("finish_reason", out var fp) &&
                fp.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(fp.GetString()))
            {
                _log?.WriteLine($"[defer finish_reason] {fp.GetString()}");
                _deferredFinishReasonLine = line;
                return;
            }

            if (!choice.TryGetProperty("delta", out var delta))
            {
                // Some SSE lines may not have delta (e.g., finish_reason only)
                // Pass them through unchanged
                Enqueue(line + "\n");
                return;
            }

            // Format A: reasoning_content (unambiguous — always reasoning, never the answer).
            // "reasoning" is the same shape under a different key — LM Studio uses it for GPT-OSS,
            // having already parsed the Harmony <|channel|> envelope server-side (those raw tokens
            // never reach us, so the literal <|channel|> sniffing in FilterContent never fires for it).
            bool hasReasoning = delta.TryGetProperty("reasoning_content", out var rc) || delta.TryGetProperty("reasoning", out rc);
            if (hasReasoning)
            {
                var ch = rc.GetString();
                if (!string.IsNullOrEmpty(ch))
                {
                    _inReasoning = true;
                    _reasoningMirrorBuf.Append(ch);
                    if (StreamThinkingLive)
                    {
                        // Confirmed thinking model → stream each reasoning delta to the UI live.
                        _reasoningLiveStreamed = true;
                        OnReasoningContent?.Invoke(ch);
                    }
                    else
                    {
                        _reasoningBuf.Append(ch);
                    }
                }
            }

            bool hasContent   = delta.TryGetProperty("content",    out var cp);
            bool hasToolCalls = delta.TryGetProperty("tool_calls", out _);
            if (hasToolCalls) _sawNativeToolCalls = true;

            if (_inReasoning && (hasContent || hasToolCalls))
                FlushReasoningBuf();

            // Format B/C: think tags + tool call tags in content
            if (hasContent)
            {
                _contentFieldEverSeen = true;
                var raw = cp.GetString() ?? "";

                // Re-attach any partial open-tag tail held from the previous chunk, then check
                // whether the combined content itself ends with a new partial prefix. This handles
                // think tags like <|channel>thought that arrive split across SSE chunks.
                if (_partialOpenTagTail.Length > 0) { raw = _partialOpenTagTail + raw; _partialOpenTagTail = ""; }
                if (!_inThink && !_inToolCall && !_inMistralToolCalls)
                    (raw, _partialOpenTagTail) = StripPartialOpenTagTail(raw);
                if (raw.Length == 0) return; // entire chunk was a partial open-tag prefix — wait for more

                var filtered = FilterContent(raw);
                bool changed = filtered != raw;
                _log?.WriteLine($"[content] raw={Truncate(raw)} filtered={Truncate(filtered)} inThink={_inThink} inTool={_inToolCall} changed={changed}");

                if (!changed)
                    Enqueue(line + "\n");
                else if (!string.IsNullOrEmpty(filtered))
                    Enqueue("data: " + RewriteContent(json, filtered) + "\n");
                // empty → whole chunk inside think/tool block → drop
            }
            else
            {
                // Drop lines that only carry reasoning_content (we already extracted it);
                // pass through everything else (native tool_calls, finish_reason, etc.).
                if (!hasReasoning || hasToolCalls)
                    Enqueue(line + "\n");
            }
        }
        catch (JsonException ex)
        {
            _log?.WriteLine($"[json error] {ex.Message}");
            Enqueue(line + "\n");
        }
    }
    // ── Dispose ───────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_inHarmony)
                FlushHarmonyChannel();
            _log?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ── Stream boilerplate ────────────────────────────────────────────────────

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush()                                      => _inner.Flush();
    public override long Seek(long o, SeekOrigin or)                 => throw new NotSupportedException();
    public override void SetLength(long v)                           => throw new NotSupportedException();
    public override void Write(byte[] b, int off, int cnt)           => throw new NotSupportedException();
}
