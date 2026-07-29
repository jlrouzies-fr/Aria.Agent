using Aria.Harness.Context;
using Xunit;

namespace Aria.Tests.Harness;

/// <summary>
/// Guards the "/rewind" chat-command grammar: bare rewinds the most recent mutating turn and an
/// integer argument selects the nth recent turn. Pure parsing — the Blazor layer only executes the
/// parsed result.
/// </summary>
public class RewindCommandTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Bare_RewindsMostRecent(string? args)
    {
        var cmd = RewindCommand.Parse(args);
        Assert.Equal(RewindCommandKind.MostRecent, cmd.Kind);
        Assert.Equal(1, cmd.Steps);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("  5  ", 5)]
    public void Parse_PositiveInteger_SelectsNthRecent(string args, int expected)
    {
        var cmd = RewindCommand.Parse(args);
        Assert.Equal(RewindCommandKind.NthRecent, cmd.Kind);
        Assert.Equal(expected, cmd.Steps);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1 2")]
    public void Parse_Invalid_ReturnsUsageError(string args)
    {
        var cmd = RewindCommand.Parse(args);
        Assert.Equal(RewindCommandKind.Invalid, cmd.Kind);
        Assert.Equal(RewindCommand.Usage, cmd.Error);
    }
}
