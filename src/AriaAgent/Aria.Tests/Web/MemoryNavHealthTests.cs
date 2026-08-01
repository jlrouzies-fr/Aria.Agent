using Aria.Web.Services.Memory;
using Xunit;

namespace Aria.Tests.Web;

/// <summary>
/// Inscribe returns before extraction finishes. When LM Studio is down on a secondary node the agent
/// moves on; the sidebar warning is what surfaces that failure. These tests lock the multi-node fold
/// so a healthy primary cannot hide a broken Windows box.
/// </summary>
public class MemoryNavHealthTests
{
    private static MemoryStatsDto Stats(
        int pending = 0, string? err = null, DateTime? at = null) =>
        new(0, 0, 0, pending, 0, true, true, err, at);

    [Fact]
    public void HealthyNodes_NoWarning()
    {
        var health = BridgeMemoryClient.AggregateNavHealth(
        [
            (Stats(), "MAC-STUDIO"),
            (Stats(pending: 2), "DESKTOP-47OJQSG"),
        ]);

        Assert.True(health.Processing);
        Assert.Null(health.ExtractionError);
    }

    [Fact]
    public void FailureOnSecondaryNode_SurfacesEvenWhenPrimaryHealthy()
    {
        var at = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var health = BridgeMemoryClient.AggregateNavHealth(
        [
            (Stats(), "MAC-STUDIO"),
            (Stats(err: "connection refused (192.168.0.122:1234)", at: at), "DESKTOP-47OJQSG"),
        ]);

        Assert.False(health.Processing);
        Assert.Equal("connection refused (192.168.0.122:1234)", health.ExtractionError);
        Assert.Equal("DESKTOP-47OJQSG", health.ErrorNodeLabel);
        Assert.Equal(at, health.ExtractionErrorAt);
    }

    [Fact]
    public void PrefersMostRecentFailureAcrossNodes()
    {
        var older = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 8, 1, 11, 0, 0, DateTimeKind.Utc);
        var health = BridgeMemoryClient.AggregateNavHealth(
        [
            (Stats(err: "old failure", at: older), "MAC-STUDIO"),
            (Stats(err: "fresh refusal", at: newer), "DESKTOP-47OJQSG"),
        ]);

        Assert.Equal("fresh refusal", health.ExtractionError);
        Assert.Equal("DESKTOP-47OJQSG", health.ErrorNodeLabel);
    }

    [Fact]
    public void NullSamples_AreIgnored()
    {
        var health = BridgeMemoryClient.AggregateNavHealth([(null, "gone"), (Stats(pending: 1), "alive")]);
        Assert.True(health.Processing);
        Assert.Null(health.ExtractionError);
    }

    [Fact]
    public void PendingAndStickyFailure_BothSurface()
    {
        // Sidebar shows gold blink (pending) and red warn (sticky error) together — AggregateNavHealth
        // must not clear Processing just because LastExtractionError is still set.
        var at = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var health = BridgeMemoryClient.AggregateNavHealth(
        [
            (Stats(pending: 1, err: "connection refused", at: at), "DESKTOP-47OJQSG"),
        ]);

        Assert.True(health.Processing);
        Assert.Equal("connection refused", health.ExtractionError);
    }

    [Fact]
    public void MemoryNodeHealth_ExposesProcessingAndErrorIndependently()
    {
        var busy = new MemoryNodeHealth("n1", "WIN", Stats(pending: 2));
        var failed = new MemoryNodeHealth("n2", "MAC", Stats(err: "refused", at: DateTime.UtcNow));
        var both = new MemoryNodeHealth("n3", "BOX", Stats(pending: 1, err: "refused", at: DateTime.UtcNow));
        var idle = new MemoryNodeHealth("n4", "IDLE", Stats());

        Assert.True(busy.Processing);
        Assert.False(busy.HasExtractionError);
        Assert.False(failed.Processing);
        Assert.True(failed.HasExtractionError);
        Assert.True(both.Processing);
        Assert.True(both.HasExtractionError);
        Assert.False(idle.Processing);
        Assert.False(idle.HasExtractionError);
    }
}
