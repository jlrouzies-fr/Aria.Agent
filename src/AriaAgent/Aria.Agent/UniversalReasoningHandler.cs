using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aria.Agent;

/// <summary>
/// Tool-call format emitted by different model families when the SDK doesn't handle it natively.
/// </summary>
public enum ToolCallFormat
{
    Unknown,
    None,              // model emits native OpenAI tool_calls deltas — no rewriting needed
    ToolCallTag,       // <tool_call>…</tool_call>  (Qwen, DeepSeek, Llama, etc.)
    StartFunctionCall, // <start_function_call>…<end_function_call>  (Gemma 3)
    MistralToolCalls,  // [TOOL_CALLS] [{…}]  (Mistral)
    MinimaxToolCall,   // <minimax:tool_call>…</minimax:tool_call>
    KimiK2,            // <|tool_calls_section_begin|>…<|tool_calls_section_end|>
    Longcat,           // <longcat_tool_call>…</longcat_tool_call>
    GlmXml,            // <arg_key>…</arg_key><arg_value>…</arg_value>  (GLM 4.7 / 5)
    Gemma4ToolCall,    // <|tool_call>call:server:name{args}<tool_call|>  (Gemma 4)
    Harmony,           // <|channel|>analysis/commentary/final          (OpenAI GPT-OSS)
    Functionary,       // bare  functionname\n{args}  — no delimiter (Functionary v3.x via LM Studio).
                       // Delimiter-less, so it is NEVER auto-detected: only applied when a human
                       // explicitly picks it in the format-override modal. Parsed by matching the
                       // leading content against the live tool-name set (see UniversalSSEStream.Functionary).
}

/// <summary>
/// Single SSE interceptor that auto-detects reasoning/thinking content and tool-call format
/// regardless of which model family is being used.
///
/// Thinking paths:
///   • reasoning_content field (OpenAI o-series, DeepSeek, some Qwen)
///   • &lt;think&gt;…&lt;/think&gt; tags in content (Gemma, Qwen via LM Studio/Foundry)
///   • StartsInThinkMode: first tokens are thinking without an opening &lt;think&gt; tag
///   • Harmony &lt;|channel|&gt;analysis…&lt;|channel|&gt;final (OpenAI GPT-OSS via LM Studio)
///
/// Tool-call paths (all auto-detected from stream content):
///   • &lt;tool_call&gt; JSON or &lt;function=…&gt; XML  (Qwen, DeepSeek, Llama)
///   • &lt;start_function_call&gt; / &lt;end_function_call&gt;  (Gemma 3)
///   • &lt;|tool_call&gt;call:server:name{args}&lt;tool_call|&gt;  (Gemma 4)
///   • &lt;minimax:tool_call&gt; … &lt;/minimax:tool_call&gt;
///   • &lt;longcat_tool_call&gt; … &lt;/longcat_tool_call&gt;
///   • &lt;|tool_calls_section_begin|&gt; … &lt;|tool_calls_section_end|&gt;  (Kimi K2)
///   • [TOOL_CALLS] [{…}]  (Mistral)
///   • &lt;arg_key&gt;…&lt;/arg_key&gt;&lt;arg_value&gt;…&lt;/arg_value&gt; key-value XML  (GLM)
///   • &lt;|channel|&gt;analysis / commentary / final  (OpenAI GPT-OSS)
///
/// All tool-call paths rewrite to proper OpenAI tool_calls deltas.
/// </summary>
public class UniversalReasoningHandler : DelegatingHandler
{
    public Action<string>? OnReasoningContent { get; set; }

    /// <summary>
    /// True for models (e.g. Qwen3) that start emitting thinking tokens immediately
    /// without an opening &lt;think&gt; tag — only &lt;/think&gt; marks the end.
    /// </summary>
    public bool StartsInThinkMode { get; set; } = false;

    /// <summary>
    /// When true, thinking tokens are emitted to <see cref="OnReasoningContent"/> incrementally as
    /// they arrive (live streaming) rather than buffered and flushed once at &lt;/think&gt;. Only
    /// safe for a *confirmed* thinking format (ThinkTags / StartsInThinkMode), because the buffer is
    /// also the fallback that reclassifies text as the answer when &lt;/think&gt; never arrives.
    /// </summary>
    public bool StreamThinkingLive { get; set; } = false;

    /// <summary>
    /// A human-forced tool-call format for models Aria can't auto-detect. Only
    /// <see cref="ToolCallFormat.Functionary"/> is meaningful here (delimiter-less name\n{args});
    /// every other format is auto-detected from stream markers and needs no hint. When set to
    /// Functionary, the outgoing request's tool names are handed to the stream so it can match them.
    /// </summary>
    public ToolCallFormat ForcedToolFormat { get; set; } = ToolCallFormat.Unknown;

