using Aria.Bridge;
using Xunit;

namespace Aria.Tests.Bridge;

// Regression tests for sloppy model-emitted path arguments (observed live with gpt-oss-20b):
// a leading space made an absolute path parse as RELATIVE, silently resolving against the
// bridge's CWD and failing the allowed-paths check for a path the user had actually allowed.
public class BuiltinToolPathTests
{
    [Theory]
    [InlineData(" /tmp/spectra")]                // leading space — the live failure
    [InlineData("/tmp/spectra ")]                // trailing space
    [InlineData("  /tmp/spectra  ")]             // both
    [InlineData("\"/tmp/spectra\"")]             // wrapping double quotes
    [InlineData("'/tmp/spectra'")]               // wrapping single quotes
    [InlineData(" '/tmp/spectra' ")]             // quotes + spaces
    public void Expand_NormalizesSloppyModelPaths(string sloppy)
    {
        Assert.Equal("/tmp/spectra", BuiltinTools.Expand(sloppy));
    }

    [Fact]
    public void Expand_CleanAbsolutePath_Unchanged()
    {
        Assert.Equal("/tmp/spectra", BuiltinTools.Expand("/tmp/spectra"));
    }

    [Fact]
    public void Expand_TildeStillResolvesToHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, BuiltinTools.Expand("~"));
        Assert.Equal(Path.Combine(home, "x"), BuiltinTools.Expand("~/x"));
        Assert.Equal(Path.Combine(home, "x"), BuiltinTools.Expand(" ~/x "));
    }
}
