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
    public void Catalog_HasSixExtractVariantsAndEmbed()
    {
        Assert.Equal(6, NoosphereBuiltinCatalog.ExtractVariants.Count);
        Assert.NotNull(NoosphereBuiltinCatalog.LookupExtract(NoosphereBuiltinCatalog.DefaultExtractModelId));
        Assert.True(NoosphereBuiltinCatalog.IsKnownRole("embed"));
        Assert.True(NoosphereBuiltinCatalog.IsKnownRole("extract"));
        Assert.False(NoosphereBuiltinCatalog.IsKnownExtractId("nope"));
        Assert.Contains(NoosphereBuiltinCatalog.ExtractVariants, v => v.Recommended);
        Assert.All(
            NoosphereBuiltinCatalog.ExtractVariants.Where(v => v.Id.StartsWith("lfm25-")),
            v => Assert.False(string.IsNullOrEmpty(v.WarnTip)));
    }

    [Fact]
    public void ResolveExtractId_FallsBackToDefault()
    {
        Assert.Equal(NoosphereBuiltinCatalog.DefaultExtractModelId, NoosphereBuiltinCatalog.ResolveExtractId(null));
        Assert.Equal(NoosphereBuiltinCatalog.DefaultExtractModelId, NoosphereBuiltinCatalog.ResolveExtractId("bogus"));
        Assert.Equal("lfm2-2.6b-q5km", NoosphereBuiltinCatalog.ResolveExtractId("lfm2-2.6b-q5km"));
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
            Assert.False(runtime.IsExtractOnDisk("lfm2-2.6b-q5km"));
            Assert.False(runtime.IsReady("lfm2-2.6b-q5km"));
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
            var err = runtime.StartDownload("extract", licenseAccepted: false, extractModelId: "lfm2-2.6b-q5km");
            Assert.Contains("license", err, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Status_ReportsPerVariantDiskState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aria-builtin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var runtime = new NoosphereBuiltinRuntime(dir);
            var status = runtime.Status(enabled: true, licenseAcceptedAt: DateTime.UtcNow, selectedExtractModelId: "lfm2-2.6b-q5km");
            var json = System.Text.Json.JsonSerializer.Serialize(status);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal("lfm2-2.6b-q5km", doc.RootElement.GetProperty("selectedExtractModelId").GetString());
            Assert.False(doc.RootElement.GetProperty("ready").GetBoolean());
            Assert.Equal(6, doc.RootElement.GetProperty("extractVariants").GetArrayLength());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
