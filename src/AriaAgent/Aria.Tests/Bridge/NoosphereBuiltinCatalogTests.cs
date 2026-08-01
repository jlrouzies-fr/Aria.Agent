using System.Security.Cryptography;
using System.Text;
using Aria.Bridge.Services.Noosphere;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Built-in Noosphere models are pinned by SHA256 — a corrupted/swapped download must not load.
/// </summary>
public class NoosphereBuiltinCatalogTests
{
    [Fact]
    public void Catalog_HasExtractAndEmbedRoles()
    {
        Assert.NotNull(NoosphereBuiltinCatalog.Lookup("extract"));
        Assert.NotNull(NoosphereBuiltinCatalog.Lookup("embed"));
        Assert.Null(NoosphereBuiltinCatalog.Lookup("nope"));
    }

    [Fact]
    public void VerifyFileSha256_RejectsMismatch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aria-builtin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "bad.bin");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not-the-model"));
            var expected = Convert.ToHexString(SHA256.HashData("different"u8.ToArray())).ToLowerInvariant();
            Assert.False(NoosphereBuiltinRuntime.VerifyFileSha256(path, expected));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void VerifyFileSha256_AcceptsMatch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aria-builtin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "ok.bin");
            var bytes = Encoding.UTF8.GetBytes("pinned-content");
            File.WriteAllBytes(path, bytes);
            var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.True(NoosphereBuiltinRuntime.VerifyFileSha256(path, expected));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void IsRoleOnDisk_FalseWhenMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aria-builtin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var runtime = new NoosphereBuiltinRuntime(dir);
            Assert.False(runtime.IsRoleOnDisk("extract"));
            Assert.False(runtime.IsReady);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }


    [Fact]
    public void StartDownload_ExtractRequiresLicense()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aria-builtin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var runtime = new NoosphereBuiltinRuntime(dir);
            var err = runtime.StartDownload("extract", licenseAccepted: false);
            Assert.Contains("license", err, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
