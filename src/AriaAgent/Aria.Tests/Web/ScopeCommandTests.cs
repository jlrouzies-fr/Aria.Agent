using Aria.Web.Services.Chat;
using Xunit;

namespace Aria.Tests.Web;

/// <summary>Parsing of the "/scope" chat command (Wave 5). The path is the rest of the line after the
/// verb — verbatim, spaces included; the node validates and normalises it at grant time.</summary>
public class ScopeCommandTests
{
    [Fact]
    public void Bare_IsStatus()
    {
        var cmd = ScopeCommand.Parse("");
        Assert.Equal(ScopeCommandKind.Status, cmd.Kind);

        cmd = ScopeCommand.Parse("   ");
        Assert.Equal(ScopeCommandKind.Status, cmd.Kind);
    }

    [Fact]
    public void Add_ParsesPath_Verbatim()
    {
        var cmd = ScopeCommand.Parse("add /tmp/some dir/with spaces");
        Assert.Equal(ScopeCommandKind.Add, cmd.Kind);
        Assert.Equal("/tmp/some dir/with spaces", cmd.Path);
    }

    [Fact]
    public void Add_WithoutPath_IsInvalid()
    {
        var cmd = ScopeCommand.Parse("add");
        Assert.Equal(ScopeCommandKind.Invalid, cmd.Kind);
        Assert.Contains("/scope add <path>", cmd.Error);
    }

    [Fact]
    public void Remove_ParsesPath()
    {
        var cmd = ScopeCommand.Parse("remove /tmp/x");
        Assert.Equal(ScopeCommandKind.Remove, cmd.Kind);
        Assert.Equal("/tmp/x", cmd.Path);
    }

    [Fact]
    public void Remove_WithoutPath_IsInvalid()
    {
        var cmd = ScopeCommand.Parse("remove  ");
        Assert.Equal(ScopeCommandKind.Invalid, cmd.Kind);
        Assert.Contains("/scope remove <path>", cmd.Error);
    }

    [Fact]
    public void Verbs_AreCaseInsensitive()
    {
        Assert.Equal(ScopeCommandKind.Add, ScopeCommand.Parse("ADD /tmp/x").Kind);
        Assert.Equal(ScopeCommandKind.Remove, ScopeCommand.Parse("Remove /tmp/x").Kind);
    }

    [Fact]
    public void UnknownVerb_IsInvalid()
    {
        var cmd = ScopeCommand.Parse("widen /tmp/x");
        Assert.Equal(ScopeCommandKind.Invalid, cmd.Kind);
        Assert.Contains("unknown sub-command", cmd.Error);
    }
}
