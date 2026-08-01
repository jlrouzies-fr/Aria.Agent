namespace Aria.Shared;

/// <summary>
/// Best-effort static lookup for well-known cloud model context windows. These are used when a
/// public provider is selected and no per-channel override or discovery is available.
/// </summary>
public static class ContextWindowCatalog
{
    // Values are approximate public context-window sizes (input context, in tokens).
    private static readonly Dictionary<string, int> ByModelId = new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenAI
        ["gpt-4o"] = 128_000,
        ["gpt-4o-mini"] = 128_000,
        ["gpt-4-turbo"] = 128_000,
        ["gpt-4-turbo-preview"] = 128_000,
        ["gpt-4-0125-preview"] = 128_000,
        ["gpt-4-1106-preview"] = 128_000,
        ["gpt-4"] = 8_192,
        ["gpt-4-32k"] = 32_768,
        ["gpt-3.5-turbo"] = 16_385,
        ["gpt-3.5-turbo-0125"] = 16_385,
        ["gpt-3.5-turbo-1106"] = 16_385,
        ["o1"] = 200_000,
        ["o1-preview"] = 128_000,
        ["o1-mini"] = 128_000,
        ["o3"] = 200_000,
        ["o3-mini"] = 200_000,

        // Anthropic
        ["claude-3-5-sonnet-latest"] = 200_000,
        ["claude-3-5-sonnet-20241022"] = 200_000,
        ["claude-3-5-sonnet-20240620"] = 200_000,
        ["claude-3-5-haiku-latest"] = 200_000,
        ["claude-3-5-haiku-20241022"] = 200_000,
        ["claude-3-opus-latest"] = 200_000,
        ["claude-3-opus-20240229"] = 200_000,
        ["claude-3-sonnet-20240229"] = 200_000,
        ["claude-3-haiku-20240307"] = 200_000,

        // Google Gemini
        ["gemini-2.5-pro"] = 1_048_576,
        ["gemini-2.5-flash"] = 1_048_576,
        ["gemini-2.0-pro-exp"] = 2_097_152,
        ["gemini-2.0-flash"] = 1_048_576,
        ["gemini-2.0-flash-thinking-exp"] = 1_048_576,
        ["gemini-1.5-pro"] = 2_097_152,
        ["gemini-1.5-pro-latest"] = 2_097_152,
        ["gemini-1.5-flash"] = 1_048_576,
        ["gemini-1.5-flash-latest"] = 1_048_576,
        ["gemini-1.0-pro"] = 32_768,

        // Mistral
        ["mistral-large-latest"] = 131_072,
        ["mistral-large-2411"] = 131_072,
        ["mistral-large-2407"] = 131_072,
        ["mistral-medium"] = 32_768,
        ["mistral-small-latest"] = 32_768,
        ["ministral-8b-latest"] = 131_072,
        ["ministral-3b-latest"] = 131_072,
        ["pixtral-large-latest"] = 131_072,

        // Groq
        ["llama-3.3-70b-versatile"] = 131_072,
        ["llama-3.1-8b-instant"] = 131_072,
        ["llama3-70b-8192"] = 8_192,
        ["llama3-8b-8192"] = 8_192,
        ["mixtral-8x7b-32768"] = 32_768,
        ["gemma2-9b-it"] = 8_192,
    };

    /// <summary>Look up a known context window by exact or normalized model id.</summary>
    public static int? TryGetKnownTokens(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        // Exact match first.
        if (ByModelId.TryGetValue(modelId, out var exact)) return exact;

        // Fuzzy: strip known date tags and trailing -latest/-preview variants.
        var normalized = modelId.Trim().Replace(" ", "-");
        if (ByModelId.TryGetValue(normalized, out var norm)) return norm;

        // Prefix match for dated variants (e.g. claude-3-5-sonnet-20241022 already exact, but
        // gpt-4o-2024-08-06 is not; fall back to the family entry if present).
        var family = normalized;
        while (true)
        {
            var lastDash = family.LastIndexOf('-');
            if (lastDash <= 0) break;
            family = family[..lastDash];
            if (ByModelId.TryGetValue(family, out var familyTokens)) return familyTokens;
        }

        return null;
    }
}
