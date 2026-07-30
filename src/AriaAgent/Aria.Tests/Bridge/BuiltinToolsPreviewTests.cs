using System.Text.Json;
using Aria.Bridge;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// BuiltinTools.Preview — the read-only core of POST /tools/preview. Runs the same validation,
/// scope enforcement and apply logic as the real mutation against an in-memory copy and returns
/// the prospective unified diff. Nothing is ever written; failures mirror the real call; any
/// other tool answers "no-preview".
/// </summary>
public class BuiltinToolsPreviewTests : IDisposable
{
    private readonly string _root;

    public BuiltinToolsPreviewTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aria-pv-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Dictionary<string, JsonElement> Args(object o)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(o));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private SecurityPolicy Policy() => new(AllowedPaths: [_root]);

    [Fact]
    public void EditFile_ReturnsProspectiveDiff_WithoutWriting()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "alpha one\nbeta two\ngamma three\n");

        var r = BuiltinTools.Preview("edit_file",
            Args(new { path, old_string = "beta two", new_string = "beta TWO" }), Policy());

        Assert.True(r.Ok, r.Reason);
        Assert.False(r.Truncated);
        Assert.Contains("--- a/a.txt", r.Diff!);
        Assert.Contains("-beta two", r.Diff!);
        Assert.Contains("+beta TWO", r.Diff!);
        Assert.Equal("alpha one\nbeta two\ngamma three\n", File.ReadAllText(path)); // untouched
    }

    [Fact]
    public void MultiEdit_AppliesSequentially_WithoutWriting()
    {
        var path = Path.Combine(_root, "seq.txt");
        File.WriteAllText(path, "start abc end");

        var r = BuiltinTools.Preview("multi_edit", Args(new
        {
            path,
            edits = new[]
            {
                new { old_string = "abc",            new_string = "xyz" },
                new { old_string = "start xyz end",  new_string = "done" },
            },
        }), Policy());

        Assert.True(r.Ok, r.Reason);
        Assert.Contains("-start abc end", r.Diff!);
        Assert.Contains("+done", r.Diff!);
        Assert.Equal("start abc end", File.ReadAllText(path));
    }

    [Fact]
    public void WriteFile_NewPath_DiffsAgainstEmpty_AndCreatesNothing()
    {
        var path = Path.Combine(_root, "brand-new.txt");

        var r = BuiltinTools.Preview("write_file",
            Args(new { path, content = "first\nsecond\n" }), Policy());

        Assert.True(r.Ok, r.Reason);
        Assert.Contains("--- /dev/null", r.Diff!); // all-added diff
        Assert.Contains("+first", r.Diff!);
        Assert.False(File.Exists(path)); // preview must not create the file
    }

    [Fact]
    public void WriteFile_ExistingPath_DiffsOldVsNew_WithoutWriting()
    {
        var path = Path.Combine(_root, "existing.txt");
        File.WriteAllText(path, "old content\n");

        var r = BuiltinTools.Preview("write_file",
            Args(new { path, content = "new content\n" }), Policy());

        Assert.True(r.Ok, r.Reason);
        Assert.Contains("-old content", r.Diff!);
        Assert.Contains("+new content", r.Diff!);
        Assert.Equal("old content\n", File.ReadAllText(path));
    }

    [Fact]
    public void OutOfScopePath_Refused_LikeTheRealCall()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"aria-pv-outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "secret");
        try
        {
            var r = BuiltinTools.Preview("edit_file",
                Args(new { path = outside, old_string = "secret", new_string = "x" }), Policy());

            Assert.False(r.Ok);
            Assert.StartsWith("BLOCKED", r.Reason!); // same refusal text the real call produces
            Assert.Null(r.Diff);
            Assert.Equal("secret", File.ReadAllText(outside));
        }
        finally
        {
            try { File.Delete(outside); } catch { }
        }
    }

    [Fact]
    public void FailingEdit_ReportsTheRealCallsError()
    {
        var path = Path.Combine(_root, "nf.txt");
        File.WriteAllText(path, "hello world\n");

        var r = BuiltinTools.Preview("edit_file",
            Args(new { path, old_string = "missing", new_string = "x" }), Policy());

        Assert.False(r.Ok);
        Assert.Contains("old_string not found", r.Reason!);
        Assert.Equal("hello world\n", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("delete_file")]
    [InlineData("bash_exec")]
    public void NonFileMutationTools_NoPreview(string toolName)
    {
        var r = BuiltinTools.Preview(toolName, Args(new { path = _root }), Policy());

        Assert.False(r.Ok);
        Assert.Equal("no-preview", r.Reason); // caller falls back to the args preview
    }

    [Fact]
    public void OversizedDiff_TruncatedFlagAndMarker()
    {
        // AsyncLocal override — see BuiltinDiffFeedbackTests.Truncation_HeadBiased.
        using var _ = BuiltinTools.PushDiffFeedback(enabled: true, maxChars: 90);
        var path = Path.Combine(_root, "big.txt");
        File.WriteAllText(path, string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}")));

        var r = BuiltinTools.Preview("write_file",
            Args(new { path, content = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"LINE {i}")) }), Policy());

        Assert.True(r.Ok, r.Reason);
        Assert.True(r.Truncated);
        Assert.Matches(@"… diff truncated \(\d+ more lines\)", r.Diff!);
        Assert.Equal(string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}")), File.ReadAllText(path));
    }
}
