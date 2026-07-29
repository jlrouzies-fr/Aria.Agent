using System.Text.Json;
using Aria.Bridge;
using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Diff feedback (AgentTools:DiffFeedback, default on): after a successful edit_file / multi_edit /
/// write_file mutation the unified diff is appended to the model-facing result text — head-truncated
/// at the configured cap — so the model can self-verify without a re-read. Off restores the bare
/// one-line confirmation; no-op edits and cap-skipped diffs append nothing.
/// </summary>
// The knob is static state shared with BuiltinToolsPreviewTests — one collection keeps the
// toggling sequential.
[Collection("DiffFeedback knob")]
public class BuiltinDiffFeedbackTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly BridgeDbContext _db;

    public BuiltinDiffFeedbackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aria-df-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-df-tests-{Guid.NewGuid():N}.db");
        var opts = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _db = new BridgeDbContext(opts);
        _db.Database.EnsureCreated();

        BuiltinTools.ConfigureDiffFeedback(enabled: true, maxChars: 4000);
    }

    public void Dispose()
    {
        BuiltinTools.ConfigureDiffFeedback(enabled: true, maxChars: 4000); // reset the shared knob
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
    public async Task EditFile_AppendsUnifiedDiffToResultText()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "alpha one\nbeta two\ngamma three\n");

        var r = await BuiltinTools.InvokeAsync("edit_file",
            Args(new { path, old_string = "beta two", new_string = "beta TWO" }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        Assert.StartsWith($"Replaced 1 occurrence in {path}", r.Text); // confirmation line first
        Assert.Contains("--- a/a.txt", r.Text);
        Assert.Contains("+++ b/a.txt", r.Text);
        Assert.Contains("-beta two", r.Text);
        Assert.Contains("+beta TWO", r.Text);
        Assert.NotNull(r.MetadataJson); // UI metadata still rides along unchanged
    }

    [Fact]
    public async Task WriteFile_NewFile_AppendsAllAddedDiff()
    {
        var path = Path.Combine(_root, "new.txt");

        var r = await BuiltinTools.InvokeAsync("write_file",
            Args(new { path, content = "first\nsecond\n" }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        Assert.StartsWith("Wrote ", r.Text);
        Assert.Contains("--- /dev/null", r.Text); // new file diffs against empty
        Assert.Contains("+first", r.Text);
        Assert.Contains("+second", r.Text);
    }

    [Fact]
    public async Task MultiEdit_AppendsDiff()
    {
        var path = Path.Combine(_root, "m.txt");
        File.WriteAllText(path, "one two three");

        var r = await BuiltinTools.InvokeAsync("multi_edit", Args(new
        {
            path,
            edits = new[] { new { old_string = "two", new_string = "2" } },
        }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        Assert.StartsWith("Applied 1 edit(s)", r.Text);
        Assert.Contains("-one two three", r.Text);
        Assert.Contains("+one 2 three", r.Text);
    }

    [Fact]
    public async Task Truncation_HeadBiased_WithLineCountMarker()
    {
        BuiltinTools.ConfigureDiffFeedback(enabled: true, maxChars: 90);
        var path = Path.Combine(_root, "big.txt");
        var before = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}"));
        var after  = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"LINE {i}"));
        File.WriteAllText(path, before);

        var r = await BuiltinTools.InvokeAsync("edit_file",
            Args(new { path, old_string = before, new_string = after }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        Assert.Matches(@"… diff truncated \(\d+ more lines\)", r.Text);
        Assert.Contains("+LINE 1", r.Text);       // head of the diff kept
        Assert.DoesNotContain("-line 9", r.Text); // tail elided by the cap
    }

    [Fact]
    public async Task KnobOff_RestoresBareConfirmation()
    {
        BuiltinTools.ConfigureDiffFeedback(enabled: false);
        var path = Path.Combine(_root, "off.txt");
        File.WriteAllText(path, "alpha one\n");

        var r = await BuiltinTools.InvokeAsync("edit_file",
            Args(new { path, old_string = "alpha one", new_string = "alpha ONE" }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        Assert.Equal($"Replaced 1 occurrence in {path}", r.Text); // exactly today's text
        Assert.NotNull(r.MetadataJson); // knob only gates the model-facing text, not the UI card
    }

    [Fact]
    public async Task NoOpEdit_AppendsNothing()
    {
        var path = Path.Combine(_root, "noop.txt");
        File.WriteAllText(path, "same text\n");

        var r = await BuiltinTools.InvokeAsync("edit_file",
            Args(new { path, old_string = "same text", new_string = "same text" }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        Assert.Equal($"Replaced 1 occurrence in {path}", r.Text); // no hunks → no diff
    }
}
