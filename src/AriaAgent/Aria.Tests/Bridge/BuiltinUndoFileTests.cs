using System.Text.Json;
using Aria.Bridge;
using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// undo_file: restores the most recent FileUndo snapshot for a path (the same restore logic the
/// Explorer revert endpoint uses), records the undo itself so it can be undone in turn, and
/// refuses cleanly when there is nothing to undo.
/// </summary>
public class BuiltinUndoFileTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly BridgeDbContext _db;

    public BuiltinUndoFileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aria-undo-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-undo-tests-{Guid.NewGuid():N}.db");
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
    public async Task WriteThenUndo_RestoresOriginalContent()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "original");

        var w = await BuiltinTools.InvokeAsync("write_file",
            Args(new { path, content = "modified" }), Policy(), _db);
        Assert.False(w.IsError, w.Text);
        Assert.Equal("modified", File.ReadAllText(path));

        var u = await BuiltinTools.InvokeAsync("undo_file", Args(new { path }), Policy(), _db);
        Assert.False(u.IsError, u.Text);
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Contains("write_file", u.Text); // reports which mutation was reverted
    }

    [Fact]
    public async Task UndoOfUndo_RestoresAgain()
    {
        var path = Path.Combine(_root, "b.txt");
        File.WriteAllText(path, "original");

        await BuiltinTools.InvokeAsync("write_file",
            Args(new { path, content = "modified" }), Policy(), _db);

        var u1 = await BuiltinTools.InvokeAsync("undo_file", Args(new { path }), Policy(), _db);
        Assert.False(u1.IsError, u1.Text);
        Assert.Equal("original", File.ReadAllText(path));

        // The undo recorded its own snapshot, so a second undo walks back to "modified".
        var u2 = await BuiltinTools.InvokeAsync("undo_file", Args(new { path }), Policy(), _db);
        Assert.False(u2.IsError, u2.Text);
        Assert.Equal("modified", File.ReadAllText(path));
        Assert.Contains("undo_file", u2.Text);
    }

    [Fact]
    public async Task NoSnapshot_RefusesCleanly()
    {
        var path = Path.Combine(_root, "c.txt");
        File.WriteAllText(path, "never mutated by a tool");

        var r = await BuiltinTools.InvokeAsync("undo_file", Args(new { path }), Policy(), _db);

        Assert.True(r.IsError);
        Assert.Contains("No undo snapshot", r.Text);
        Assert.Equal("never mutated by a tool", File.ReadAllText(path));
    }

    [Fact]
    public async Task ChangedSinceMutation_RefusesToClobber()
    {
        var path = Path.Combine(_root, "d.txt");
        File.WriteAllText(path, "original");

        await BuiltinTools.InvokeAsync("write_file",
            Args(new { path, content = "modified" }), Policy(), _db);
        File.WriteAllText(path, "touched out of band"); // hash no longer matches the snapshot

        var r = await BuiltinTools.InvokeAsync("undo_file", Args(new { path }), Policy(), _db);

        Assert.True(r.IsError);
        Assert.Contains("has changed since", r.Text);
        Assert.Equal("touched out of band", File.ReadAllText(path));
    }

    [Fact]
    public async Task OutsideAllowedPaths_Blocked()
    {
        var r = await BuiltinTools.InvokeAsync("undo_file",
            Args(new { path = "/etc/passwd" }), Policy(), _db);

        Assert.True(r.IsError);
        Assert.Contains("BLOCKED", r.Text);
    }
}
