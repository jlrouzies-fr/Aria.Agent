using Aria.Harness.Context;
using Xunit;

namespace Aria.Tests.Harness;

/// <summary>
/// Guards the "/compact" chat-command grammar: bare manual compact, auto-compaction status,
/// per-session threshold override, and off. Pure parsing — the Blazor layer only executes the
/// parsed result.
/// </summary>
public class CompactCommandTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Bare_IsManualCompact(string? args)
    {
        var cmd = CompactCommand.Parse(args);
        Assert.Equal(CompactCommandKind.Manual, cmd.Kind);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("  auto  ")]
    public void Parse_Auto_ShowsStatus(string args)
    {
        var cmd = CompactCommand.Parse(args);
        Assert.Equal(CompactCommandKind.Status, cmd.Kind);
    }

    [Theory]
    [InlineData("auto 50000", 50_000)]
    [InlineData("AUTO 120000", 120_000)]
    public void Parse_AutoThreshold_SetsSessionOverride(string args, int expected)
    {
        var cmd = CompactCommand.Parse(args);
        Assert.Equal(CompactCommandKind.SetThreshold, cmd.Kind);
        Assert.Equal(expected, cmd.Threshold);
    }

    [Theory]
    [InlineData("auto off")]
    [InlineData("AUTO OFF")]
    public void Parse_AutoOff_Disables(string args)
    {
        var cmd = CompactCommand.Parse(args);
        Assert.Equal(CompactCommandKind.Disable, cmd.Kind);
    }

    [Theory]
    [InlineData("auto abc")]      // not a number
    [InlineData("auto 0")]        // must be positive ("off" is the way to disable)
    [InlineData("auto -5000")]    // negative
    [InlineData("auto 100 200")]  // one argument only
    [InlineData("bogus")]         // unknown sub-command
    [InlineData("on")]            // only "auto" is valid
    public void Parse_Invalid_ReturnsUsageError(string args)
    {
        var cmd = CompactCommand.Parse(args);
        Assert.Equal(CompactCommandKind.Invalid, cmd.Kind);
        Assert.NotNull(cmd.Error);
    }

    [Fact]
    public void Describe_Off_SaysDisabled()
    {
        var text = CompactCommand.Describe(0);
        Assert.Contains("OFF", text);
    }

    [Fact]
    public void Describe_Default_MarksThresholdAsDefault()
    {
        var text = CompactCommand.Describe(null);
        Assert.Contains($"{AutoCompaction.DefaultThresholdTokens:N0}", text);
        Assert.Contains("default", text);
    }

    [Fact]
    public void Describe_Override_ShowsSessionThreshold()
    {
        var text = CompactCommand.Describe(42_000);
        Assert.Contains($"{42_000:N0}", text);
        Assert.DoesNotContain("default", text);
    }
}
