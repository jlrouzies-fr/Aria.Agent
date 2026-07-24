using System.Text.Json;
using Aria.Bridge;
using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// multi_edit: several exact-string replacements in one call. Each old_string must be unique at
/// the moment it applies, a failure anywhere writes nothing (atomic per file), and the whole
/// batch is recorded as a single undo entry.
/// </summary>
public class BuiltinMultiEditTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly BridgeDbContext _db;

    public BuiltinMultiEditTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aria-me-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-me-tests-{Guid.NewGuid():N}.db");
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
    public async Task HappyPath_AppliesSeveralHunksInOneCall()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "alpha one\nbeta two\ngamma three\n");

        var r = await BuiltinTools.InvokeAsync("multi_edit", Args(new
        {
            path,
            edits = new[]
            {
                new { old_string = "alpha one",   new_string = "alpha ONE" },
                new { old_string = "gamma three", new_string = "gamma THREE" },
            },
        }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        Assert.Equal("alpha ONE\nbeta two\ngamma THREE\n", File.ReadAllText(path));
        Assert.NotNull(r.MetadataJson); // diff + undo token ride along like edit_file
    }

    [Fact]
    public async Task Sequential_EditCanMatchTextProducedByEarlierEdit()
    {
        var path = Path.Combine(_root, "seq.txt");
        File.WriteAllText(path, "start abc end");

        var r = await BuiltinTools.InvokeAsync("multi_edit", Args(new
        {
            path,
            edits = new[]
            {
                new { old_string = "abc", new_string = "xyz" },
                // Only exists once edit 0 has applied — sequencing must see it.
                new { old_string = "start xyz end", new_string = "done" },
            },
        }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        Assert.Equal("done", File.ReadAllText(path));
    }

    [Fact]
    public async Task NonUniqueAtApplyTime_FailsAtomically_AndReportsIndex()
    {
        var path = Path.Combine(_root, "dup.txt");
        var original = "alpha one\ndup beta dup\n";
        File.WriteAllText(path, original);

        var r = await BuiltinTools.InvokeAsync("multi_edit", Args(new
        {
            path,
            edits = new[]
            {
                new { old_string = "alpha one", new_string = "alpha changed" }, // would succeed
                new { old_string = "dup",       new_string = "x" },             // ambiguous at apply time
            },
        }), Policy(), _db);

        Assert.True(r.IsError);
        Assert.Contains("Edit 1", r.Text);       // failing index reported
        Assert.Contains("ambiguous", r.Text);
        Assert.Equal(original, File.ReadAllText(path)); // nothing written
    }

    [Fact]
    public async Task NotFound_FailsAtomically_AndReportsIndex()
    {
        var path = Path.Combine(_root, "nf.txt");
        var original = "hello world\n";
        File.WriteAllText(path, original);

        var r = await BuiltinTools.InvokeAsync("multi_edit", Args(new
        {
            path,
            edits = new[]
            {
                new { old_string = "missing text", new_string = "x" },
            },
        }), Policy(), _db);

        Assert.True(r.IsError);
        Assert.Contains("Edit 0", r.Text);
        Assert.Contains("not found", r.Text);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public async Task OutsideAllowedPaths_Blocked()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"aria-me-outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "secret");
        try
        {
            var r = await BuiltinTools.InvokeAsync("multi_edit", Args(new
            {
                path = outside,
                edits = new[] { new { old_string = "secret", new_string = "x" } },
            }), Policy(), _db);

            Assert.True(r.IsError);
            Assert.Contains("BLOCKED", r.Text);
            Assert.Equal("secret", File.ReadAllText(outside));
        }
        finally
        {
            try { File.Delete(outside); } catch { }
        }
    }

    [Fact]
    public async Task RecordsSingleUndoEntry_ForWholeBatch()
    {
        var path = Path.Combine(_root, "undo.txt");
        File.WriteAllText(path, "one two three");

        var r = await BuiltinTools.InvokeAsync("multi_edit", Args(new
        {
            path,
            edits = new[]
            {
                new { old_string = "one",   new_string = "1" },
                new { old_string = "two",   new_string = "2" },
                new { old_string = "three", new_string = "3" },
            },
        }), Policy(), _db);
        Assert.False(r.IsError, r.Text);

        var undos = _db.FileUndos.Where(u => u.Path == path).ToList();
        var undo = Assert.Single(undos);
        Assert.Equal("multi_edit", undo.ToolName);
        Assert.Equal("one two three", undo.PreContent);
    }
}
