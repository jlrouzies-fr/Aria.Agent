using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Services.Llm;

namespace Aria.Bridge;

// Node-side web search + datetime. Executing these on the bridge (the cogitator node) keeps the
// web-search API key on the user's machine — never on the server — and means both Aria.Web and
// Aria.Console get them through the same bridge tool path (so they render as live tool blocks).
public static partial class BuiltinTools
{
    private static readonly HttpClient _webHttp = new();

    // Ollama web search defaults to the public endpoint; the API key is stored encrypted in the
    // bridge's local vault under this provider name (same table/encryption as cloud LLM keys —
    // see LlmKeyEndpoints), configured from the bridge status page rather than a config file.
    private const string DefaultOllamaBaseUrl = "https://ollama.com";
    private const string OllamaWebSearchKeyName = "OllamaWebSearch";

    private static IEnumerable<BridgeToolInfo> WebToolInfos()
    {
        yield return new("GetCurrentDateTime",
            "Report the current temporal datum and time.",
            Js("""{"type":"object","properties":{}}"""));

        yield return new("SearchWeb",
            "Performs a web search using the Emperor's Codex Archive.",
            Js("""
               {"type":"object",
                "properties":{"query":{"type":"string","description":"The query string for the web search."}},
                "required":["query"]}
               """));
    }

    private static ToolCallResponse GetCurrentDateTime() =>
        new(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"), false);

    // Bound the result so a huge page body can't flood a local model's context window.
    private const int WebMaxPerResult = 1500;
    private const int WebMaxTotal     = 6000;

    private static async Task<ToolCallResponse> SearchWebAsync(Dictionary<string, JsonElement> args, BridgeDbContext? db)
    {
        var query = args.Str("query") ?? throw new ArgumentException("'query' is required");

        var apiKey = db == null ? null : await LlmKeyStore.GetPlaintextKeyAsync(db, OllamaWebSearchKeyName);
        if (string.IsNullOrEmpty(apiKey))
            return new("Web search is not configured on this node — open http://localhost:5741 → Tools / MCP → Web Search (Ollama) and save an API key.", true);

        var baseUrl = DefaultOllamaBaseUrl.TrimEnd('/');

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/web_search");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("ollama-web-search-tool", "1.0"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { query, max_results = 3 }), Encoding.UTF8, "application/json");

        using var resp = await _webHttp.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return new(FormatSearchResults(doc.RootElement), false);
    }

    private static string FormatSearchResults(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return "Web search failed: No results found or unexpected response format.";

        var results = new List<string>();
        foreach (var r in arr.EnumerateArray())
        {
            var title   = r.TryGetProperty("title",   out var t) ? t.GetString() ?? "No Title"   : "No Title";
            var url     = r.TryGetProperty("url",     out var u) ? u.GetString() ?? "No URL"     : "No URL";
            var content = r.TryGetProperty("content", out var c) ? c.GetString() ?? "No Content" : "No Content";
            if (content.Length > WebMaxPerResult) content = content[..WebMaxPerResult] + " …[truncated]";
            results.Add($"Title: {title}\nURL: {url}\nContent: {content}");
        }

        var text = string.Join("\n---\n", results);
        return text.Length > WebMaxTotal ? text[..WebMaxTotal] + "\n…[truncated]" : text;
    }
}
