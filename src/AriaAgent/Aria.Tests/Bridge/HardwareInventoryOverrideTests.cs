using Aria.Bridge.Infrastructure;
using Aria.Bridge.Services.Metrics;
using Xunit;

namespace Aria.Tests.Bridge;

public class HardwareInventoryOverrideTests
{
    private static HardwareInventory.Snapshot Baseline() =>
        new(
            Hostname: "real-host",
            Os: "macOS",
            Arch: "Arm64",
            CpuModel: "Apple M3",
            CpuCores: 8,
            TotalRamMb: 16384,
            GpuName: "Apple M3",
            GpuVramTotalMb: 16384,
            FormFactor: "laptop");

    [Fact]
    public void ApplyOverrides_ReplacesOnlyNonNullFields()
    {
        var profile = new DebugBridgeProfile(
            Label: "DEBUG-NODE-1",
            Platform: "Windows",
            Hostname: "WIN-1",
            FormFactor: "desktop",
            CpuModel: null,
            CpuCores: 16,
            TotalRamMb: null,
            GpuName: "NVIDIA GeForce RTX 4090",
            GpuVramTotalMb: 24564,
            GpuVramFreeMb: null);

        var overridden = HardwareInventory.ApplyOverrides(Baseline(), profile);

        Assert.Equal("WIN-1", overridden.Hostname);
        Assert.Equal("desktop", overridden.FormFactor);
        Assert.Equal(16, overridden.CpuCores);
        Assert.Equal("NVIDIA GeForce RTX 4090", overridden.GpuName);
        Assert.Equal(24564, overridden.GpuVramTotalMb);

        // The profile's Platform drives the reported OS (a fake Windows node must not leak macOS).
        Assert.Equal("Windows", overridden.Os);

        // Fields not provided by the profile stay as the real probe values.
        Assert.Equal("Arm64", overridden.Arch);
        Assert.Equal("Apple M3", overridden.CpuModel);
        Assert.Equal(16384, overridden.TotalRamMb);
    }

    [Fact]
    public void ApplyOverrides_NullProfile_ReturnsSnapshotUnchanged()
    {
        var baseline = Baseline();
        Assert.Same(baseline, HardwareInventory.ApplyOverrides(baseline, null));
    }
}
