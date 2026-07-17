namespace Aria.Agent;

/// <summary>
/// Public cloud/OpenAI-compatible providers available to every Aria host.
/// Kept next to <see cref="ModelSource"/> so Aria.Web and Aria.Console share the same list.
/// Canonical base URLs must stay in sync with <c>Aria.Shared.PublicProviderCatalog</c>, which is the
/// node-side egress source of truth used by <c>/llm/proxy</c>.
/// </summary>
public static class PublicModelSourceCatalog
{
    public static readonly IReadOnlyList<ModelSource> Providers = new List<ModelSource>
    {
        new() { Name = "OpenAI",        Url = "https://api.openai.com/v1",                                        IsPublicProvider = true, Models = ["gpt-4o", "gpt-4o-mini", "o3", "o4-mini"] },
        new() { Name = "Anthropic",     Url = "https://api.anthropic.com/v1",                                     IsPublicProvider = true, Models = ["claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5-20251001"] },
        new() { Name = "Google Gemini", Url = "https://generativelanguage.googleapis.com/v1beta/openai/",         IsPublicProvider = true, Models = ["gemini-2.5-pro", "gemini-2.5-flash"] },
        new() { Name = "Mistral",       Url = "https://api.mistral.ai/v1",                                        IsPublicProvider = true, Models = ["mistral-large-latest", "mistral-small-latest", "codestral-latest"] },
        new() { Name = "Groq",          Url = "https://api.groq.com/openai/v1",                                   IsPublicProvider = true, Models = ["llama-3.3-70b-versatile", "meta-llama/llama-4-scout-17b-16e-instruct", "qwen/qwen3-32b", "llama-3.1-8b-instant"] },
    }.AsReadOnly();
}
