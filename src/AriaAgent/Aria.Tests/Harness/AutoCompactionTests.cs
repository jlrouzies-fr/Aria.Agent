using Aria.Harness.Context;
using Xunit;

namespace Aria.Tests.Harness;

/// <summary>
/// Guards the auto-compaction decision: threshold crossed vs not, "/compact auto off", and a
/// reported prompt-token count taking precedence over the character-based estimate.
/// </summary>
public class AutoCompactionTests
{
    [Fact]
    public void ShouldCompact_ReportedTokensAtThreshold_Compacts()
    {
        Assert.True(AutoCompaction.ShouldCompact(
            reportedInputTokens: AutoCompaction.DefaultThresholdTokens, transcriptChars: 0, thresholdOverride: null));
    }

    [Fact]
    public void ShouldCompact_ReportedTokensBelowThreshold_DoesNotCompact()
    {
        Assert.False(AutoCompaction.ShouldCompact(
            reportedInputTokens: AutoCompaction.DefaultThresholdTokens - 1, transcriptChars: long.MaxValue / 2,
            thresholdOverride: null));
    }

    [Fact]
    public void ShouldCompact_NoReportedUsage_FallsBackToCharEstimate()
    {
        // 400_000 chars / 4 = 100_000 estimated tokens — exactly the default threshold.
        Assert.True(AutoCompaction.ShouldCompact(null, transcriptChars: 400_000, thresholdOverride: null));
        Assert.False(AutoCompaction.ShouldCompact(null, transcriptChars: 399_996, thresholdOverride: null));
    }

    [Fact]
    public void ShouldCompact_ReportedUsageWinsOverEstimate()
    {
        // Huge transcript, tiny reported prompt — the actual count decides.
        Assert.False(AutoCompaction.ShouldCompact(reportedInputTokens: 1_000, transcriptChars: 10_000_000, thresholdOverride: null));
    }

    [Fact]
    public void ShouldCompact_Off_NeverCompacts()
    {
        Assert.False(AutoCompaction.ShouldCompact(
            reportedInputTokens: int.MaxValue, transcriptChars: long.MaxValue / 2, thresholdOverride: 0));
        Assert.False(AutoCompaction.ShouldCompact(null, transcriptChars: long.MaxValue / 2, thresholdOverride: 0));
    }

    [Fact]
    public void ShouldCompact_SessionOverride_ReplacesDefault()
    {
        Assert.True(AutoCompaction.ShouldCompact(reportedInputTokens: 5_000, transcriptChars: 0, thresholdOverride: 5_000));
        Assert.False(AutoCompaction.ShouldCompact(reportedInputTokens: 5_000, transcriptChars: 0, thresholdOverride: 10_000));
    }

    [Fact]
    public void EstimateTokens_RoundsUp()
    {
        Assert.Equal(0, AutoCompaction.EstimateTokens(0));
        Assert.Equal(1, AutoCompaction.EstimateTokens(1));
        Assert.Equal(25, AutoCompaction.EstimateTokens(100));
        Assert.Equal(26, AutoCompaction.EstimateTokens(101));
    }

    [Fact]
    public void ResolveThreshold_NullIsDefault_ZeroMeansOff()
    {
        Assert.Equal(AutoCompaction.DefaultThresholdTokens, AutoCompaction.ResolveThreshold(null));
        Assert.Equal(0, AutoCompaction.ResolveThreshold(0));
        Assert.Equal(42_000, AutoCompaction.ResolveThreshold(42_000));
    }
}
