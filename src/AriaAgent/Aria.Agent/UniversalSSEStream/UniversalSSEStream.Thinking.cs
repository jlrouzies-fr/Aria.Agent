using System.Text;
using System.Text.Json;

namespace Aria.Agent;

public partial class UniversalSSEStream : Stream
{
    // ── reasoning_content mode (OpenAI / DeepSeek style) ─────────────────────
    private readonly StringBuilder _reasoningBuf = new();
    private bool _inReasoning = false;
    private bool _reasoningLiveStreamed = false;
    // Full mirror of all reasoning_content seen this stream (regardless of live mode).
    // Used to re-emit as content when the model never transitions to a content field.
    private readonly StringBuilder _reasoningMirrorBuf = new();
    private bool _contentFieldEverSeen = false;

    // ── <think>…</think> / <|channel>thought…<channel|> mode ────────────────
    private readonly StringBuilder _thinkBuf = new();
    private bool _inThink = false;
    private readonly bool _startsInThinkMode;
    private bool _thinkEverClosed = false;
    // Active think-tag pair — set when an open tag is detected so FilterInsideThink
    // knows which closing tag to look for and how long it is.
    private string _activeThinkOpen  = "<think>";
    private string _activeThinkClose = "</think>";
    // Partial-tag tail buffers: some models emit tags split across SSE chunks
    // (e.g. Gemma 12b sends "<|channel>" in one chunk and "thought" in the next;
    // token-per-delta servers split "<tool_call>" mid-tag the same way).
    // We hold back any trailing partial prefix and prepend it to the following chunk.
    private string _partialOpenTagTail  = "";
    private string _partialCloseTagTail = "";
    // Open tags that may arrive split: think openers plus every tool-call opener,
    // so a split tool-call tag never leaks to the user as raw markup.
    // Lazy: ToolPatterns lives in another partial-class file, so static field
    // initializer order is not guaranteed — defer the read until first use.
    private static string[]? _splitableOpenTags;
    private static string[] SplitableOpenTags =>
        _splitableOpenTags ??= ["<|channel>thought", .. ToolPatterns.Select(p => p.Open)];
    // ── Think filtering ───────────────────────────────────────────────────────

    private string FilterInsideThink(string content)
    {
        int nested = content.IndexOf(_activeThinkOpen,  StringComparison.OrdinalIgnoreCase);
        int end    = content.IndexOf(_activeThinkClose, StringComparison.OrdinalIgnoreCase);

        if (nested >= 0 && (end < 0 || nested < end))
        {
            AppendThink(content[..nested]);
            return FilterInsideThink(content[(nested + _activeThinkOpen.Length)..]);
        }
        if (end < 0)
        {
            // No close tag yet — check whether we're sitting on a partial close-tag prefix.
            var (toAppend, closeTail) = StripPartialCloseTagTail(content, _activeThinkClose);
            AppendThink(toAppend);
            _partialCloseTagTail = closeTail;
            return "";
        }

        AppendThink(content[..end]);
        _log?.WriteLine($"[{_activeThinkClose}] flushing {_thinkBuf.Length}ch");
        _inThink = false; _thinkEverClosed = true;
        FlushThinkBuf();
        return FilterContent(content[(end + _activeThinkClose.Length)..]);
    }
    // Check whether content ends with a partial prefix of any known splitable open tag
    // (think or tool-call). Returns (contentWithoutTail, tail) so the tail can be
    // prepended to the next chunk.
    private static (string main, string tail) StripPartialOpenTagTail(string content)
    {
        foreach (var openTag in SplitableOpenTags)
        {
            for (int len = Math.Min(openTag.Length - 1, content.Length); len >= 1; len--)
            {
                if (content.EndsWith(openTag[..len], StringComparison.OrdinalIgnoreCase))
                    return (content[..^len], content[^len..]);
            }
        }
        return (content, "");
    }
    private static (string main, string tail) StripPartialCloseTagTail(string content, string closeTag)
    {
        for (int len = Math.Min(closeTag.Length - 1, content.Length); len >= 1; len--)
        {
            if (content.EndsWith(closeTag[..len], StringComparison.OrdinalIgnoreCase))
                return (content[..^len], content[^len..]);
        }
        return (content, "");
    }
    // Accumulate thinking into the buffer, and — for a confirmed thinking format — also emit it live
    // so the UI streams reasoning token-by-token instead of all at once at </think>.
    private void AppendThink(string text)
    {
        if (text.Length == 0) return;
        _thinkBuf.Append(text);
        if (StreamThinkingLive)
        {
            _thinkLiveStreamed = true;
            OnReasoningContent?.Invoke(text);
        }
    }
    private void FlushReasoningBuf()
    {
        if (!_inReasoning) return;
        _inReasoning = false;

        // Live mode: deltas were already emitted incrementally — just reset, don't re-emit.
        if (_reasoningLiveStreamed)
        {
            _reasoningBuf.Clear(); _reasoningLiveStreamed = false;
            return;
        }

        var text = _reasoningBuf.ToString().TrimEnd();
        _reasoningBuf.Clear();
        if (!string.IsNullOrWhiteSpace(text))
        {
            _log?.WriteLine($"[FIRE OnReasoningContent] reasoning_content path, {text.Length}ch");
            OnReasoningContent?.Invoke(text);
        }
    }
    private void FlushThinkBuf()
    {
        if (_thinkBuf.Length == 0 && !_inThink) return;

        // Live mode: the deltas were already emitted incrementally — just reset, don't re-emit.
        if (_thinkLiveStreamed)
        {
            _thinkBuf.Clear(); _inThink = false; _thinkLiveStreamed = false;
            return;
        }

        var text = _thinkBuf.ToString().TrimEnd();
        _thinkBuf.Clear(); _inThink = false;
        if (!string.IsNullOrWhiteSpace(text))
        {
            _log?.WriteLine($"[FIRE OnReasoningContent] think-tag path, {text.Length}ch");
            OnReasoningContent?.Invoke(text);
        }
    }
}
