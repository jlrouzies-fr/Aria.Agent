using System.Text.Json;
using Aria.Bridge;
using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// bash_exec working-directory behaviour: defaults to the first allowed project root (not the
/// bridge's own CWD), remembers a bare "cd" across calls, validates cd targets against the
/// policy, and always prefers an explicit working_dir.
/// </summary>
public class BuiltinShellCwdTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly BridgeDbContext _db;

    public BuiltinShellCwdTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aria-sh-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        // bash_exec is gated on the per-node Projects capability; enable it on a throwaway db.
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-sh-tests-{Guid.NewGuid():N}.db");
        var opts = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _db = new BridgeDbContext(opts);
        _db.Database.EnsureCreated();
        _db.Souls.Add(new BridgeSoul { Name = "test", ProjectsEnabled = true });
        _db.SaveChanges();

        BuiltinTools.ResetSessionCwd();
    }

    public void Dispose()
    {
        BuiltinTools.ResetSessionCwd();
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
    public async Task NoWorkingDir_DefaultsToFirstAllowedRoot()
    {
        // The marker exists only in the allowed root; finding it proves the process ran there
        // rather than in the bridge's own CWD or the user's home.
        File.WriteAllText(Path.Combine(_root, "cwd-marker.txt"), "x");

        var r = await BuiltinTools.InvokeAsync("bash_exec",
            Args(new { command = "ls cwd-marker.txt" }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        Assert.Contains("cwd-marker.txt", r.Text);
    }

    [Fact]
    public async Task BareCd_PersistsAcrossCalls()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "sub-marker.txt"), "x");

        var cd = await BuiltinTools.InvokeAsync("bash_exec",
            Args(new { command = "cd sub" }), Policy(), _db);
        Assert.False(cd.IsError, cd.Text);
        Assert.Contains(sub, cd.Text);

        var r = await BuiltinTools.InvokeAsync("bash_exec",
            Args(new { command = "ls sub-marker.txt" }), Policy(), _db);
        Assert.False(r.IsError, r.Text);
        Assert.Contains("sub-marker.txt", r.Text);
    }

    [Fact]
    public async Task Cd_OutsideAllowedRoots_Blocked()
    {
        var r = await BuiltinTools.InvokeAsync("bash_exec",
            Args(new { command = "cd /etc" }), Policy(), _db);

        Assert.True(r.IsError);
        Assert.Contains("BLOCKED", r.Text);
    }

    [Fact]
    public async Task ExplicitWorkingDir_WinsOverSessionCwd()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(_root, "root-marker.txt"), "x");

        var cd = await BuiltinTools.InvokeAsync("bash_exec",
            Args(new { command = "cd sub" }), Policy(), _db);
        Assert.False(cd.IsError, cd.Text);

        // root-marker.txt is not under sub/ — finding it proves working_dir overrode the session cwd.
        var r = await BuiltinTools.InvokeAsync("bash_exec",
            Args(new { command = "ls root-marker.txt", working_dir = _root }), Policy(), _db);
        Assert.False(r.IsError, r.Text);
        Assert.Contains("root-marker.txt", r.Text);
    }
}
