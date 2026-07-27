using System.Text.Json;
using Aria.Harness.Bridge;
using Aria.Harness.Governance;
using Microsoft.Extensions.AI;
using Xunit;

namespace Aria.Tests.Harness;

/// <summary>
/// Guards the fleet routing gate: with ApproveCrossNodeCalls on, a call that the multi-node
/// dispatcher resolves to a bridge OTHER than the session's default node escalates to an
/// in-chat approval; same-node calls and the gate-off case run untouched.
/// </summary>
public class FleetRoutingGateTests
{
    private const string DefaultNode = "node-default";
    private const string OtherNode   = "node-windows-rtx2";

    private static AIFunction EchoTool(string marker) =>
        AIFunctionFactory.Create((string path) => $"{marker}:{path}", name: "read_file");

    private static PathRoutedTerminalTool RoutedTool() =>
        new(
        [
            new PathRoutedTerminalTool.Candidate(EchoTool("default"), ["/home/proj-a"], DefaultNode),
            new PathRoutedTerminalTool.Candidate(EchoTool("other"),   ["/home/proj-b"], OtherNode),
        ],
        defaultIndex: 0,
        nodeLabels: new Dictionary<string, string> { [OtherNode] = "WINDOWS-RTX2" });

    private static GovernanceContext Ctx(bool fleetGate)
    {
        var ctx = new GovernanceContext(
            GovernancePolicy.FromMode(GovernanceMode.Coding) with { ApproveCrossNodeCalls = fleetGate });
        ctx.BeginTurn(null);
        return ctx;
    }

    private static AIFunctionArguments CallArgs(object o)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;
        return new AIFunctionArguments(dict.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
    }

    private static Dictionary<string, JsonElement> ArgsDict(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

    [Fact]
    public async Task CrossNodeCall_GateOn_RequiresApproval_RunsWhenApproved()
    {
        var approvals = new List<ActionDescriptor>();
        var tool = new GovernedTool(RoutedTool(), Ctx(true),
            (d, _) => { approvals.Add(d); return Task.FromResult(true); }, null, null);

        var result = await tool.InvokeAsync(CallArgs(new { path = "/home/proj-b/file.cs" }));

        var d = Assert.Single(approvals);
        Assert.Equal(ToolSeverity.NeedsApproval, d.Severity);
        Assert.Contains("WINDOWS-RTX2", d.Reason);
        Assert.Equal("other:/home/proj-b/file.cs", result?.ToString());
    }

    [Fact]
    public async Task CrossNodeCall_GateOn_Denied_DoesNotRun()
    {
        var tool = new GovernedTool(RoutedTool(), Ctx(true),
            (_, _) => Task.FromResult(false), null, null);

        var result = await tool.InvokeAsync(CallArgs(new { path = "/home/proj-b/file.cs" }));

        Assert.Contains("DENIED", result?.ToString());
    }

    [Fact]
    public async Task CrossNodeCall_GateOff_RunsWithoutApproval()
    {
        var approvals = 0;
        var tool = new GovernedTool(RoutedTool(), Ctx(false),
            (_, _) => { approvals++; return Task.FromResult(true); }, null, null);

        var result = await tool.InvokeAsync(CallArgs(new { path = "/home/proj-b/file.cs" }));

        Assert.Equal(0, approvals);
        Assert.Equal("other:/home/proj-b/file.cs", result?.ToString());
    }

    [Fact]
    public async Task DefaultNodeCall_GateOn_NoApproval()
    {
        var approvals = 0;
        var tool = new GovernedTool(RoutedTool(), Ctx(true),
            (_, _) => { approvals++; return Task.FromResult(true); }, null, null);

        var result = await tool.InvokeAsync(CallArgs(new { path = "/home/proj-a/file.cs" }));

        Assert.Equal(0, approvals);
        Assert.Equal("default:/home/proj-a/file.cs", result?.ToString());
    }

    [Fact]
    public void ResolveTargetNodeId_LongestPrefixWins_PathlessFallsToDefault()
    {
        var tool = RoutedTool();

        Assert.Equal(OtherNode,   tool.ResolveTargetNodeId(ArgsDict(new { path = "/home/proj-b/x" })));
        Assert.Equal(DefaultNode, tool.ResolveTargetNodeId(ArgsDict(new { path = "/home/proj-a/x" })));
        Assert.Equal(DefaultNode, tool.ResolveTargetNodeId(ArgsDict(new { command = "ls" })));
        Assert.Equal(DefaultNode, tool.DefaultNodeId);
        Assert.Equal("WINDOWS-RTX2", tool.DescribeNode(OtherNode));
    }
}
