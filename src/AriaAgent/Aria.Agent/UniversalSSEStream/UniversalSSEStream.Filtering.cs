using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aria.Agent;

public partial class UniversalSSEStream : Stream
{
    // ── Content tag filtering ─────────────────────────────────────────────────

    private string FilterContent(string content)
    {
        // Human-forced Functionary override: delimiter-less name\n{args}. Handled entirely by its own
        // sniff/parse state (see UniversalSSEStream.Functionary) so it never interferes with the
        // marker-based auto-detection below. Functionary v3.x is not a reasoning model, so there is no
        // think/harmony block to compete with.
        if (ForcedToolFormat == ToolCallFormat.Functionary
            && !_inThink && !_inHarmony && !_inToolCall && !_inMistralToolCalls)
            return FilterFunctionaryContent(content);

        if (_inHarmony || content.Contains("<|channel|>", StringComparison.OrdinalIgnoreCase))
            return FilterHarmonyContent(content);

        if (_inThink)
        {
            // Re-attach any partial close-tag tail held from the previous chunk.
            if (_partialCloseTagTail.Length > 0) { content = _partialCloseTagTail + content; _partialCloseTagTail = ""; }
            return FilterInsideThink(content);
        }
        if (_inToolCall) return FilterInsideToolCall(content);
        if (_inMistralToolCalls) { _mistralBuffer.Append(content); return ""; }

        // Dynamic StartsInThinkMode detection:
        // If </think> appears before any <think> and we have never been in think mode,
        // the model started emitting thinking tokens without an opening tag.
        // Prior chunks already leaked as content (unavoidable without full buffering),
        // but at least capture anything in THIS chunk that precedes </think>.
        if (!_thinkEverClosed)
        {
            int closeIdx = content.IndexOf("</think>",  StringComparison.OrdinalIgnoreCase);
            int openIdx  = content.IndexOf("<think>",   StringComparison.OrdinalIgnoreCase);
            if (closeIdx >= 0 && (openIdx < 0 || closeIdx < openIdx))
            {
                var thinkPart = content[..closeIdx];
                if (thinkPart.Length > 0) _thinkBuf.Append(thinkPart);
                _thinkEverClosed = true;
                _log?.WriteLine($"[dynamic </think>] retroactive flush, {_thinkBuf.Length}ch captured from this chunk");
                FlushThinkBuf();
                return FilterContent(content[(closeIdx + 8)..]);
            }
        }

        // Only strip orphaned </think> tags — stripping unconditionally breaks valid
        // <think>...</think> blocks that arrive in a single content chunk.
        int thinkOpenIdx  = content.IndexOf("<think>",  StringComparison.OrdinalIgnoreCase);
        int thinkCloseIdx = content.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        bool hasOrphanedCloseThink = thinkCloseIdx >= 0 && (thinkOpenIdx < 0 || thinkCloseIdx < thinkOpenIdx);

        var cleaned = content;
        if (hasOrphanedCloseThink)
            cleaned = Regex.Replace(cleaned, @"</think>", "", RegexOptions.IgnoreCase);

        // Check Mistral [TOOL_CALLS] prefix first
        var trimmed = cleaned.TrimStart();
        if (trimmed.StartsWith("[TOOL_CALLS]", StringComparison.OrdinalIgnoreCase))
        {
            _log?.WriteLine("[Mistral] [TOOL_CALLS] detected");
            _inMistralToolCalls = true;
            var payload = trimmed["[TOOL_CALLS]".Length..].TrimStart();
            _mistralBuffer.Append(payload);
            return cleaned[..(cleaned.Length - trimmed.Length)]; // leading whitespace only
        }

        // GLM: detect <arg_key> without a wrapper
        int glmAt = cleaned.IndexOf("<arg_key>", StringComparison.OrdinalIgnoreCase);
        if (glmAt >= 0 && !_hadToolCalls && _glmToolName == "")
        {
            // GLM emits the function name before the args — try to capture it
            _log?.WriteLine("[GLM] <arg_key> detected — switching to GLM arg parsing");
            _activeToolFmt = ToolCallFormat.GlmXml;
            // Fall through to GLM processing below
            return FilterGlmContent(cleaned);
        }

        // Find earliest think-open tag (either <think> or <|channel>thought)
        int thinkAt = -1;
        string thinkOpen = "<think>", thinkClose = "</think>";
        int tAt  = cleaned.IndexOf("<think>",          StringComparison.OrdinalIgnoreCase);
        int chAt = cleaned.IndexOf("<|channel>thought", StringComparison.OrdinalIgnoreCase);
        if (tAt >= 0 && (chAt < 0 || tAt <= chAt)) { thinkAt = tAt;  thinkOpen = "<think>";          thinkClose = "</think>"; }
        else if (chAt >= 0)                          { thinkAt = chAt; thinkOpen = "<|channel>thought"; thinkClose = "<channel|>"; }

        // Find earliest tool-call pattern
        ToolPattern? bestPat = null;
        int bestIdx = int.MaxValue;
        foreach (var pat in ToolPatterns)
        {
            int idx = cleaned.IndexOf(pat.Open, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < bestIdx) { bestIdx = idx; bestPat = pat; }
        }

        bool hasTool  = bestPat != null;
        bool hasThink = thinkAt >= 0;

        if (!hasTool && !hasThink) return cleaned;

        bool toolFirst = hasTool && (!hasThink || bestIdx <= thinkAt);

        if (toolFirst)
        {
            var before = cleaned[..bestIdx];
            _inToolCall      = true;
            _activeToolClose = bestPat!.Close;
            _activeToolFmt   = bestPat.Fmt;
            _log?.WriteLine($"[{bestPat.Open}] detected at {bestIdx} fmt={bestPat.Fmt}");
            return before + FilterInsideToolCall(cleaned[(bestIdx + bestPat.Open.Length)..]);
        }
        else
        {
            var before = cleaned[..thinkAt];
            _inThink         = true;
            _activeThinkOpen  = thinkOpen;
            _activeThinkClose = thinkClose;
            _log?.WriteLine($"[{thinkOpen}] detected at {thinkAt}");
            return before + FilterInsideThink(cleaned[(thinkAt + thinkOpen.Length)..]);
        }
    }
}
