using Aria.Bridge;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Inscribe used to always return a success string ("Engram committed…") even when the node's
/// extraction channel (LM Studio etc.) was down. The model then told the user the Archivum was
/// sealed while the ingest fell back to opaque raw text — or the user looked at a different
/// bridge's Noosphere and saw nothing. These tests lock the elevated failure ack.
/// </summary>
public class BuiltinMemoryInscribeAckTests
{
    private static readonly DateTime Now = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HealthyChannel_QueuesWithNodeLocality_NotCelebratorySeal()
    {
        var (text, isError) = BuiltinTools.FormatInscribeAck(null, null, "MAC-STUDIO", Now);

        Assert.False(isError);
        Assert.Contains("queued", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MAC-STUDIO", text);
        Assert.Contains("not replicated", text, StringComparison.OrdinalIgnoreCase);
        // The old string trained the model to claim success before extraction finished.
        Assert.DoesNotContain("Archivum shall preserve", text);
        Assert.DoesNotContain("Engram committed", text);
    }

    [Fact]
    public void RecentExtractionFailure_IsError_AndForbidsClaimingSuccess()
    {
        var (text, isError) = BuiltinTools.FormatInscribeAck(
            "No connection could be made because the target machine actively refused it. (192.168.0.122:1234)",
            Now.AddMinutes(-1),
            "WIN-BOX",
            Now);

        Assert.True(isError);
        Assert.Contains("INSCRIBE DEGRADED", text);
        Assert.Contains("192.168.0.122:1234", text);
        Assert.Contains("WIN-BOX", text);
        Assert.Contains("Do NOT tell the user", text);
        Assert.Contains("not replicated", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StaleExtractionFailure_DoesNotKeepInscribeBrokenForever()
    {
        // A failure from hours ago shouldn't poison every subsequent inscribe after LM Studio is back.
        var (text, isError) = BuiltinTools.FormatInscribeAck(
            "connection refused",
            Now.AddMinutes(-10),
            "MAC-STUDIO",
            Now);

        Assert.False(isError);
        Assert.DoesNotContain("INSCRIBE DEGRADED", text);
        Assert.Contains("queued", text, StringComparison.OrdinalIgnoreCase);
    }
}
