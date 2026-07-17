using Aria.Bridge.Endpoints;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Guards the response parsers behind POST /llm/discover-models — the channel editor's replacement for
/// manually typing model names into a textarea. Each shape is tried in turn against the target endpoint,
/// so a malformed or empty body must fall through to an empty list rather than throw.
/// </summary>
public class LlmDiscoverModelsTests
{
    [Fact]
    public void ParseOpenAiModels_ExtractsIdsSorted()
    {
        var json = """{"object":"list","data":[{"id":"gemma-3-27b"},{"id":"llama-3.1-8b"}]}""";
        var models = LlmKeyEndpoints.ParseOpenAiModels(json);
        Assert.Equal(["gemma-3-27b", "llama-3.1-8b"], models);
    }

    [Fact]
    public void ParseOpenAiModels_IgnoresEntriesWithoutId()
    {
        var json = """{"data":[{"id":"a"},{"owned_by":"local"},{"id":""}]}""";
        var models = LlmKeyEndpoints.ParseOpenAiModels(json);
        Assert.Equal(["a"], models);
    }

    [Fact]
    public void ParseOpenAiModels_MalformedOrWrongShape_ReturnsEmpty()
    {
        Assert.Empty(LlmKeyEndpoints.ParseOpenAiModels("not json"));
        Assert.Empty(LlmKeyEndpoints.ParseOpenAiModels("""{"models":[{"name":"a"}]}"""));
    }

    [Fact]
    public void ParseOllamaTags_ExtractsNamesSorted()
    {
        var json = """{"models":[{"name":"llama3:8b"},{"name":"gemma3:27b"}]}""";
        var models = LlmKeyEndpoints.ParseOllamaTags(json);
        Assert.Equal(["gemma3:27b", "llama3:8b"], models);
    }

    [Fact]
    public void ParseOllamaTags_MalformedOrWrongShape_ReturnsEmpty()
    {
        Assert.Empty(LlmKeyEndpoints.ParseOllamaTags("not json"));
        Assert.Empty(LlmKeyEndpoints.ParseOllamaTags("""{"data":[{"id":"a"}]}"""));
    }
}
