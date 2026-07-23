using System.Text;
using System.Text.Json;
using Aria.Bridge.Endpoints;

namespace Aria.Bridge;

// Structured HTTP client for API testing, executed on the node. Running it bridge-side means the
// request originates from the user's machine: it can reach LAN/localhost services the hosted server
// cannot see, and no response data transits anywhere else. Deliberately NOT added to GetToolInfos()
// (the terminal tool's /tools/list would leak it into every session) — Harness registers it
// explicitly (case "http_request"), like the memory tools. Layer B classification stays Sensitive:
// the tool can carry data OUT of the node, so it is not on the read-only Benign list.
public static partial class BuiltinTools
{
    // Redirects are not auto-followed — a 3xx is reported with its Location header so the agent
    // sees the real API surface instead of silently landing wherever it redirects.
    private static readonly HttpClient _httpRequestClient =
        new(new HttpClientHandler { AllowAutoRedirect = false });

    private static readonly string[] HttpMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    private const int HttpDefaultTimeoutSeconds = 30;
    private const int HttpMaxTimeoutSeconds     = 60;
    private const int HttpMaxResponseHeaders    = 50;

    private static async Task<ToolCallResponse> HttpRequestAsync(Dictionary<string, JsonElement> args)
    {
        var methodStr = (args.Str("method") ?? throw new ArgumentException("'method' is required")).Trim().ToUpperInvariant();
        if (!HttpMethods.Contains(methodStr))
            return Err($"'method' must be one of {string.Join(", ", HttpMethods)}.");

        var urlStr = args.Str("url") ?? throw new ArgumentException("'url' is required");
        if (!Uri.TryCreate(urlStr, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return Err("Invalid URL. Provide an absolute http:// or https:// URL.");

        var timeoutSeconds = args.Int("timeout_seconds") ?? HttpDefaultTimeoutSeconds;
        if (timeoutSeconds <= 0 || timeoutSeconds > HttpMaxTimeoutSeconds)
            return Err($"'timeout_seconds' must be between 1 and {HttpMaxTimeoutSeconds}.");

        using var req = new HttpRequestMessage(new HttpMethod(methodStr), uri);

        // Content-Type is collected first so a caller-supplied one replaces the StringContent
        // default instead of producing a duplicate header.
        string? contentType = null;
        var headers = new List<(string Name, string Value)>();
        if (args.TryGetValue("headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var h in headersEl.EnumerateObject())
            {
                if (h.Value.ValueKind != JsonValueKind.String) continue;
                if (h.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                    contentType = h.Value.GetString();
                else
                    headers.Add((h.Name, h.Value.GetString()!));
            }
        }

        if (args.Str("body") is { } body)
        {
            req.Content = new StringContent(body, Encoding.UTF8);
            if (contentType != null &&
                System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var mt))
                req.Content.Headers.ContentType = mt;
        }

        foreach (var (name, value) in headers)
            req.Headers.TryAddWithoutValidation(name, value);

        // Per-request timeout (the shared client's Timeout stays infinite).
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        HttpResponseMessage resp;
        try
        {
            resp = await _httpRequestClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return Err($"Request timed out after {timeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            return Err($"Request failed: {ex.Message}");
        }

        using (resp)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}".TrimEnd());

            var headerCount = 0;
            foreach (var h in resp.Headers.Concat(resp.Content.Headers))
            {
                if (headerCount++ >= HttpMaxResponseHeaders)
                {
                    sb.AppendLine($"… (response headers truncated at {HttpMaxResponseHeaders})");
                    break;
                }
                sb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
            }
            sb.AppendLine();

            var text = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            if (text.Length > GitEndpoints.MaxOutputChars)
            {
                sb.Append(text[..GitEndpoints.MaxOutputChars]);
                sb.Append($"\n… (body truncated at {GitEndpoints.MaxOutputChars:N0} chars)");
            }
            else
            {
                sb.Append(text);
            }

            return new ToolCallResponse(sb.ToString(), false);
        }
    }
}
