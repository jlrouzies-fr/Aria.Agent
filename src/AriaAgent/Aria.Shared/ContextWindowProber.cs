using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Aria.Shared;

/// <summary>
/// Best-effort discovery of a model's context window from the provider's own metadata endpoints.
/// Returns null when nothing authoritative is available — callers fall back to the catalog or the
/// 100k assumption.
/// </summary>
public static class ContextWindowProber
{
    /// <summary>
    /// Probe the endpoint for the model's context window.
    /// Order: Ollama /api/show, then OpenAI-compatible /models/{id} metadata.
    /// </summary>
    public static async Task<int?> ProbeAsync(string endpointUrl, string model, string? apiKey = null, CancellationToken ct = default)
    {
        var baseUrl = endpointUrl.TrimEnd('/');
        // Strip the /chat/completions suffix if present so we land on the API base.
        if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[.."/chat/completions".Length];

        try
        {
            var ollama = await ProbeOllamaAsync(baseUrl, model, apiKey, ct);
            if (ollama.HasValue) return ollama.Value;
        }
        catch { /* best-effort */ }

        try
        {
            var openAi = await ProbeOpenAiMetadataAsync(baseUrl, model, apiKey, ct);
            if (openAi.HasValue) return openAi.Value;
        }
        catch { /* best-effort */ }

        return null;
    }

    private static async Task<int?> ProbeOllamaAsync(string baseUrl, string model, string? apiKey, CancellationToken ct)
    {
        // Ollama's native API lives at the server root, but the chat endpoint we are handed may be
        // the OpenAI-compatible /v1/chat/completions path. Try both /v1/api/show and /api/show.
        var candidates = new List<string> { baseUrl };
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            candidates.Add(baseUrl[..^"/v1".Length]);

        using var http = BuildHttp(apiKey);
        var body = JsonSerializer.Serialize(new { model });

        foreach (var root in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, root.TrimEnd('/') + "/api/show")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                using var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                // Preferred: model_info.context_length (exact int set by the model's Modelfile).
                if (doc.RootElement.TryGetProperty("model_info", out var modelInfo))
                {
                    if (modelInfo.TryGetProperty("context_length", out var cl) && cl.ValueKind == JsonValueKind.Number)
                        return cl.GetInt32();
                    if (modelInfo.TryGetProperty("general.context_length", out var gcl) && gcl.ValueKind == JsonValueKind.Number)
                        return gcl.GetInt32();
                }

                // Fallback: parameters.num_ctx, often a string like "8192" or "2048".
                if (doc.RootElement.TryGetProperty("parameters", out var parameters))
                {
                    var text = parameters.GetString() ?? "";
                    var numCtx = ExtractKeyValueInt(text, "num_ctx");
                    if (numCtx.HasValue) return numCtx.Value;
                }
            }
            catch { /* try the next candidate */ }
        }

        return null;
    }

    private static async Task<int?> ProbeOpenAiMetadataAsync(string baseUrl, string model, string? apiKey, CancellationToken ct)
    {
        using var http = BuildHttp(apiKey);
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models/" + Uri.EscapeDataString(model));
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        // OpenAI / LM Studio model metadata.
        if (doc.RootElement.TryGetProperty("context_window", out var cw) && cw.ValueKind == JsonValueKind.Number)
            return cw.GetInt32();

        if (doc.RootElement.TryGetProperty("max_model_len", out var mml) && mml.ValueKind == JsonValueKind.Number)
            return mml.GetInt32();

        // Some gateways put context length inside a nested "info" object.
        if (doc.RootElement.TryGetProperty("info", out var info))
        {
            if (info.TryGetProperty("context_window", out var icw) && icw.ValueKind == JsonValueKind.Number)
                return icw.GetInt32();
            if (info.TryGetProperty("max_model_len", out var imml) && imml.ValueKind == JsonValueKind.Number)
                return imml.GetInt32();
        }

        return null;
    }

    private static int? ExtractKeyValueInt(string text, string key)
    {
        // parameters block is "key value\nkey value"; e.g. "num_ctx 8192\nnum_thread 4".
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Equals(key, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(parts[1], out var value))
                return value;
        }
        return null;
    }

    private static HttpClient BuildHttp(string? apiKey)
    {
        var http = new HttpClient();
        if (!string.IsNullOrEmpty(apiKey))
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        return http;
    }
}
