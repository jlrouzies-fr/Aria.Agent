using System.Text.Json;
using Aria.Bridge;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Behaviour of the grep builtin: regex/substring matching, path enforcement, result caps,
/// binary-file and dependency-directory skipping.
/// </summary>
public class BuiltinGrepTests : IDisposable
{
    private readonly string _root;

    public BuiltinGrepTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aria-grep-tests-{Guid.NewGuid():N}");
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

    private string Write(string rel, string content)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task SubstringMatch_FindsLine()
    {
        Write("a.txt", "hello world\ngoodbye\n");

        var r = await BuiltinTools.InvokeAsync("grep",
            Args(new { pattern = "hello", path = _root, is_regex = false }), Policy());

        Assert.False(r.IsError);
        Assert.Contains("1 match(es) in 1 file(s)", r.Text);
        Assert.Contains("a.txt:1: hello world", r.Text);
    }

    [Fact]
    public async Task RegexMatch_FindsLine()
    {
        Write("a.cs", "void Main() {}\n");

        var r = await BuiltinTools.InvokeAsync("grep",
            Args(new { pattern = @"v\w+d\s+Main", path = _root }), Policy());

        Assert.False(r.IsError);
        Assert.Contains("void Main() {}", r.Text);
    }

    [Fact]
    public async Task InvalidRegex_ReturnsError()
    {
        var r = await BuiltinTools.InvokeAsync("grep",
            Args(new { pattern = "([", path = _root }), Policy());

        Assert.True(r.IsError);
        Assert.Contains("Invalid regex", r.Text);
    }

    [Fact]
    public async Task PathOutsideAllowedRoots_Blocked()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"aria-grep-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            var r = await BuiltinTools.InvokeAsync("grep",
                Args(new { pattern = "x", path = outside }), Policy());

            Assert.True(r.IsError);
            Assert.Contains("BLOCKED", r.Text);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public async Task IncludeGlob_FiltersFiles()
    {
        Write("a.cs", "needle\n");
        Write("a.txt", "needle\n");

        var r = await BuiltinTools.InvokeAsync("grep",
            Args(new { pattern = "needle", path = _root, include = "*.cs", is_regex = false }), Policy());

        Assert.False(r.IsError);
        Assert.Contains("a.cs", r.Text);
        Assert.DoesNotContain("a.txt", r.Text);
    }

    [Fact]
    public async Task SkippedDirs_NotSearchedByDefault_ButOverridable()
    {
        Write("node_modules/pkg/index.js", "needle\n");

        var def = await BuiltinTools.InvokeAsync("grep",
            Args(new { pattern = "needle", path = _root, is_regex = false }), Policy());
        Assert.False(def.IsError);
        Assert.Contains("No matches", def.Text);

        var incl = await BuiltinTools.InvokeAsync("grep",
            Args(new { pattern = "needle", path = _root, is_regex = false, include_ignored = true }), Policy());
        Assert.False(incl.IsError);
        Assert.Contains("index.js", incl.Text);
    }

    [Fact]
    public async Task BinaryFile_Skipped()
    {
        var bin = Path.Combine(_root, "blob.bin");
        var bytes = new byte[] { 0x41, 0x00, 0x42 }
            .Concat("needle"u8.ToArray())
            .ToArray();
        File.WriteAllBytes(bin, bytes);

        var r = await BuiltinTools.InvokeAsync("grep",
            Args(new { pattern = "needle", path = _root, is_regex = false }), Policy());

        Assert.False(r.IsError);
        Assert.Contains("No matches", r.Text);
    }

    [Fact]
    public async Task PerFileCap_Truncates()
    {
        Write("many.txt", string.Join('\n', Enumerable.Range(1, 30).Select(i => $"needle line {i}")));

        var r = await BuiltinTools.InvokeAsync("grep",
            Args(new { pattern = "needle", path = _root, is_regex = false }), Policy());

        Assert.False(r.IsError);
        Assert.Contains("20 match(es) in 1 file(s)", r.Text);
        Assert.Contains("truncated", r.Text);
    }

    [Fact]
    public async Task ContextLines_EmittedAroundMatch()
    {
        Write("ctx.txt", "l1\nl2\nneedle\nl4\nl5\n");

        var r = await BuiltinTools.InvokeAsync("grep",
            Args(new { pattern = "needle", path = _root, is_regex = false, context_lines = 1 }), Policy());

        Assert.False(r.IsError);
        Assert.Contains("ctx.txt-2- l2", r.Text);
        Assert.Contains("ctx.txt:3: needle", r.Text);
        Assert.Contains("ctx.txt-4- l4", r.Text);
        Assert.DoesNotContain("l1", r.Text);
        Assert.DoesNotContain("l5", r.Text);
    }
}
