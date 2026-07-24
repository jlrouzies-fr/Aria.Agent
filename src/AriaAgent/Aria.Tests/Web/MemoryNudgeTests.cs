using Aria.Web.Services.Cogitations;
using Xunit;

namespace Aria.Tests.Web;

public class MemoryNudgeTests
{
    [Theory]
    // Deferrals — the original failure case ("we'll keep working on it at a later time").
    [InlineData("alright, we'll keep working on it at a later time")]
    [InlineData("let's park this and come back to it later")]
    [InlineData("remind me to check the deploy tomorrow")]
    [InlineData("we should revisit the Hydra integration some other time")]
    // Preferences / standing rules
    [InlineData("I prefer uv over pip for python projects")]
    [InlineData("from now on, always run the tests before reporting done")]
    [InlineData("i don't like squash merges")]
    // Corrections
    [InlineData("no, actually use the dotnet build output, not the installer")]
    [InlineData("stop using the release binary for this")]
    // Explicit memory intent
    [InlineData("remember that the bridge port is 5741")]
    [InlineData("don't forget the web app uses a local sqlite db")]
    // French
    [InlineData("je préfère qu'on fasse ça plus tard")]
    [InlineData("n'oublie pas que le serveur tourne en local")]
    public void ShouldNudge_fires_on_worth_remembering_phrases(string text) =>
        Assert.True(MemoryNudge.ShouldNudge(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("later!")]                                     // below MinLength
    [InlineData("ok")]                                         // below MinLength
    [InlineData("run the tests")]                              // plain command, no trigger
    [InlineData("what time is it in Tokyo right now?")]        // question, no trigger
    [InlineData("start the server on port 8001 and wait")]     // ordinary task
    [InlineData("the build is always green on CI for this")]   // 'always' but not 'i/you always'
    public void ShouldNudge_ignores_trivial_or_unrelated_text(string? text) =>
        Assert.False(MemoryNudge.ShouldNudge(text));
}
