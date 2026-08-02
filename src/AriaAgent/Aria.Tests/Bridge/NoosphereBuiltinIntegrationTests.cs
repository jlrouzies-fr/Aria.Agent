using Aria.Bridge.Services.Noosphere;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Optional live inference against downloaded built-in models. Soft-skips unless
/// ARIA_BUILTIN_MODELS_DIR points at a catalog folder (or the default bridge app-data dir has them).
/// </summary>
public class NoosphereBuiltinIntegrationTests
{
    private static string? ResolveModelsDir()
    {
        var env = Environment.GetEnvironmentVariable("ARIA_BUILTIN_MODELS_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        // App-data auto-detect is opt-in — cold GGUF load is multi-second and would inflate every local run.
        if (!string.Equals(Environment.GetEnvironmentVariable("ARIA_BUILTIN_LIVE"), "1", StringComparison.Ordinal))
            return null;
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "aria-bridge", "noosphere-models");
        return Directory.Exists(appData) ? appData : null;
    }

    [Fact]
    public async Task EmbedBatch_ProducesNormalizedVector_WhenModelsPresent()
    {
        var dir = ResolveModelsDir();
        if (dir == null) return;

        using var runtime = new NoosphereBuiltinRuntime(dir);
        if (!runtime.IsRoleOnDisk(NoosphereBuiltinCatalog.RoleEmbed))
            return;

        var (vectors, error) = await runtime.EmbedBatchAsync(["Noosphere engram test"], CancellationToken.None);
        Assert.Null(error);
        Assert.NotNull(vectors);
        Assert.Single(vectors!);
        Assert.True(vectors[0].Length >= 64);
        var norm = MathF.Sqrt(vectors[0].Sum(v => v * v));
        Assert.InRange(norm, 0.99f, 1.01f);
    }

    [Fact]
    public async Task ExtractChat_ProducesParsableFacts_WhenModelsPresent()
    {
        var dir = ResolveModelsDir();
        if (dir == null) return;

        using var runtime = new NoosphereBuiltinRuntime(dir);
        if (!runtime.IsRoleOnDisk(NoosphereBuiltinCatalog.RoleExtract))
            return;

        const string system =
            """
            Extract atomic self-contained facts from the user text into JSON only.
            Rules: resolve pronouns; entities need name+kind; relations only between entities on the same fact. No markdown.
            Schema:
            {"facts":[{"content":"...","entities":[{"name":"...","kind":"person|place|org|concept|thing|event|project|other"}],"relations":[{"from":"...","relation":"...","to":"..."}],"timeAnchor":"YYYY-MM-DD"}]}
            """;
        const string user = "Alice works at Acme Corp in Berlin.";

        var (raw, error) = await runtime.CompleteChatAsync(
            system, user, temperature: 0.1, maxTokens: 512, CancellationToken.None, prefillJsonObject: false);
        Assert.Null(error);
        Assert.False(string.IsNullOrWhiteSpace(raw), "model returned empty text");

        var json = NoosphereExtractor.TryExtractJson(raw);
        Assert.False(string.IsNullOrWhiteSpace(json), "no JSON in: " + raw);
        var facts = NoosphereExtractor.ParseFacts(json!);
        Assert.NotNull(facts);
        Assert.NotEmpty(facts!);
        Assert.Contains(facts, f => f.Content.Contains("Alice", StringComparison.OrdinalIgnoreCase)
                                    || f.Entities.Any(e => e.Name.Contains("Alice", StringComparison.OrdinalIgnoreCase)));
    }
}
