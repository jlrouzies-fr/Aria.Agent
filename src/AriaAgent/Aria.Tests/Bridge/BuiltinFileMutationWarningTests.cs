using System.Text.Json;
using Aria.Bridge;
using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Above the 512KB pre-image cap a mutation is still applied, but no diff is computed — the
/// result metadata must say so explicitly (warning field) instead of silently omitting the diff.
/// </summary>
public class BuiltinFileMutationWarningTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly BridgeDbContext _db;

    public BuiltinFileMutationWarningTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aria-cap-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-cap-tests-{Guid.NewGuid():N}.db");
        var opts = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _db = new BridgeDbContext(opts);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Dictionary<string, JsonElement> Args(object o)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(o));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private SecurityPolicy Policy() => new(AllowedPaths: [_root]);

    [Fact]
    public async Task WriteFile_AboveDiffCap_WarnsAndStillWrites()
    {
        var path = Path.Combine(_root, "big.txt");
        File.WriteAllText(path, new string('a', DiffTools.PreImageCap + 1024));

        var r = await BuiltinTools.InvokeAsync("write_file",
            Args(new { path, content = "replaced" }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        Assert.Equal("replaced", File.ReadAllText(path)); // mutation not blocked

        Assert.NotNull(r.MetadataJson);
        using var meta = JsonDocument.Parse(r.MetadataJson!);
        Assert.Equal(JsonValueKind.Null, meta.RootElement.GetProperty("diff").ValueKind);
        var warning = meta.RootElement.GetProperty("warning").GetString();
        Assert.NotNull(warning);
        Assert.Contains("diff cap", warning!);
        Assert.Contains("no diff preview", warning!);
    }

    [Fact]
    public async Task WriteFile_BelowDiffCap_NoWarningAndDiffPresent()
    {
        var path = Path.Combine(_root, "small.txt");
        File.WriteAllText(path, "old\n");

        var r = await BuiltinTools.InvokeAsync("write_file",
            Args(new { path, content = "new\n" }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        Assert.NotNull(r.MetadataJson);
        using var meta = JsonDocument.Parse(r.MetadataJson!);
        Assert.Equal(JsonValueKind.Null, meta.RootElement.GetProperty("warning").ValueKind);
        Assert.Equal(JsonValueKind.String, meta.RootElement.GetProperty("diff").ValueKind);
    }
}
