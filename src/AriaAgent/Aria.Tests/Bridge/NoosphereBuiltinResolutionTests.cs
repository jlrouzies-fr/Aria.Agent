using Aria.Bridge.Services.Noosphere;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Built-in is only "active" when enabled AND both roles are verified on disk. Missing models must
/// fall through to HTTP channels rather than hard-failing Inscribe.
/// </summary>
public class NoosphereBuiltinResolutionTests
{
    [Fact]
    public void Ready_RequiresBothRolesOnDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aria-builtin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var runtime = new NoosphereBuiltinRuntime(dir);
            Assert.False(runtime.IsReady);
            Assert.False(runtime.IsRoleOnDisk(NoosphereBuiltinCatalog.RoleExtract));
            Assert.False(runtime.IsRoleOnDisk(NoosphereBuiltinCatalog.RoleEmbed));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ModelIdFor_IsStableForStoredEmbeddingModel()
    {
        // Engrams store EmbeddingModel; backfill compares against this string when builtin is active.
        Assert.Equal("all-MiniLM-L6-v2", NoosphereBuiltinCatalog.ModelIdFor("embed"));
        Assert.Equal("LFM2.5-1.2B-Instruct-Q4_K_M", NoosphereBuiltinCatalog.ModelIdFor("extract"));
    }

    [Fact]
    public void UnloadModel_WhenCold_IsNoOpAndReportsNotLoaded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aria-builtin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var runtime = new NoosphereBuiltinRuntime(dir);
            Assert.False(runtime.IsRoleLoaded(NoosphereBuiltinCatalog.RoleExtract));
            Assert.False(runtime.IsRoleLoaded(NoosphereBuiltinCatalog.RoleEmbed));
            Assert.True(runtime.UnloadModel(NoosphereBuiltinCatalog.RoleExtract));
            Assert.True(runtime.UnloadModel(NoosphereBuiltinCatalog.RoleEmbed));
            Assert.False(runtime.UnloadModel("nope"));
            Assert.False(runtime.IsRoleLoaded(NoosphereBuiltinCatalog.RoleExtract));
            Assert.False(runtime.IsRoleLoaded(NoosphereBuiltinCatalog.RoleEmbed));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
