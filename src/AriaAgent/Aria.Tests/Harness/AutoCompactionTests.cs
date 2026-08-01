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

    [Fact]
    public void ResolveThreshold_KnownWindow_DerivesFromWindow()
    {
        // 128k * 0.8 = 102,400
        Assert.Equal(102_400, AutoCompaction.ResolveThreshold(null, new ContextWindow(128_000, false)));
    }

    [Fact]
    public void ResolveThreshold_KnownTinyWindow_ClampsToFloor()
    {
        Assert.Equal(AutoCompaction.MinimumDerivedThresholdTokens,
            AutoCompaction.ResolveThreshold(null, new ContextWindow(2_048, false)));
    }

    [Fact]
    public void ResolveThreshold_AssumedWindow_KeepsDefault100k()
    {
        Assert.Equal(AutoCompaction.DefaultThresholdTokens,
            AutoCompaction.ResolveThreshold(null, new ContextWindow(128_000, true)));
    }

    [Fact]
    public void ShouldCompact_KnownWindow_UsesDerivedThreshold()
    {
        // Known 10k window -> threshold 8k. Transcript of 8k*4=32k chars should trigger.
        Assert.True(AutoCompaction.ShouldCompact(null, 32_000, null, new ContextWindow(10_000, false)));
        Assert.False(AutoCompaction.ShouldCompact(null, 31_996, null, new ContextWindow(10_000, false)));
    }

    [Fact]
    public void ShouldCompact_AssumedWindow_KeepsTodayBehaviour()
    {
        // 400k chars / 4 = 100k tokens — exactly the default threshold, regardless of assumed window size.
        Assert.True(AutoCompaction.ShouldCompact(null, 400_000, null, new ContextWindow(128_000, true)));
        Assert.False(AutoCompaction.ShouldCompact(null, 399_996, null, new ContextWindow(128_000, true)));
    }
}
