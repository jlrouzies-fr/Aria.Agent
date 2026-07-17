using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Aria.Tools;

public record OllamaConfig(string? BaseUrl = "https://ollama.com", string? ApiKeyFile = null, bool Enabled = false);

public static class WebSearchTools
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .Build();

    [Description("Performs a web search using the Emperor's Codex Archive.")]
    public static async Task<string> SearchWeb(
        [Description("The query string for the web search.")] string query)
    {
        try
        {
            var ollamaConfig = _configuration.GetSection("OllamaWebSearch").Get<OllamaConfig>() ?? new OllamaConfig();

            if (!ollamaConfig.Enabled)
                return "Web search is not enabled in appsettings.json.";

            if (string.IsNullOrEmpty(ollamaConfig.ApiKeyFile))
                return "Web search is not configured. Set the 'OllamaWebSearch:ApiKeyFile' in appsettings.json to point to a file containing your API key.";

            var apiKey = await File.ReadAllTextAsync(ollamaConfig.ApiKeyFile);

            var baseUrl = ollamaConfig.BaseUrl?.TrimEnd('/') ?? "https://ollama.com";
            var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ollama-web-search-tool", "1.0"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var requestBody = new { query, max_results = 3 };
            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                System.Text.Encoding.UTF8,
                "application/json");

            using var jsonDocument = await client.ReadJsonDocumentFromPostAsync("/api/web_search", jsonContent);

            return FormatSearchResults(jsonDocument.RootElement);
        }
        catch (Exception ex)
        {
            return $"Error performing web search: {ex.Message}";
        }
    }

    // A web_search result carries the full page body per hit; left unbounded one query produced
    // ~34K chars. Fed back as a tool result that floods a local model's context window, evicting
    // the system prompt + conversation history — the model then loses the thread and re-introduces
    // itself. Bound each result's content and the total so the context stays intact.
    public const int MaxContentPerResult = 1500;
    public const int MaxTotalChars       = 6000;

    public static string FormatSearchResults(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var resultsArray) || resultsArray.ValueKind != JsonValueKind.Array)
            return "Web search failed: No results found or unexpected response format.";

        var results = new List<string>();
        foreach (var result in resultsArray.EnumerateArray())
        {
            var title   = result.TryGetProperty("title",   out var t) ? t.GetString() ?? "No Title"   : "No Title";
            var url     = result.TryGetProperty("url",     out var u) ? u.GetString() ?? "No URL"     : "No URL";
            var content = result.TryGetProperty("content", out var c) ? c.GetString() ?? "No Content" : "No Content";

            if (content.Length > MaxContentPerResult)
                content = content[..MaxContentPerResult] + " …[truncated]";

            results.Add($"Title: {title}\nURL: {url}\nContent: {content}");
        }

        var text = string.Join("\n---\n", results);
        return text.Length > MaxTotalChars ? text[..MaxTotalChars] + "\n…[truncated]" : text;
    }
}

public static class WebPageTools
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    static WebPageTools()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("aria-agent/1.0");
    }

    [Description("Fetches and reads the text content of a web page. Use this to read articles, documentation, or any page the user provides a URL for.")]
    public static async Task<string> FetchWebPage(
        [Description("The full URL (http or https) of the web page to fetch.")] string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                return "Invalid URL. Only http:// and https:// URLs are supported.";

            var html = await _httpClient.GetStringAsync(uri);

            // Remove script, style, and noscript blocks entirely
            html = System.Text.RegularExpressions.Regex.Replace(
                html, @"<(script|style|noscript)[^>]*>[\s\S]*?</\1>", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Strip remaining HTML tags
            var text = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", " ");

            // Decode HTML entities
            text = System.Net.WebUtility.HtmlDecode(text);

            // Collapse excessive whitespace
            text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]{2,}", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
            text = text.Trim();

            const int maxChars = 20_000;
            if (text.Length > maxChars)
                text = text[..maxChars] + "\n\n[... content truncated at 20,000 characters ...]";

            return string.IsNullOrWhiteSpace(text) ? "Page fetched but no readable text content found." : text;
        }
        catch (Exception ex)
        {
            return $"Error fetching page: {ex.Message}";
        }
    }
}

internal static class HttpClientExtension
{
    public static async Task<JsonDocument> ReadJsonDocumentAsync(this HttpClient client, string requestUri)
    {
        using var response = await client.GetAsync(requestUri);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

    public static async Task<JsonDocument> ReadJsonDocumentFromPostAsync(this HttpClient client, string requestUri, HttpContent content)
    {
        using var response = await client.PostAsync(requestUri, content);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }
}
