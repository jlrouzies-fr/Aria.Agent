using Aria.Bridge.Services.Noosphere;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Builtin 1.2B models often drift from the strict schema (bare arrays, text vs content).
/// Parsing must stay tolerant — a silent empty parse is what dumps Inscribe to raw storage.
/// </summary>
public class NoosphereExtractionJsonTests
{
    [Fact]
    public void ParseFacts_CanonicalSchema()
    {
        var facts = NoosphereExtractor.ParseFacts(
            """{"facts":[{"content":"Alex lives in Paris","entities":[{"name":"Alex","kind":"person"},{"name":"Paris","kind":"place"}],"relations":[{"from":"Alex","relation":"lives_in","to":"Paris"}]}]}""");

        Assert.NotNull(facts);
        Assert.Single(facts!);
        Assert.Equal("Alex lives in Paris", facts[0].Content);
        Assert.Equal(2, facts[0].Entities.Count);
        Assert.Single(facts[0].Relations);
    }

    [Fact]
    public void ParseFacts_AcceptsTextAliasAndBareArray()
    {
        var facts = NoosphereExtractor.ParseFacts(
            """[{"text":"Ship left dock","entities":[{"name":"Ship","kind":"thing"}]}]""");

        Assert.NotNull(facts);
        Assert.Single(facts!);
        Assert.Equal("Ship left dock", facts[0].Content);
        Assert.Equal("Ship", facts[0].Entities[0].Name);
    }

