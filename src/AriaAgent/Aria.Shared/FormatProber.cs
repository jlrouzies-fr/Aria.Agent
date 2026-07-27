using System.Text;
using System.Text.Json;

namespace Aria.Shared;

public static class FormatProber
{
    /// <summary>Loads/warms the model with one tiny non-streaming request so the real format probes
    /// don't race a cold just-in-time load (which makes concurrent probes falsely time out to
    /// "Unknown"). Best-effort: any failure is swallowed — the probes still run, just cold. The long
    /// timeout accommodates a genuinely slow first load of a large local model.</summary>
    public static async Task WarmupAsync(
        string endpointUrl, string model, string? apiKey = null, string? proxyUrl = null, string? keyRef = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        try
        {
            using var http = BuildHttp(string.IsNullOrEmpty(proxyUrl) ? apiKey : null);
            var body = JsonSerializer.Serialize(new
            {
                model,
                messages   = new[] { new { role = "user", content = "hi" } },
                max_tokens = 1,
                stream     = false
            });
            using var req  = BuildRequest(endpointUrl, body, apiKey, proxyUrl, keyRef);
            using var resp = await http.SendAsync(req, timeout.Token);
            // Drain so the model is fully resident before the probes fire.
            _ = await resp.Content.ReadAsStringAsync(timeout.Token);
        }
        catch { /* cold probe is still better than no probe */ }
    }

