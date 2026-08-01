using System.Text.Json;
using Aria.Bridge;
using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Guards turn-checkpoint tagging on bridge file mutations: agent-originated writes stamp the
/// provided checkpoint, while legacy/manual calls without one leave the row null.
/// </summary>
public class BuiltinFileCheckpointTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly BridgeDbContext _db;

    public BuiltinFileCheckpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aria-checkpoint-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-checkpoint-tests-{Guid.NewGuid():N}.db");
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
    public async Task WriteFile_WithCheckpoint_StampsFileUndoRow()
    {
        var path = Path.Combine(_root, "a.txt");
        var checkpoint = Guid.NewGuid().ToString("N");

        var result = await BuiltinTools.InvokeAsync(
            "write_file",
            Args(new { path, content = "hello" }),
            Policy(),
            _db,
            checkpoint: checkpoint);

        Assert.False(result.IsError, result.Text);
        var undo = await _db.FileUndos.SingleAsync();
        Assert.Equal(checkpoint, undo.Checkpoint);
    }

    [Fact]
    public async Task WriteFile_WithoutCheckpoint_LeavesFileUndoRowNull()
    {
        var path = Path.Combine(_root, "b.txt");

        var result = await BuiltinTools.InvokeAsync(
            "write_file",
            Args(new { path, content = "hello" }),
            Policy(),
            _db);

        Assert.False(result.IsError, result.Text);
        var undo = await _db.FileUndos.SingleAsync();
        Assert.Null(undo.Checkpoint);
    }
}
