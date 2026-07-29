using Aria.Harness.Context;
using Xunit;

namespace Aria.Tests.Harness;

/// <summary>
/// Pure decision/data tests for the <c>context_status</c> report: with/without reported usage,
/// with/without a session threshold override, disabled auto-compaction, and the headroom math.
/// </summary>
public class ContextStatusReportTests
{
    [Fact]
    public void NoUsage_ReportedAsNone_EstimateDrivesPressure()
    {
        var report = ContextStatusReport.Build(new ContextStatusSnapshot(
            LastInputTokens: null, TranscriptChars: 40_000, ThresholdOverride: null,
            MessageCount: 5, ToolCallCount: 7));

        Assert.Contains("none yet", report);
        Assert.Contains("~10,000", report);                       // 40,000 chars / 4
        Assert.Contains("100,000 tokens (default)", report);
        Assert.Contains("10.0% of the threshold used", report);
        Assert.Contains("estimated count", report);
        Assert.Contains("Messages this session: 5 (tool calls: 7)", report);
    }

    [Fact]
    public void ReportedUsage_TakesPrecedenceOverEstimate()
    {
        var report = ContextStatusReport.Build(new ContextStatusSnapshot(
            LastInputTokens: 25_000, TranscriptChars: 40_000, ThresholdOverride: null,
            MessageCount: 5, ToolCallCount: 0));

        Assert.Contains("Last reported input tokens: 25,000", report);
        Assert.Contains("25.0% of the threshold used (75.0% headroom)", report);
        Assert.Contains("reported count", report);
    }

    [Fact]
    public void SessionOverride_ReplacesDefaultThreshold()
    {
        var report = ContextStatusReport.Build(new ContextStatusSnapshot(
            LastInputTokens: 10_000, TranscriptChars: 0, ThresholdOverride: 20_000,
            MessageCount: 1, ToolCallCount: 0));

        Assert.Contains("20,000 tokens (session override)", report);
        Assert.Contains("50.0% of the threshold used", report);
    }

    [Fact]
    public void DisabledThreshold_ReportsOff_NoPercentage()
    {
        var report = ContextStatusReport.Build(new ContextStatusSnapshot(
            LastInputTokens: 10_000, TranscriptChars: 0, ThresholdOverride: 0,
            MessageCount: 1, ToolCallCount: 0));

        Assert.Contains("disabled", report);
        Assert.DoesNotContain("headroom", report);
    }

    [Fact]
    public void ZeroCounts_ZeroPressure()
    {
        var report = ContextStatusReport.Build(new ContextStatusSnapshot(
            null, 0, null, 0, 0));

        Assert.Contains("0.0% of the threshold used (100.0% headroom)", report);
    }

    [Fact]
    public void KnownWindow_ReportedWithUsage()
    {
        var report = ContextStatusReport.Build(new ContextStatusSnapshot(
            LastInputTokens: 10_000, TranscriptChars: 0, ThresholdOverride: null,
            MessageCount: 2, ToolCallCount: 1, Window: new ContextWindow(20_000, false)));

        Assert.Contains("Context window: 20,000 tokens (known)", report);
        Assert.Contains("Context window usage: 50.0%", report);
        Assert.Contains("Auto-compact threshold: 16,000 tokens (derived from known window)", report);
    }

    [Fact]
    public void AssumedWindow_ReportedAsAssumed()
    {
        var report = ContextStatusReport.Build(new ContextStatusSnapshot(
            LastInputTokens: 10_000, TranscriptChars: 0, ThresholdOverride: null,
            MessageCount: 2, ToolCallCount: 1, Window: new ContextWindow(100_000, true)));

        Assert.Contains("Context window: 100,000 tokens (assumed)", report);
        Assert.Contains("Auto-compact threshold: 100,000 tokens (default)", report);
    }

    [Fact]
    public void NoWindow_ReportedUnknown()
    {
        var report = ContextStatusReport.Build(new ContextStatusSnapshot(
            LastInputTokens: 5_000, TranscriptChars: 0, ThresholdOverride: null,
            MessageCount: 1, ToolCallCount: 0));

        Assert.Contains("Context window: unknown", report);
        Assert.Contains("Auto-compact threshold: 100,000 tokens (default)", report);
    }
}
