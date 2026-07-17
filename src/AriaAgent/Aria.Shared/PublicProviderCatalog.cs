namespace Aria.Shared;

/// <summary>
/// Canonical cloud/OpenAI-compatible providers, with the <b>fixed</b> base URL each provider's key is
/// allowed to reach. This is the node-side source of truth for egress destinations: <c>/llm/proxy</c>
/// resolves a keyed request's host from here (public providers) or from a bridge-authored custom channel,
/// so a compromised server can never redirect a stored key to a host it controls.
///
/// Keep in sync with <c>Aria.Agent.PublicModelSourceCatalog</c> (the web/console-facing ModelSource list).
/// </summary>
public static class PublicProviderCatalog
{
    public sealed record Provider(string Name, string CanonicalUrl, string[] DefaultModels);

    public static readonly IReadOnlyList<Provider> Providers = new List<Provider>
    {
        new("OpenAI",        "https://api.openai.com/v1",                             ["gpt-4o", "gpt-4o-mini", "o3", "o4-mini"]),
        new("Anthropic",     "https://api.anthropic.com/v1",                          ["claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5-20251001"]),
        new("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/", ["gemini-2.5-pro", "gemini-2.5-flash"]),
        new("Mistral",       "https://api.mistral.ai/v1",                             ["mistral-large-latest", "mistral-small-latest", "codestral-latest"]),
        new("Groq",          "https://api.groq.com/openai/v1",                        ["llama-3.3-70b-versatile", "meta-llama/llama-4-scout-17b-16e-instruct", "qwen/qwen3-32b", "llama-3.1-8b-instant"]),
    }.AsReadOnly();

    /// <summary>Returns the canonical base URL for a public provider name, or null if not a public provider.</summary>
    public static string? CanonicalUrlFor(string? name) =>
        name is null ? null : Providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.CanonicalUrl;

    public static bool IsPublic(string? name) => CanonicalUrlFor(name) != null;

    /// <summary>
    /// Forces <paramref name="requestedUrl"/> onto the authoritative scheme+host of <paramref name="authBase"/>,
    /// preserving the operation path and query the caller appended. Whether the requested URL carried the
    /// authoritative base path or a tampered host, the result always targets the authoritative origin — so a
    /// stored key can only ever reach the host the node declared. Falls back to <paramref name="authBase"/>
    /// on a malformed input (never the requested host).
    /// </summary>
    public static string PinToHost(string authBase, string requestedUrl)
    {
        try
        {
            var baseUri  = new Uri(authBase, UriKind.Absolute);
            var basePath = baseUri.AbsolutePath.TrimEnd('/');          // e.g. /v1  or  /openai/v1

            var reqUri   = new Uri(requestedUrl, UriKind.Absolute);
            var reqPath  = reqUri.AbsolutePath;                        // e.g. /v1/chat/completions or /chat/completions

            var fullPath = basePath.Length == 0
                ? reqPath
                : (reqPath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase) || reqPath.Equals(basePath, StringComparison.OrdinalIgnoreCase)
                    ? reqPath
                    : basePath + (reqPath.StartsWith('/') ? reqPath : "/" + reqPath));

            return new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.Port)
            {
                Path  = fullPath,
                Query = reqUri.Query.TrimStart('?'),
            }.Uri.ToString();
        }
        catch
        {
            return authBase;
        }
    }
}
