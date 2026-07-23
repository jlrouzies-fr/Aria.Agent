using Aria.Harness.Governance;
using Xunit;

namespace Aria.Tests.Harness;

/// <summary>
/// Guards the "/governance" chat-command grammar: bare status, mode switch, per-session budget
/// overrides, and reset. Pure parsing — the Blazor layer only executes the parsed result.
/// </summary>
public class GovernanceCommandTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Bare_ShowsStatus(string? args)
    {
        var cmd = GovernanceCommand.Parse(args);
        Assert.Equal(GovernanceCommandKind.Status, cmd.Kind);
    }

    [Theory]
    [InlineData("coding",   GovernanceMode.Coding)]
    [InlineData("PLAN",     GovernanceMode.Plan)]
    [InlineData("off",      GovernanceMode.Off)]
    [InlineData("Balanced", GovernanceMode.Balanced)]
    [InlineData("strict",   GovernanceMode.Strict)]
    [InlineData("paranoid", GovernanceMode.Paranoid)]
    public void Parse_ModeName_SwitchesMode(string args, GovernanceMode expected)
    {
        var cmd = GovernanceCommand.Parse(args);
        Assert.Equal(GovernanceCommandKind.SwitchMode, cmd.Kind);
        Assert.Equal(expected, cmd.Mode);
    }

    [Fact]
    public void Parse_Budget_ToolsAndReads()
    {
        var cmd = GovernanceCommand.Parse("budget tools=50 reads=30");
        Assert.Equal(GovernanceCommandKind.SetBudget, cmd.Kind);
        Assert.Equal(50, cmd.Tools);
        Assert.Equal(30, cmd.Reads);
    }

    [Fact]
    public void Parse_Budget_SingleKey_LeavesOtherNull()
    {
        var cmd = GovernanceCommand.Parse("budget reads=10");
        Assert.Equal(GovernanceCommandKind.SetBudget, cmd.Kind);
        Assert.Null(cmd.Tools);
        Assert.Equal(10, cmd.Reads);
    }

    [Theory]
    [InlineData("budget reset")]
    [InlineData("BUDGET RESET")]
    public void Parse_BudgetReset_ClearsOverrides(string args)
    {
        var cmd = GovernanceCommand.Parse(args);
        Assert.Equal(GovernanceCommandKind.ResetBudget, cmd.Kind);
    }

    [Theory]
    [InlineData("budget")]               // no keys
    [InlineData("budget tools=abc")]     // not a number
    [InlineData("budget tools=0")]       // must be positive
    [InlineData("budget calls=5")]       // unknown key
    [InlineData("bogus")]                // not a mode
    public void Parse_Invalid_ReturnsUsageError(string args)
    {
        var cmd = GovernanceCommand.Parse(args);
        Assert.Equal(GovernanceCommandKind.Invalid, cmd.Kind);
        Assert.NotNull(cmd.Error);
    }

    [Fact]
    public void Describe_OffMode_ShowsUnlimitedBudgets()
    {
        var text = GovernanceCommand.Describe(GovernancePolicy.FromMode(GovernanceMode.Off), hasOverrides: false);
        Assert.Contains("OFF", text);
        Assert.Contains("unlimited", text);
    }

    [Fact]
    public void Describe_WithOverrides_NotesThemAndShowsEffectiveNumbers()
    {
        var policy = GovernancePolicy.FromMode(GovernanceMode.Balanced).WithBudgetOverrides(50, null);
        var text = GovernanceCommand.Describe(policy, hasOverrides: true);
        Assert.Contains("50 tool calls/turn", text);
        Assert.Contains("18 file reads/turn", text);
        Assert.Contains("overrides active", text);
    }

    [Fact]
    public void Describe_PlanMode_SaysMutationsBlocked()
    {
        var text = GovernanceCommand.Describe(GovernancePolicy.FromMode(GovernanceMode.Plan), hasOverrides: false);
        Assert.Contains("mutations blocked", text);
    }
}