    [Fact]
    public void ParseFacts_UnwrapsArrayWrappedFactsObject()
    {
        // Qwen2.5-3B Q4 live failure: root array holding one {"facts":[…]} object — ReadFact
        // saw no content and Inscribe fell through to raw.
        var facts = NoosphereExtractor.ParseFacts(
            """[{"facts":[{"content":"Project Aria.Agent Noosphere extracts with Qwen2.5-3B-Instruct.","entities":[{"name":"Aria.Agent","kind":"project"},{"name":"Qwen2.5-3B-Instruct","kind":"concept"}],"relations":[]}]}]""");

        Assert.NotNull(facts);
        Assert.Single(facts!);
        Assert.Contains("Aria.Agent", facts[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, facts[0].Entities.Count);
        Assert.Equal("project", facts[0].Entities[0].Kind);
    }

    [Fact]
    public void ParseFacts_UnwrapsMultipleArrayWrappedFactsObjects()
    {
        var facts = NoosphereExtractor.ParseFacts(
            """[{"facts":[{"content":"one","entities":[]}]},{"facts":[{"content":"two","entities":[{"name":"X","kind":"thing"}]}]}]""");

        Assert.NotNull(facts);
        Assert.Equal(2, facts!.Count);
        Assert.Equal("one", facts[0].Content);
        Assert.Equal("two", facts[1].Content);
        Assert.Single(facts[1].Entities);
    }

    [Fact]
    public void ParseFacts_AcceptsSingleRootFactObject()
    {
        var facts = NoosphereExtractor.ParseFacts(
            """{"content":"Meeting moved to Tuesday","entities":[],"relations":[]}""");

        Assert.NotNull(facts);
        Assert.Single(facts!);
        Assert.Equal("Meeting moved to Tuesday", facts[0].Content);
    }

    [Fact]
    public void ParseFacts_CaseInsensitiveFactsKey()
    {
        var facts = NoosphereExtractor.ParseFacts(
            """{"Facts":[{"Content":"Hello world","Entities":[]}]}""");

        Assert.NotNull(facts);
        Assert.Single(facts!);
        Assert.Equal("Hello world", facts[0].Content);
    }

    [Fact]
    public void TryExtractJson_PrefersObjectThenArray()
    {
        Assert.Equal(
            """{"facts":[]}""",
            NoosphereExtractor.TryExtractJson("Here you go:\n```json\n{\"facts\":[]}\n```"));
        Assert.Equal(
            """[{"content":"x"}]""",
            NoosphereExtractor.TryExtractJson("Result: [{\"content\":\"x\"}]"));
    }

    [Fact]
    public void TryExtractJson_StopsAtBalancedClose_IgnoresTrailingJunk()
    {
        var json = NoosphereExtractor.TryExtractJson(
            """{"facts":[{"content":"ok","entities":[]}]} and then more: {"broken":""}""");
        Assert.Equal("""{"facts":[{"content":"ok","entities":[]}]}""", json);
        var facts = NoosphereExtractor.ParseFacts(json!);
        Assert.Single(facts!);
    }

    [Fact]
    public void ParseFacts_EmptyFactsArray_ReturnsEmpty()
    {
        var facts = NoosphereExtractor.ParseFacts("""{"facts":[]}""");
        Assert.NotNull(facts);
        Assert.Empty(facts!);
    }

    [Fact]
    public void ParseFacts_AcceptsBareStringEntities_InfersKind()
    {
        var facts = NoosphereExtractor.ParseFacts(
            """{"facts":[{"content":"Spectra.MLX runs locally","entities":["Spectra.MLX","AGENTS.md","8001","data-repo"],"relations":[]}]}""");

        Assert.NotNull(facts);
        Assert.Single(facts!);
        Assert.Equal(4, facts[0].Entities.Count);
        Assert.Equal("Spectra.MLX", facts[0].Entities[0].Name);
        // Dotted product name without a file-like extension → other; files/ports/slugs infer.
        Assert.Equal("other", facts[0].Entities[0].Kind);
        Assert.Equal("thing", facts[0].Entities[1].Kind);   // AGENTS.md
        Assert.Equal("concept", facts[0].Entities[2].Kind); // 8001
        Assert.Equal("project", facts[0].Entities[3].Kind); // data-repo
    }

    [Theory]
    [InlineData("server.py", null, "thing")]
    [InlineData("AGENTS.md", null, "thing")]
    [InlineData("8001", null, "concept")]
    [InlineData("data-repo", null, "project")]
    [InlineData("Alex", "person", "person")]
    [InlineData("Alex", "PERSON", "person")]
    [InlineData("X", "person|place", "person")]
    [InlineData("mystery", null, "other")]
    public void ResolveEntityKind_InfersOrNormalizes(string name, string? kind, string expected)
        => Assert.Equal(expected, NoosphereExtractor.ResolveEntityKind(name, kind));

    [Fact]
    public void SoftRepairJson_QuotesBareKindEnumsAndStripsTrailingCommas()
    {
        var repaired = NoosphereExtractor.SoftRepairJson(
            """{"facts":[{"content":"x","entities":[{"name":"A","kind": person|place}],}]}""");
        var facts = NoosphereExtractor.ParseFacts(repaired);
        Assert.NotNull(facts);
        Assert.Single(facts!);
        Assert.Equal("person", facts[0].Entities[0].Kind);
    }

    [Fact]
    public void SoftRepairJson_FixesPrefillArrayArtifact()
    {
        var repaired = NoosphereExtractor.SoftRepairJson(
            """{["facts": [{"content":"Spectra.MLX runs locally","entities":[{"name":"Spectra.MLX","kind":"project"}],"relations":[]}]}""");
        var facts = NoosphereExtractor.ParseFacts(repaired);
        Assert.NotNull(facts);
        Assert.Single(facts!);
        Assert.Contains("Spectra", facts[0].Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SoftRepairJson_InsertsMissingCommasBetweenObjects()
    {
        var repaired = NoosphereExtractor.SoftRepairJson(
            """[{"content":"one","entities":[]}{"content":"two","entities":[]}]""");
        var facts = NoosphereExtractor.ParseFacts(repaired);
        Assert.NotNull(facts);
        Assert.Equal(2, facts!.Count);
    }

    [Fact]
    public void TryExtractJson_SalvagesCompleteFactsFromTruncatedStream()
    {
        // Mimics max_tokens cutting mid-second-fact (the sticky "no usable JSON" case on long Inscribes).
        var truncated =
            """{"facts": [ {"content": "Alice prefers dark mode", "entities":[{"name":"Alice","kind":"person"}], "relations":[]}, {"content": "JeanLaurent configured node Windows-RTX2 and JeanLaurentsMBP as sibling bridges sharing soul identity, with tunnel allowlist and local-origin middlew""";

        var json = NoosphereExtractor.TryExtractJson(truncated);
        Assert.NotNull(json);
        var facts = NoosphereExtractor.ParseFacts(json!);
        Assert.NotNull(facts);
        Assert.Single(facts!);
        Assert.Contains("Alice", facts[0].Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SalvageTruncatedFactsJson_DropsIncompleteTrailingObject()
    {
        var salvaged = NoosphereExtractor.SalvageTruncatedFactsJson(
            """{"facts":[{"content":"one","entities":[]},{"content":"two""");
        Assert.Equal("""{"facts":[{"content":"one","entities":[]}]}""", salvaged);
    }
}
