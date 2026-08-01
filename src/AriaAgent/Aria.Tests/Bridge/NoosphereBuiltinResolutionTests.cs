using Aria.Bridge.Services.Noosphere;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Built-in is only "active" when enabled AND the selected extract + embed are verified on disk.
/// Missing models must fall through to HTTP channels rather than hard-failing Inscribe.
/// </summary>
public class NoosphereBuiltinResolutionTests
{
    [Fact]
    public void Ready_RequiresSelectedExtractAndEmbedOnDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aria-builtin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var runtime = new NoosphereBuiltinRuntime(dir);
            Assert.False(runtime.IsReady("lfm2-2.6b-q5km"));
            Assert.False(runtime.IsExtractOnDisk("lfm2-2.6b-q5km"));
            Assert.False(runtime.IsEmbedOnDisk());
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
        Assert.Equal("LFM2-2.6B-Q5_K_M", NoosphereBuiltinCatalog.ModelIdFor("extract", "lfm2-2.6b-q5km"));
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
