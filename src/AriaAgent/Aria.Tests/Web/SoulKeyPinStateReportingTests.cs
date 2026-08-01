using Aria.Shared;
using Aria.Web.Services.ModelBridge;
using Xunit;

namespace Aria.Tests.Web;

/// <summary>
/// A joined node that hasn't pinned the soul master key refuses sibling grants silently, which looks
/// exactly like "seals randomly stopped replicating". The node therefore reports its pin state over
/// the tunnel so the Devices panel can name the machine that needs the ceremony.
///
/// These tests fix the boundary: the report is transported and displayed, and nothing else. It is a
/// self-assertion from the node — the server cannot verify it, so no code may branch on it for trust.
/// The fail-closed check that actually protects Layer B lives on the node, in
/// <c>SiblingRoster.ResolveSoulMasterPublicKey</c>, and is covered by <c>SiblingRosterTests</c>.
/// </summary>
public class SoulKeyPinStateReportingTests
{
    private static ModelBridgeRegistry RegistryWithNode(string userId, string nodeId, string connId)
    {
        var registry = new ModelBridgeRegistry();
        registry.RegisterNode(userId, nodeId, "laptop", "Windows", connId);
        return registry;
    }

    [Fact]
    public void NewlyRegisteredNode_ReportsUnknown_UntilItSaysOtherwise()
    {
        var registry = RegistryWithNode("soul-1", "node-a", "conn-1");
        Assert.Equal(SoulKeyPinState.Unknown, registry.GetNodes("soul-1").Single().SoulKeyPinState);
    }

    [Theory]
    [InlineData(SoulKeyPinState.Ok)]
    [InlineData(SoulKeyPinState.Unpinned)]
    [InlineData(SoulKeyPinState.Mismatch)]
    public void ReportedState_IsStoredOnTheLiveConnection(string state)
    {
        var registry = RegistryWithNode("soul-1", "node-a", "conn-1");
        registry.SetNodeSoulKeyState("conn-1", state);
        Assert.Equal(state, registry.GetNodes("soul-1").Single().SoulKeyPinState);
    }

    [Fact]
    public void ReportFromAnUnknownConnection_IsIgnored()
    {
        var registry = RegistryWithNode("soul-1", "node-a", "conn-1");
        registry.SetNodeSoulKeyState("conn-other", SoulKeyPinState.Unpinned);
        Assert.Equal(SoulKeyPinState.Unknown, registry.GetNodes("soul-1").Single().SoulKeyPinState);
    }

    [Fact]
    public void ReportTouchesOnlyTheReportingNode()
    {
        var registry = RegistryWithNode("soul-1", "node-a", "conn-1");
        registry.RegisterNode("soul-1", "node-b", "desktop", "macOS", "conn-2");

        registry.SetNodeSoulKeyState("conn-1", SoulKeyPinState.Unpinned);

        var nodes = registry.GetNodes("soul-1").ToDictionary(n => n.NodeId);
        Assert.Equal(SoulKeyPinState.Unpinned, nodes["node-a"].SoulKeyPinState);
        Assert.Equal(SoulKeyPinState.Unknown,  nodes["node-b"].SoulKeyPinState);
    }

    [Fact]
    public void RepeatedIdenticalReports_DoNotRaiseNodesChanged()
    {
        // The node re-reports on every 60s knock; re-rendering the sidebar each time would be noise.
        var registry = RegistryWithNode("soul-1", "node-a", "conn-1");
        var changes  = 0;
        registry.NodesChanged += _ => changes++;

        registry.SetNodeSoulKeyState("conn-1", SoulKeyPinState.Unpinned);
        registry.SetNodeSoulKeyState("conn-1", SoulKeyPinState.Unpinned);
        registry.SetNodeSoulKeyState("conn-1", SoulKeyPinState.Unpinned);
        Assert.Equal(1, changes);

        registry.SetNodeSoulKeyState("conn-1", SoulKeyPinState.Ok);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Reconnect_ResetsToUnknown_RatherThanKeepingAStaleClaim()
    {
        var registry = RegistryWithNode("soul-1", "node-a", "conn-1");
        registry.SetNodeSoulKeyState("conn-1", SoulKeyPinState.Ok);

        registry.RegisterNode("soul-1", "node-a", "laptop", "Windows", "conn-2");

        Assert.Equal(SoulKeyPinState.Unknown, registry.GetNodes("soul-1").Single().SoulKeyPinState);
    }

    [Theory]
    [InlineData("pinned")]
    [InlineData("OK")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("<script>alert(1)</script>")]
    public void UnrecognisedReports_FoldToUnknown(string? reported)
    {
        // The hub sanitizes before storing, so a node can never widen the set of states the panel
        // renders — nor put arbitrary text where the UI expects one of four constants.
        Assert.Equal(SoulKeyPinState.Unknown, SoulKeyPinState.Sanitize(reported));
    }

    [Theory]
    [InlineData(SoulKeyPinState.Unpinned, true)]
    [InlineData(SoulKeyPinState.Mismatch, true)]
    [InlineData(SoulKeyPinState.Ok,       false)]
    [InlineData(SoulKeyPinState.Unknown,  false)]
    public void OnlyUnpinnedAndMismatch_WarnTheHuman(string state, bool expected)
    {
        // Unknown means offline or an older bridge. Warning there would train the user to ignore it.
        Assert.Equal(expected, SoulKeyPinState.NeedsAttention(state));
    }
}
