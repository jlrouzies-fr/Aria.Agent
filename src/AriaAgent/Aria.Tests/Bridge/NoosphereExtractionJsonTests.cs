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
    public void ParseFacts_AcceptsBareStringEntities()
    {
        var facts = NoosphereExtractor.ParseFacts(
            """{"facts":[{"content":"Spectra.MLX runs locally","entities":["Spectra.MLX","Apple Silicon"],"relations":[]}]}""");

        Assert.NotNull(facts);
        Assert.Single(facts!);
        Assert.Equal(2, facts[0].Entities.Count);
        Assert.Equal("Spectra.MLX", facts[0].Entities[0].Name);
        Assert.Null(facts[0].Entities[0].Kind);
    }

    [Fact]
    public void SoftRepairJson_QuotesBareKindEnumsAndStripsTrailingCommas()
    {
        var repaired = NoosphereExtractor.SoftRepairJson(
            """{"facts":[{"content":"x","entities":[{"name":"A","kind": person|place}],}]}""");
        var facts = NoosphereExtractor.ParseFacts(repaired);
        Assert.NotNull(facts);
        Assert.Single(facts!);
        Assert.Equal("person|place", facts[0].Entities[0].Kind);
    }
}