    public static async Task<string> ProbeThinkingAsync(
        string endpointUrl, string model, string? apiKey = null, string? proxyUrl = null, CancellationToken ct = default, string? keyRef = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        bool sawOpenThink = false, sawCloseThink = false, sawReasoning = false, sawChannelThought = false, sawHarmony = false;
        bool streamOpened = false;

        // The verdict must survive a timeout: long-reasoning models (GLM 4.7 flash) outrun the
        // budget mid-stream, but the format is usually decided by the very first deltas — so on
        // cancellation we return what was collected instead of discarding it as "Unknown".
        string Verdict()
        {
            if (sawReasoning)      return "ReasoningContent";
            if (sawHarmony)        return "Harmony";
            if (sawChannelThought) return "ChannelThought";
            if (sawOpenThink)      return "ThinkTags";
            if (sawCloseThink)     return "StartsInThinkMode";
            // A stream that opened but showed no thinking markers is a confident "None";
            // one that never opened says nothing about the model.
            return streamOpened ? "None" : "Unknown";
        }

        try
        {
            using var http = BuildHttp(string.IsNullOrEmpty(proxyUrl) ? apiKey : null);
            var body = JsonSerializer.Serialize(new
            {
                model,
                messages = new[] { new { role = "user", content = "Briefly explain the tradeoffs between quicksort and mergesort, and when you would choose one over the other." } },
                stream     = true,
                // Safety bound — reasoning models given no cap can generate for minutes; the
                // markers we need all appear early, so a capped stream still decides the format.
                max_tokens = 2048
            });

            using var req = BuildRequest(endpointUrl, body, apiKey, proxyUrl, keyRef);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            // "Unknown", not "None": a rejected/failed probe says nothing about the model's thinking
            // style, must never be cached, and must never feed a think-mode assumption downstream.
            if (!resp.IsSuccessStatusCode) return "Unknown";

            await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token);
            using var reader = new StreamReader(stream);
            streamOpened = true;

            string? line;
            while ((line = await reader.ReadLineAsync(linked.Token)) != null)
            {
                if (!line.StartsWith("data: ")) continue;
                var json = line["data: ".Length..];
                if (json == "[DONE]") break;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("choices", out var choices)) continue;
                    if (choices.GetArrayLength() == 0) continue;
                    if (!choices[0].TryGetProperty("delta", out var delta)) continue;
                    // "reasoning" is LM Studio's field for GPT-OSS — it parses the Harmony <|channel|>
                    // envelope server-side and never emits those raw tokens, so treat it as an alias
                    // for "reasoning_content" (same separate-reasoning-stream shape).
                    if (delta.TryGetProperty("reasoning_content", out _) || delta.TryGetProperty("reasoning", out _)) sawReasoning = true;
                    if (delta.TryGetProperty("content", out var c))
                    {
                        var t = c.GetString() ?? "";
                        if (t.Contains("<think>",                 StringComparison.OrdinalIgnoreCase)) sawOpenThink      = true;
                        if (t.Contains("<thinking>",              StringComparison.OrdinalIgnoreCase)) sawOpenThink      = true;
                        if (t.Contains("</think>",                StringComparison.OrdinalIgnoreCase)) sawCloseThink     = true;
                        if (t.Contains("</thinking>",             StringComparison.OrdinalIgnoreCase)) sawCloseThink     = true;
                        if (t.Contains("<|channel>thought",       StringComparison.OrdinalIgnoreCase)) sawChannelThought = true;
                        if (t.Contains("<|channel|>analysis",     StringComparison.OrdinalIgnoreCase)) sawHarmony        = true;
                        if (t.Contains("<|channel|>commentary",   StringComparison.OrdinalIgnoreCase)) sawHarmony        = true;
                        if (t.Contains("<|channel|>final",        StringComparison.OrdinalIgnoreCase)) sawHarmony        = true;
                    }
                    // These signals are each a final verdict on their own — stop reading so a
                    // slow local model doesn't burn the probe budget (and its own GPU) for nothing.
                    if (sawReasoning || sawHarmony || sawChannelThought || sawOpenThink) break;
                }
                catch { }
            }
        }
        catch { return Verdict(); }

        return Verdict();
    }

    public static async Task<string> ProbeToolCallAsync(
        string endpointUrl, string model, string? apiKey = null, string? proxyUrl = null, CancellationToken ct = default, string? keyRef = null)
    {
        // 45s, not 20: reasoning models think before emitting the tool call, and that thinking
        // alone can outrun a short budget on local hardware.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        var fullContent = new StringBuilder();
        bool sawNative  = false;

        try
        {
            using var http = BuildHttp(string.IsNullOrEmpty(proxyUrl) ? apiKey : null);
            var body = JsonSerializer.Serialize(new
            {
                model,
                messages = new[] { new { role = "user", content = "Call get_time with no arguments." } },
                tools = new[]
                {
                    new { type = "function", function = new {
                        name = "get_time", description = "Returns the current time",
                        parameters = new { type = "object", properties = new { } } } }
                },
                stream     = true,
                // Safety bound — see ProbeThinkingAsync: never hand a probe an unbounded budget.
                max_tokens = 2048
            });

            using var req = BuildRequest(endpointUrl, body, apiKey, proxyUrl, keyRef);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!resp.IsSuccessStatusCode) return "Unknown";

            await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(linked.Token)) != null)
            {
                if (!line.StartsWith("data: ")) continue;
                var json = line["data: ".Length..];
                if (json == "[DONE]") break;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("choices", out var choices)) continue;
                    if (choices.GetArrayLength() == 0) continue;
                    if (!choices[0].TryGetProperty("delta", out var delta)) continue;
                    if (delta.TryGetProperty("tool_calls", out _)) sawNative = true;
                    if (delta.TryGetProperty("content",    out var c)) fullContent.Append(c.GetString() ?? "");
                    // Both signals are final verdicts — stop reading as soon as either lands so a
                    // slow local model doesn't burn the probe budget (and its own GPU) for nothing.
                    if (sawNative || ClassifyToolCallText(fullContent.ToString()) != null) break;
                }
                catch { }
            }
        }
        catch { }

        // A timeout lands here with whatever was collected — classify it rather than discard it.
        if (sawNative) return "None";
        return ClassifyToolCallText(fullContent.ToString()) ?? "Unknown";
    }

    // Client-parsed tool-call envelope markers, checked against accumulated content. Returns the
    // format name, or null if no marker has appeared (yet).
    private static string? ClassifyToolCallText(string text)
    {
        if (text.Contains("<|channel|>commentary to=functions.", StringComparison.OrdinalIgnoreCase)) return "Harmony";
        if (text.Contains("<|channel|>analysis to=functions.",  StringComparison.OrdinalIgnoreCase)) return "Harmony";
        if (text.Contains("<tool_call>",                        StringComparison.OrdinalIgnoreCase)) return "ToolCallTag";
        if (text.Contains("<start_function_call>",              StringComparison.OrdinalIgnoreCase)) return "StartFunctionCall";
        if (text.Contains("[TOOL_CALLS]",                       StringComparison.OrdinalIgnoreCase)) return "MistralToolCalls";
        if (text.Contains("<minimax:tool_call>",                StringComparison.OrdinalIgnoreCase)) return "MinimaxToolCall";
        if (text.Contains("<|tool_calls_section_begin|>",       StringComparison.OrdinalIgnoreCase)) return "KimiK2";
        if (text.Contains("<longcat_tool_call>",                StringComparison.OrdinalIgnoreCase)) return "Longcat";
        if (text.Contains("<arg_key>",                          StringComparison.OrdinalIgnoreCase)) return "GlmXml";
        return null;
    }

    // 16x16 solid red PNG — small, unambiguous test image for the vision probe below.
    private const string TestImageBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFklEQVR42mP4z8BAEmIY1TCqYfhqAACQ+f8B8u7oVwAAAABJRU5ErkJggg==";

    // Sends a single unambiguous test image (solid red) and checks whether the model actually reads
    // it — a plain HTTP 200 proves nothing, since many chat templates silently drop unrecognized
    // content parts instead of erroring. Non-streaming: we only need one short word back.
    public static async Task<string> ProbeVisionAsync(
        string endpointUrl, string model, string? apiKey = null, string? proxyUrl = null, CancellationToken ct = default, string? keyRef = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            using var http = BuildHttp(string.IsNullOrEmpty(proxyUrl) ? apiKey : null);
            var body = JsonSerializer.Serialize(new
            {
                model,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = "What single color is this image? Reply with only the color name in English, nothing else." },
                            new { type = "image_url", image_url = new { url = $"data:image/png;base64,{TestImageBase64}" } }
                        }
                    }
                },
                stream     = false,
                // Not 8: a reasoning model burns its whole budget thinking and never emits the
                // colour word, misreporting a vision-capable model as Unsupported.
                max_tokens = 512
            });

            using var req  = BuildRequest(endpointUrl, body, apiKey, proxyUrl, keyRef);
            using var resp = await http.SendAsync(req, linked.Token);

            if (!resp.IsSuccessStatusCode)
            {
                // A 400/422 means the server rejected the multimodal content shape outright — a
                // confident negative. Anything else (auth, 5xx, timeout) is inconclusive.
                var code = (int)resp.StatusCode;
                return code is 400 or 422 ? "Unsupported" : "Unknown";
            }

            var responseBody = await resp.Content.ReadAsStringAsync(linked.Token);
            using var doc = JsonDocument.Parse(responseBody);
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            return text.Contains("red", StringComparison.OrdinalIgnoreCase) ? "Supported" : "Unsupported";
        }
        catch { return "Unknown"; }
    }

    private static HttpClient BuildHttp(string? apiKey)
    {
        var http = new HttpClient();
        if (!string.IsNullOrEmpty(apiKey))
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        return http;
    }

    // When proxyUrl is set, the probe is sent to the local cogitator node's /llm/proxy instead of
    // straight to the model — the node makes the call (no browser CORS / mixed-content), so format
    // detection works for LAN/HTTP models under HTTPS hosting, just like real chat traffic.
    // requireKey is always false for probes: a missing key should not abort detection.
    private static HttpRequestMessage BuildRequest(string endpointUrl, string body, string? apiKey, string? proxyUrl, string? keyRef = null)
    {
        if (!string.IsNullOrEmpty(proxyUrl))
        {
            var wrapped = JsonSerializer.Serialize(new { url = endpointUrl, body, keyRef, apiKey, requireKey = false });
            return new HttpRequestMessage(HttpMethod.Post, proxyUrl)
                { Content = new StringContent(wrapped, Encoding.UTF8, "application/json") };
        }
        return new HttpRequestMessage(HttpMethod.Post, endpointUrl)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
