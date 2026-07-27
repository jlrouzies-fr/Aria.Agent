using System.Runtime.InteropServices;
using Aria.Bridge.Services.Metrics;
using Xunit;

namespace Aria.Tests.Bridge;

public class BridgeMetricsHardwareTests
{
    [Fact]
    public async Task GetMetricsAsync_ReportsPlatformProcessAndSystemMemory()
    {
        var collector = new BridgeMetricsCollector();
        var m = await collector.GetMetricsAsync();

        Assert.NotEqual("Unknown", m.Platform);
        Assert.True(m.ProcessMemoryMb > 0);
        Assert.True(m.SystemMemoryTotalMb is null or > 0);

        if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            // Apple silicon: unified memory — VRAM total/free mirror the system RAM figures.
            Assert.Equal(m.SystemMemoryTotalMb, m.GpuMemoryTotalMb);
            Assert.NotNull(m.GpuMemoryFreeMb);
        }
    }

    [Fact]
    public async Task HardwareInventory_ReturnsStaticSnapshot_AndCachesIt()
    {
        var inventory = new HardwareInventory(new BridgeMetricsCollector());
        var s = await inventory.GetAsync();

        Assert.False(string.IsNullOrWhiteSpace(s.Hostname));
        Assert.False(string.IsNullOrWhiteSpace(s.Os));
        Assert.False(string.IsNullOrWhiteSpace(s.Arch));
        Assert.True(s.CpuCores > 0);
        Assert.True(s.TotalRamMb is null or > 0);
        Assert.Contains(s.FormFactor, new[] { "laptop", "desktop", "unknown" });

        // Computed once per process: the second call must return the cached instance.
        Assert.Same(s, await inventory.GetAsync());
    }
}