    /// <summary>
    /// True when the most recent chat/completions stream ended while still inside a think block
    /// (StartsInThinkMode, no &lt;/think&gt;, finish_reason=stop). The caller can use this to
    /// re-prompt for a proper final answer instead of leaving the reply empty.
    /// </summary>
    public bool LastStreamHadUnresolvedThinking { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        // For the Functionary override we need the exact tool names this request offered the model,
        // to match the delimiter-less  name\n{args}  it streams back.
        IReadOnlySet<string>? functionaryToolNames = null;

        if (request.RequestUri?.AbsolutePath.Contains("chat/completions") == true &&
            request.Content != null)
        {
            try
            {
                var body = await request.Content.ReadAsStringAsync(ct);
                File.AppendAllText(ReqLogPath,
                    $"\n=== REQUEST {DateTime.Now:HH:mm:ss} ===\n{body}\n");
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                if (ForcedToolFormat == ToolCallFormat.Functionary)
                    functionaryToolNames = ExtractToolNames(body);
            }
            catch { /* don't let logging break the request */ }
        }

        LastStreamHadUnresolvedThinking = false;

        var response = await base.SendAsync(request, ct);

        if (request.RequestUri?.AbsolutePath.Contains("chat/completions") == true)
        {
            var original = await response.Content.ReadAsStreamAsync(ct);

            try
            {
                File.AppendAllText(ReqLogPath,
                    $"=== RESPONSE {DateTime.Now:HH:mm:ss} {(int)response.StatusCode} {response.StatusCode} ===\n");

                if (!response.IsSuccessStatusCode)
                {
                    using var sr = new StreamReader(original, Encoding.UTF8);
                    var body = await sr.ReadToEndAsync(ct);
                    File.AppendAllText(ReqLogPath, $"{body}\n");
                    response.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    return response;
                }
            }
            catch { /* don't let logging break the response */ }

            // The bridge tunnel relays upstream FAILURES as 200/text-event-stream too (the transport
            // can't change status mid-stream), so an auth rejection or error JSON would otherwise
            // parse as an empty SSE stream → silent empty reply. Peek the first bytes: a JSON error
            // object becomes a thrown exception carrying the upstream message, which the chat UI
            // renders as a visible fault instead of nothing.
            var headBuf = new byte[1024];
            var headLen = await original.ReadAsync(headBuf.AsMemory(0, headBuf.Length), ct);
            var headText = Encoding.UTF8.GetString(headBuf, 0, Math.Max(headLen, 0)).TrimStart();
            if (headText.StartsWith('{') && headText.Contains("\"error\"", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream();
                ms.Write(headBuf, 0, headLen);
                var drain = new byte[4096];
                while (ms.Length < 16384)
                {
                    var n = await original.ReadAsync(drain.AsMemory(0, drain.Length), ct);
                    if (n == 0) break;
                    ms.Write(drain, 0, n);
                }
                var errorBody = Encoding.UTF8.GetString(ms.ToArray());
                try { File.AppendAllText(ReqLogPath, $"UPSTREAM ERROR RELAYED: {errorBody}\n"); } catch { }
                throw new HttpRequestException(UpstreamErrorMessage(errorBody));
            }

            var wrapped = new UniversalSSEStream(new PrefixedStream(headBuf, headLen, original),
                StartsInThinkMode, ended => LastStreamHadUnresolvedThinking = ended)
            {
                OnReasoningContent = OnReasoningContent,
                StreamThinkingLive = StreamThinkingLive,
                ForcedToolFormat   = ForcedToolFormat,
                KnownToolNames     = functionaryToolNames
            };
            var replacement = new StreamContent(wrapped);
            foreach (var h in response.Content.Headers)
                replacement.Headers.TryAddWithoutValidation(h.Key, h.Value);
            response.Content = replacement;
        }

        return response;
    }

    private const string ReqLogPath = "DebugLogs/foundry-request.log";

    /// <summary>Pulls the <c>tools[].function.name</c> set out of an outgoing chat/completions body —
    /// the exact names the Functionary stream matches its bare <c>name\n{args}</c> output against.</summary>
    private static IReadOnlySet<string>? ExtractToolNames(string requestBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (!doc.RootElement.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
                return null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tools.EnumerateArray())
                if (t.TryGetProperty("function", out var fn) &&
                    fn.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                    names.Add(name);
            return names.Count > 0 ? names : null;
        }
        catch { return null; }
    }

    /// <summary>Extracts a human-readable message from an upstream error body (OpenAI-style
    /// <c>{"error":{"message":…}}</c>, plain <c>{"error":"…"}</c>, or raw text).</summary>
    private static string UpstreamErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var msg = err.ValueKind == JsonValueKind.String
                    ? err.GetString()
                    : err.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (!string.IsNullOrWhiteSpace(msg))
                    return $"The model endpoint rejected the request: {msg}";
            }
        }
        catch { /* not JSON — fall through to raw head */ }
        var head = body.Length > 300 ? body[..300] : body;
        return $"The model endpoint returned an error instead of a response: {head}";
    }

    /// <summary>Truncate the focused-debug logs. Called at server startup so each run starts fresh.</summary>
    public static void ClearDebugLogs()
    {
        Directory.CreateDirectory("DebugLogs");
        foreach (var path in new[] { ReqLogPath, "DebugLogs/universal-sse-debug.log" })
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
