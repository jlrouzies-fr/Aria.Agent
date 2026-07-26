using Aria.Bridge.Infrastructure;
using Xunit;

namespace Aria.Tests.Bridge;

public class DebugBridgeProfileTests
{
    [Fact]
    public void Parse_FullJson_LoadsAllFields()
    {
        var json = """
            {"Label":"DEBUG-NODE-1","Platform":"Windows","Hostname":"WIN-1","FormFactor":"desktop",
             "CpuModel":"AMD Ryzen 9 7950X","CpuCores":16,"TotalRamMb":32313,
             "GpuName":"NVIDIA GeForce RTX 4090","GpuVramTotalMb":24564,"GpuVramFreeMb":18200}
            """;

        var profile = DebugBridgeProfileLoader.TryParse("Development", json);

        Assert.NotNull(profile);
        Assert.Equal("DEBUG-NODE-1", profile.Label);
        Assert.Equal("Windows", profile.Platform);
        Assert.Equal("WIN-1", profile.Hostname);
        Assert.Equal("desktop", profile.FormFactor);
        Assert.Equal("AMD Ryzen 9 7950X", profile.CpuModel);
        Assert.Equal(16, profile.CpuCores);
        Assert.Equal(32313, profile.TotalRamMb);
        Assert.Equal("NVIDIA GeForce RTX 4090", profile.GpuName);
        Assert.Equal(24564, profile.GpuVramTotalMb);
        Assert.Equal(18200, profile.GpuVramFreeMb);
    }

    [Fact]
    public void Parse_PartialJson_LeavesMissingFieldsNull()
    {
        var json = """{"Label":"DEBUG-NODE-2","Platform":"Linux"}""";

        var profile = DebugBridgeProfileLoader.TryParse("Development", json);

        Assert.NotNull(profile);
        Assert.Equal("DEBUG-NODE-2", profile.Label);
        Assert.Equal("Linux", profile.Platform);
        Assert.Null(profile.Hostname);
        Assert.Null(profile.FormFactor);
        Assert.Null(profile.CpuModel);
        Assert.Null(profile.CpuCores);
        Assert.Null(profile.TotalRamMb);
        Assert.Null(profile.GpuName);
        Assert.Null(profile.GpuVramTotalMb);
        Assert.Null(profile.GpuVramFreeMb);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsNull()
    {
        var profile = DebugBridgeProfileLoader.TryParse("Development", "not json");
        Assert.Null(profile);
    }

    [Fact]
    public void Parse_NonDevelopmentEnv_ReturnsNullEvenWithValidJson()
    {
        var json = """{"Label":"DEBUG-NODE-3"}""";

        Assert.Null(DebugBridgeProfileLoader.TryParse("Production", json));
        Assert.Null(DebugBridgeProfileLoader.TryParse("Staging", json));
        Assert.Null(DebugBridgeProfileLoader.TryParse(null, json));
    }

    [Fact]
    public void Parse_EmptyOrMissingJson_ReturnsNull()
    {
        Assert.Null(DebugBridgeProfileLoader.TryParse("Development", null));
        Assert.Null(DebugBridgeProfileLoader.TryParse("Development", ""));
        Assert.Null(DebugBridgeProfileLoader.TryParse("Development", "   "));
    }
}
