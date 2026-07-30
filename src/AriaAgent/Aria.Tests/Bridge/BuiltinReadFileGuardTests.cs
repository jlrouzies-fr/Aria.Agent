using System.Text.Json;
using Aria.Bridge;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// read_file context-window guard: with a known window, files estimated to exceed 25% of it are
/// truncated to ~200 lines and the model is guided to use range reads. With an assumed window or
/// explicit range args, behaviour is unchanged.
/// </summary>
public class BuiltinReadFileGuardTests : IDisposable
{
    private readonly string _root;

    public BuiltinReadFileGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aria-rf-guard-{Guid.NewGuid():N}");
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
    public async Task KnownWindow_SmallFile_ReturnsWholeFile()
    {
        var path = Path.Combine(_root, "small.txt");
        var lines = Enumerable.Range(1, 10).Select(i => $"line {i}");
        File.WriteAllText(path, string.Join('\n', lines));

        var r = await BuiltinTools.InvokeAsync("read_file", Args(new { path }), Policy(), contextWindow: 10_000);

        Assert.False(r.IsError);
        Assert.Contains("line 10", r.Text);
        Assert.DoesNotContain("FILE TRUNCATED", r.Text);
    }

    [Fact]
    public async Task KnownWindow_LargeFile_TruncatesAndGuidesRanges()
    {
        var path = Path.Combine(_root, "large.txt");
        // 1000 lines of ~40 chars => ~40k chars => ~10k tokens. With a known 20k window the 25%
        // budget is 5k tokens, so this file should be truncated.
        var lines = Enumerable.Range(1, 1000).Select(i => $"// this is line number {i:0000} with enough padding");
        File.WriteAllText(path, string.Join('\n', lines));

        var r = await BuiltinTools.InvokeAsync("read_file", Args(new { path }), Policy(), contextWindow: 20_000);

        Assert.False(r.IsError);
        Assert.Contains("FILE TRUNCATED", r.Text);
        Assert.Contains("exceeds 25%", r.Text);
        Assert.Contains("start_line/end_line", r.Text);
        // Should contain the first 200 lines only.
        Assert.Contains("// this is line number 0200", r.Text);
        Assert.DoesNotContain("// this is line number 0201", r.Text);
    }

    [Fact]
    public async Task AssumedWindow_LargeFile_ReturnsWholeFile()
    {
        var path = Path.Combine(_root, "large-assumed.txt");
        var lines = Enumerable.Range(1, 1000).Select(i => $"// this is line number {i:0000} with enough padding");
        File.WriteAllText(path, string.Join('\n', lines));

        var r = await BuiltinTools.InvokeAsync("read_file", Args(new { path }), Policy(), contextWindow: null);

        Assert.False(r.IsError);
        Assert.DoesNotContain("FILE TRUNCATED", r.Text);
        Assert.Contains("// this is line number 1000", r.Text);
    }

    [Fact]
    public async Task ExplicitRange_LargeFile_ReturnsRequestedRange()
    {
        var path = Path.Combine(_root, "large-range.txt");
        var lines = Enumerable.Range(1, 1000).Select(i => $"line {i}");
        File.WriteAllText(path, string.Join('\n', lines));

        var r = await BuiltinTools.InvokeAsync("read_file", Args(new { path, start_line = 900, end_line = 905 }), Policy(), contextWindow: 20_000);

        Assert.False(r.IsError);
        Assert.DoesNotContain("FILE TRUNCATED", r.Text);
        Assert.Contains("900\tline 900", r.Text);
        Assert.Contains("905\tline 905", r.Text);
    }
}
