using System.Text.Json;
using Aria.Tools;
using Xunit;

namespace Aria.Tests.Tools;

/// <summary>
/// Guards the web-search result size. An unbounded result (~34K chars was observed) floods a
/// local model's context window and makes the agent re-introduce itself; the formatter must cap it.
/// </summary>
public class WebSearchToolsTests
{
    private static JsonElement Parse(object payload) =>
        JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;

    [Fact]
    public void FormatSearchResults_BoundsLargeContent()
    {
        var big = new string('x', 50_000);
        var root = Parse(new
        {
            results = new[]
            {
                new { title = "T1", url = "https://a", content = big },
                new { title = "T2", url = "https://b", content = big },
                new { title = "T3", url = "https://c", content = big },
            }
        });

        var text = WebSearchTools.FormatSearchResults(root);

        // Three 50K bodies (~150K) must collapse well under the cap.
        Assert.True(text.Length <= WebSearchTools.MaxTotalChars + 64,
            $"Expected bounded output, got {text.Length} chars");
        Assert.Contains("[truncated]", text);
        Assert.Contains("Title: T1", text);   // titles/urls preserved
    }

    [Fact]
    public void FormatSearchResults_KeepsSmallContentIntact()
    {
        var root = Parse(new
        {
            results = new[] { new { title = "T", url = "https://x", content = "short body" } }
        });

        var text = WebSearchTools.FormatSearchResults(root);

        Assert.Equal("Title: T\nURL: https://x\nContent: short body", text);
        Assert.DoesNotContain("[truncated]", text);
    }

    [Fact]
    public void FormatSearchResults_MissingResults_ReturnsFailureMessage()
    {
        var text = WebSearchTools.FormatSearchResults(JsonDocument.Parse("{}").RootElement);
        Assert.Contains("No results found", text);
    }
}
