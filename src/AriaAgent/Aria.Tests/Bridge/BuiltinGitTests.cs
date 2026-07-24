using System.Diagnostics;
using System.Text.Json;
using Aria.Bridge;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Agent git builtins: path enforcement against the request policy, whole-repo discard refusal,
/// and a happy-path status/stage/commit/discard flow against a real temp repository.
/// </summary>
public class BuiltinGitTests : IDisposable
{
    private readonly string _root;

    public BuiltinGitTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aria-git-tests-{Guid.NewGuid():N}");
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

    private void InitRepo()
    {
        Git(_root, "init");
        Git(_root, "config", "user.email", "tests@aria.local");
        Git(_root, "config", "user.name", "Aria Tests");
        File.WriteAllText(Path.Combine(_root, "tracked.txt"), "v1\n");
        Git(_root, "add", "tracked.txt");
        Git(_root, "commit", "-m", "init");
    }

    private static void Git(string cwd, params string[] args)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
        proc.Start();
        proc.WaitForExit(15_000);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {proc.StandardError.ReadToEnd()}");
    }

    [Fact]
    public async Task RepoPathOutsideAllowedRoots_Blocked()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"aria-git-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            foreach (var tool in new[] { "git_status", "git_diff", "git_log" })
            {
                var r = await BuiltinTools.InvokeAsync(tool, Args(new { repo_path = outside }), Policy());
                Assert.True(r.IsError, $"{tool} should fail");
                Assert.Contains("BLOCKED", r.Text);
            }

            var stage = await BuiltinTools.InvokeAsync("git_stage",
                Args(new { repo_path = outside, paths = new[] { "x.txt" } }), Policy());
            Assert.True(stage.IsError);
            Assert.Contains("BLOCKED", stage.Text);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public async Task Discard_WithoutPaths_Refused()
    {
        InitRepo();
        var r = await BuiltinTools.InvokeAsync("git_discard", Args(new { repo_path = _root }), Policy());
        Assert.True(r.IsError);
        Assert.Contains("requires a non-empty 'paths' array", r.Text);
    }

    [Fact]
    public async Task Discard_WholeRepoTarget_Refused()
    {
        InitRepo();
        var r = await BuiltinTools.InvokeAsync("git_discard",
            Args(new { repo_path = _root, paths = new[] { "." } }), Policy());
        Assert.True(r.IsError);
        Assert.Contains("requires a non-empty 'paths' array", r.Text);
    }

    [Fact]
    public async Task Status_ShowsUntrackedFile()
    {
        InitRepo();
        File.WriteAllText(Path.Combine(_root, "new.txt"), "hello\n");

        var r = await BuiltinTools.InvokeAsync("git_status", Args(new { repo_path = _root }), Policy());

        Assert.False(r.IsError);
        Assert.Contains("?? new.txt", r.Text);
    }

    [Fact]
    public async Task Diff_ShowsUnstagedChange()
    {
        InitRepo();
        File.WriteAllText(Path.Combine(_root, "tracked.txt"), "v1\nv2\n");

        var r = await BuiltinTools.InvokeAsync("git_diff", Args(new { repo_path = _root }), Policy());

        Assert.False(r.IsError);
        Assert.Contains("+v2", r.Text);
    }

    [Fact]
    public async Task StageAndCommit_WorkAndLogShowsCommit()
    {
        InitRepo();
        File.WriteAllText(Path.Combine(_root, "new.txt"), "hello\n");

        var stage = await BuiltinTools.InvokeAsync("git_stage",
            Args(new { repo_path = _root, paths = new[] { "new.txt" } }), Policy());
        Assert.False(stage.IsError);

        var commit = await BuiltinTools.InvokeAsync("git_commit",
            Args(new { repo_path = _root, message = "add new.txt" }), Policy());
        Assert.False(commit.IsError);

        var log = await BuiltinTools.InvokeAsync("git_log", Args(new { repo_path = _root }), Policy());
        Assert.False(log.IsError);
        Assert.Contains("add new.txt", log.Text);
    }

    [Fact]
    public async Task Discard_TrackedFileChange_RevertsIt()
    {
        InitRepo();
        var file = Path.Combine(_root, "tracked.txt");
        File.WriteAllText(file, "v1\nmodified\n");

        var r = await BuiltinTools.InvokeAsync("git_discard",
            Args(new { repo_path = _root, paths = new[] { "tracked.txt" } }), Policy());

        Assert.False(r.IsError);
        Assert.Equal("v1\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task Discard_UntrackedFile_DeletesIt()
    {
        InitRepo();
        var file = Path.Combine(_root, "scratch.txt");
        File.WriteAllText(file, "temp\n");

        var r = await BuiltinTools.InvokeAsync("git_discard",
            Args(new { repo_path = _root, paths = new[] { "scratch.txt" } }), Policy());

        Assert.False(r.IsError);
        Assert.False(File.Exists(file));
    }
}
