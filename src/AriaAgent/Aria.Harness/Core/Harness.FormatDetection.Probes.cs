using System.Text;
using System.Text.Json;
using Aria.Agent;
using Aria.Harness.Context;
using Aria.Harness.Formats;
using Aria.Shared;
using Microsoft.Extensions.Logging;

namespace Aria.Harness.Core;

public sealed partial class Harness
{
    // ── Format detection probes (HTTP / bridge runners) ───────────────────────

    private async Task<VisionSupport> RunVisionDetectionAsync(
        string? selectedSourceName, string? modelId, HarnessContext context, CancellationToken ct)
    {
        var (endpoint, model, apiKey) = ResolveEndpoint(selectedSourceName, modelId, context);
        if (endpoint == null) return VisionSupport.Unknown;

        var result = await Aria.Shared.FormatProber.ProbeVisionAsync(endpoint, model, apiKey, ct: ct);
        return Enum.TryParse<VisionSupport>(result, out var parsed) ? parsed : VisionSupport.Unknown;
    }

    private async Task<ThinkingFormat> RunBridgeDetectionAsync(
        ModelSource source, string? modelId, HarnessContext context, CancellationToken ct)
    {
        var model   = modelId ?? source.Models.FirstOrDefault() ?? "default";
        var chatUrl = source.Url.TrimEnd('/') + "/chat/completions";
        var payload = JsonSerializer.Serialize(new { url = chatUrl, model, keyRef = source.ChannelName ?? source.Name });

        _logger.LogInformation("[FormatDetect] Sending /llm/detect-format to bridge: url={Url} model={Model}", source.Url, model);
        try
        {
            var responseJson = await _runtime.BridgePostAsync(
                "http://localhost:5741/llm/detect-format", payload, context, ct, nodeId: context.BridgeNodeId);

            _logger.LogInformation("[FormatDetect] Bridge responded: {Body}",
                responseJson.Length > 200 ? responseJson[..200] : responseJson);

            using var doc = JsonDocument.Parse(responseJson);
            var thinkFmt = ThinkingFormat.None;
            if (doc.RootElement.TryGetProperty("thinking", out var tf) &&
                Enum.TryParse<ThinkingFormat>(tf.GetString(), out var parsedTf))
                thinkFmt = parsedTf;

            if (doc.RootElement.TryGetProperty("toolCall", out var tc) &&
                Enum.TryParse<ToolCallFormat>(tc.GetString(), out var toolFmt))
            {
                await _runtime.FormatCache.SetToolCallFormatAsync(source.Url, modelId ?? source.Models.FirstOrDefault() ?? "", toolFmt, ct);
                _logger.LogInformation("[FormatDetect] Bridge tool-call probe result: {Format}", toolFmt);
            }

            if (doc.RootElement.TryGetProperty("vision", out var vs) &&
                Enum.TryParse<VisionSupport>(vs.GetString(), out var visionFmt))
            {
                await _runtime.FormatCache.SetVisionSupportAsync(source.Url, modelId ?? source.Models.FirstOrDefault() ?? "", visionFmt, ct);
                _logger.LogInformation("[FormatDetect] Bridge vision probe result: {Format}", visionFmt);
            }

            if (doc.RootElement.TryGetProperty("contextWindow", out var cw) &&
                cw.ValueKind == JsonValueKind.Number)
            {
                var tokens = cw.GetInt32();
                var assumed = doc.RootElement.TryGetProperty("contextWindowAssumed", out var cwa)
                              && cwa.ValueKind == JsonValueKind.True;
                await _runtime.FormatCache.SetContextWindowAsync(source.Url, model, new ContextWindow(tokens, assumed), ct);
                _logger.LogInformation("[FormatDetect] Bridge context-window probe result: {Tokens} (assumed={Assumed})", tokens, assumed);
            }

            return thinkFmt;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FormatDetect] Bridge format detection failed for source {Source}", source.Name);
            return ThinkingFormat.None;
        }
    }

    private async Task<ThinkingFormat> RunDetectionAsync(
        string? selectedSourceName, string? modelId, HarnessContext context, CancellationToken ct)
    {
        var (endpoint, model, apiKey) = ResolveEndpoint(selectedSourceName, modelId, context);
        if (endpoint == null) return ThinkingFormat.None;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        bool sawOpenThink  = false;
        bool sawCloseThink = false;
        bool sawReasoning  = false;
        bool sawHarmony    = false;

        try
        {
            using var http = new HttpClient();
            if (!string.IsNullOrEmpty(apiKey))
                http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var body = JsonSerializer.Serialize(new
            {
                model,
                messages = new[] { new { role = "user", content = "What is 3 times 7? Think step by step." } },
                stream     = true,
                // Safety bound — a long-reasoning model given no cap keeps generating for minutes
                // after the probe has its answer; the markers all appear in the earliest deltas.
                max_tokens = 2048
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!resp.IsSuccessStatusCode) return ThinkingFormat.None;

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
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() == 0) continue;
                    var delta = choices[0].GetProperty("delta");

                    // "reasoning_content" (OpenAI o-series/DeepSeek style) and "reasoning" (LM Studio's
                    // field for GPT-OSS — it parses the Harmony <|channel|> envelope server-side and
                    // never puts those raw tokens on the wire, so the literal <|channel|>analysis check
                    // below never fires for it) are the same shape: a separate reasoning stream next to
                    // "content". Treat them as aliases.
                    if (delta.TryGetProperty("reasoning_content", out _) || delta.TryGetProperty("reasoning", out _))
                        sawReasoning = true;

                    if (delta.TryGetProperty("content", out var contentEl))
                    {
                        var text = contentEl.GetString() ?? "";
                        if (text.Contains("<think>",              StringComparison.OrdinalIgnoreCase)) sawOpenThink  = true;
                        if (text.Contains("<thinking>",           StringComparison.OrdinalIgnoreCase)) sawOpenThink  = true;
                        if (text.Contains("</think>",             StringComparison.OrdinalIgnoreCase)) sawCloseThink = true;
                        if (text.Contains("</thinking>",          StringComparison.OrdinalIgnoreCase)) sawCloseThink = true;
                        if (text.Contains("<|channel|>analysis",   StringComparison.OrdinalIgnoreCase)) sawHarmony    = true;
                        if (text.Contains("<|channel|>commentary", StringComparison.OrdinalIgnoreCase)) sawHarmony    = true;
                        if (text.Contains("<|channel|>final",      StringComparison.OrdinalIgnoreCase)) sawHarmony    = true;
                    }

                    // Each of these is a final verdict on its own — stop reading so a slow local
                    // model doesn't burn the probe budget (and its own GPU) for nothing.
                    if (sawReasoning || sawHarmony || sawOpenThink) break;
                }
                catch { /* malformed chunk, skip */ }
            }
        }
        catch (OperationCanceledException) { /* timeout or cancel — use what we detected */ }
        catch (Exception ex) when (ex is HttpIOException || ex.InnerException is HttpIOException)
        {
            _logger.LogDebug("Thinking probe stream closed early for {Endpoint} (expected after early exit)", endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thinking format detection failed for {Endpoint}", endpoint);
        }

        if (sawReasoning) return ThinkingFormat.ReasoningContent;
        if (sawHarmony)   return ThinkingFormat.Harmony;
        if (sawOpenThink)  return ThinkingFormat.ThinkTags;
        if (sawCloseThink) return ThinkingFormat.StartsInThinkMode;
        return ThinkingFormat.None;
    }

    private async Task<ToolCallFormat> RunToolCallDetectionAsync(
        string? selectedSourceName, string? modelId, HarnessContext context, CancellationToken ct)
    {
        var (endpoint, model, apiKey) = ResolveEndpoint(selectedSourceName, modelId, context);
        if (endpoint == null) return ToolCallFormat.Unknown;

        // 45s, not 20: reasoning models think before emitting the tool call, and that thinking
        // alone can outrun a short budget on local hardware.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        var fullContent       = new StringBuilder();
        bool sawNativeToolCall = false;

        try
        {
            using var http = new HttpClient();
            if (!string.IsNullOrEmpty(apiKey))
                http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var body = JsonSerializer.Serialize(new
            {
                model,
                messages = new[] { new { role = "user", content = "Call get_time with no arguments." } },
                tools = new[]
                {
                    new
                    {
                        type = "function",
                        function = new
                        {
                            name        = "get_time",
                            description = "Returns the current time",
                            parameters  = new { type = "object", properties = new { } }
                        }
                    }
                },
                stream     = true,
                // Safety bound — see RunDetectionAsync: never hand a probe an unbounded budget.
                max_tokens = 2048
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!resp.IsSuccessStatusCode) return ToolCallFormat.Unknown;

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
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() == 0) continue;
                    var delta = choices[0].GetProperty("delta");

                    if (delta.TryGetProperty("tool_calls", out _))
                        sawNativeToolCall = true;

                    if (delta.TryGetProperty("content", out var cEl))
                        fullContent.Append(cEl.GetString() ?? "");

                    // Both signals are final verdicts — stop reading as soon as either lands so a
                    // slow local model doesn't burn the probe budget (and its own GPU) for nothing.
                    if (sawNativeToolCall || ClassifyClientToolCallText(fullContent.ToString()) != null) break;
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { /* timeout or cancel — use what we detected */ }
        catch (Exception ex) when (ex is HttpIOException || ex.InnerException is HttpIOException)
        {
            _logger.LogDebug("Tool-call probe stream closed early for {Endpoint} (expected after early exit)", endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool-call format detection failed for {Endpoint}", endpoint);
        }

        if (sawNativeToolCall) return ToolCallFormat.None;

        return ClassifyClientToolCallText(fullContent.ToString()) ?? ToolCallFormat.Unknown;
    }

    // Client-parsed tool-call envelope markers, checked against accumulated content. Returns the
    // format, or null if no marker has appeared (yet).
    private static ToolCallFormat? ClassifyClientToolCallText(string text)
    {
        if (text.Contains("<|channel|>commentary to=functions.", StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.Harmony;
        if (text.Contains("<|channel|>analysis to=functions.",  StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.Harmony;
        if (text.Contains("<tool_call>",                        StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.ToolCallTag;
        if (text.Contains("<start_function_call>",              StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.StartFunctionCall;
        if (text.Contains("[TOOL_CALLS]",                       StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.MistralToolCalls;
        if (text.Contains("<minimax:tool_call>",                StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.MinimaxToolCall;
        if (text.Contains("<|tool_calls_section_begin|>",       StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.KimiK2;
        if (text.Contains("<longcat_tool_call>",                StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.Longcat;
        if (text.Contains("<arg_key>",                          StringComparison.OrdinalIgnoreCase)) return ToolCallFormat.GlmXml;
        return null;
    }

    private (string? endpoint, string model, string? apiKey) ResolveEndpoint(string? sourceName, string? modelId, HarnessContext context)
    {
        var source = _runtime.FindSource(sourceName, context);
        if (source == null) return (null, "", null);

        var endpoint = source.Url.TrimEnd('/') + "/chat/completions";
        var model    = modelId ?? source.Models.FirstOrDefault() ?? "default";
        return (endpoint, model, source.GetApiKey());
    }
}
