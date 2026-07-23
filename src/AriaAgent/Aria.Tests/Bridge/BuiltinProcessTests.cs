using System.Runtime.InteropServices;
using System.Text.Json;
using Aria.Bridge;
using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Background job tracking: bash_exec(background:true) and run_background register launched jobs;
/// a foreground bash_exec that times out is converted to a background job. process_list shows the
/// live status, process_output reads the log, and process_kill stops the job — while
/// refusing any pid that isn't a tracked background job.
/// </summary>
[Collection("BuiltinBackgroundJobs")]
public class BuiltinProcessTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly BridgeDbContext _db;

    public BuiltinProcessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aria-proc-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        // bash_exec is gated on the per-node Projects capability; enable it on a throwaway db.
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-proc-tests-{Guid.NewGuid():N}.db");
        var opts = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _db = new BridgeDbContext(opts);
        _db.Database.EnsureCreated();
        _db.Souls.Add(new BridgeSoul { Name = "test", ProjectsEnabled = true });
        _db.SaveChanges();

        BuiltinTools.ResetBackgroundJobs();
    }

    public void Dispose()
    {
        BuiltinTools.ResetBackgroundJobs();
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

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string LongRunningCommand =>
        IsWindows ? "timeout /t 30 /nobreak" : "sleep 30";

    private async Task<int> LaunchBackgroundAsync(string command)
    {
        var r = await BuiltinTools.InvokeAsync("bash_exec",
            Args(new { command, working_dir = _root, background = true }), Policy(), _db);
        Assert.False(r.IsError, r.Text);
        using var doc = JsonDocument.Parse(r.Text);
        return doc.RootElement.GetProperty("pid").GetInt32();
    }

    private static async Task<bool> WaitForAsync(Func<bool> cond, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return true;
            await Task.Delay(100);
        }
        return cond();
    }

    [Fact]
    public async Task BackgroundLaunch_IsRegistered_AndListedAsRunning()
    {
        var pid = await LaunchBackgroundAsync("sleep 30");
        try
        {
            var r = await BuiltinTools.InvokeAsync("process_list", Args(new { }), Policy(), _db);
            Assert.False(r.IsError, r.Text);

            using var doc = JsonDocument.Parse(r.Text);
            var job = Assert.Single(doc.RootElement.EnumerateArray(), j => j.GetProperty("pid").GetInt32() == pid);
            Assert.Equal("sleep 30", job.GetProperty("command").GetString());
            Assert.Equal("running", job.GetProperty("status").GetString());
            Assert.Contains(".aria-bg", job.GetProperty("log_file").GetString());
        }
        finally
        {
            await BuiltinTools.InvokeAsync("process_kill", Args(new { pid }), Policy(), _db);
        }
    }

    [Fact]
    public async Task ProcessList_ReportsExitCode_OnceJobFinishes()
    {
        var pid = await LaunchBackgroundAsync("echo done");

        // The launcher wrapper writes the exit-code sidecar when the command finishes.
        var exited = await WaitForAsync(() =>
        {
            var r = BuiltinTools.InvokeAsync("process_list", Args(new { }), Policy(), _db).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(r.Text);
            var job = doc.RootElement.EnumerateArray().FirstOrDefault(j => j.GetProperty("pid").GetInt32() == pid);
            return job.ValueKind == JsonValueKind.Object && job.GetProperty("status").GetString() == "exited";
        });
        Assert.True(exited, "job did not reach exited status in time");

        var list = await BuiltinTools.InvokeAsync("process_list", Args(new { }), Policy(), _db);
        using var listDoc = JsonDocument.Parse(list.Text);
        var finished = listDoc.RootElement.EnumerateArray().First(j => j.GetProperty("pid").GetInt32() == pid);
        Assert.Equal("exited", finished.GetProperty("status").GetString());
        Assert.Equal(0, finished.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task ProcessOutput_ReturnsLogContentAndStatus()
    {
        var pid = await LaunchBackgroundAsync("echo hello-from-bg; sleep 30");
        try
        {
            string? output = null;
            var found = await WaitForAsync(() =>
            {
                var r = BuiltinTools.InvokeAsync("process_output", Args(new { pid }), Policy(), _db).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(r.Text);
                output = doc.RootElement.GetProperty("output").GetString();
                return output?.Contains("hello-from-bg") == true;
            });
            Assert.True(found, "log content did not appear in time");

            var r2 = await BuiltinTools.InvokeAsync("process_output",
                Args(new { pid, tail_lines = 50 }), Policy(), _db);
            Assert.False(r2.IsError, r2.Text);
            using var doc2 = JsonDocument.Parse(r2.Text);
            Assert.Equal("running", doc2.RootElement.GetProperty("status").GetString());
            Assert.Contains("hello-from-bg", doc2.RootElement.GetProperty("output").GetString());
            Assert.Contains(".aria-bg", doc2.RootElement.GetProperty("log_file").GetString());
        }
        finally
        {
            await BuiltinTools.InvokeAsync("process_kill", Args(new { pid }), Policy(), _db);
        }
    }

    [Fact]
    public async Task ProcessOutput_RefusesUnknownPid()
    {
        var r = await BuiltinTools.InvokeAsync("process_output", Args(new { pid = 1 }), Policy(), _db);
        Assert.True(r.IsError);
        Assert.Contains("Unknown pid", r.Text);
    }

    [Fact]
    public async Task ProcessKill_StopsTrackedJob()
    {
        var pid = await LaunchBackgroundAsync("sleep 60");

        var r = await BuiltinTools.InvokeAsync("process_kill", Args(new { pid }), Policy(), _db);
        Assert.False(r.IsError, r.Text);
        using var doc = JsonDocument.Parse(r.Text);
        Assert.Equal("stopped", doc.RootElement.GetProperty("status").GetString());

        // The process is really gone, and the registry now reports the job as stopped.
        var gone = await WaitForAsync(
            () => !System.Diagnostics.Process.GetProcesses().Any(p => p.Id == pid), 2000);
        Assert.True(gone, "process should no longer be running");

        var list = await BuiltinTools.InvokeAsync("process_list", Args(new { }), Policy(), _db);
        using var listDoc = JsonDocument.Parse(list.Text);
        var job = listDoc.RootElement.EnumerateArray().First(j => j.GetProperty("pid").GetInt32() == pid);
        Assert.Equal("stopped", job.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ProcessKill_RefusesUnregisteredPid()
    {
        // Pid 1 (launchd/init) is definitely not a background job this bridge started.
        var r = await BuiltinTools.InvokeAsync("process_kill", Args(new { pid = 1 }), Policy(), _db);
        Assert.True(r.IsError);
        Assert.Contains("Refusing to kill pid 1", r.Text);
    }

    [Fact]
    public async Task ForegroundTimeout_ConvertsToBackground()
    {
        var r = await BuiltinTools.InvokeAsync("bash_exec",
            Args(new { command = LongRunningCommand, working_dir = _root, timeout_seconds = 1 }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        using var doc = JsonDocument.Parse(r.Text);
        Assert.True(doc.RootElement.GetProperty("timed_out").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("converted_to_background").GetBoolean());

        var pid = doc.RootElement.GetProperty("pid").GetInt32();
        Assert.Contains(".aria-bg", doc.RootElement.GetProperty("log_file").GetString());
        Assert.Contains($"exceeded the 1s timeout", doc.RootElement.GetProperty("note").GetString());

        try
        {
            var list = await BuiltinTools.InvokeAsync("process_list", Args(new { }), Policy(), _db);
            Assert.False(list.IsError, list.Text);
            using var listDoc = JsonDocument.Parse(list.Text);
            Assert.Contains(listDoc.RootElement.EnumerateArray(),
                j => j.GetProperty("pid").GetInt32() == pid && j.GetProperty("status").GetString() == "running");
        }
        finally
        {
            await BuiltinTools.InvokeAsync("process_kill", Args(new { pid }), Policy(), _db);
        }
    }

    [Fact]
    public async Task RunBackground_LaunchesTrackedJob()
    {
        var r = await BuiltinTools.InvokeAsync("run_background",
            Args(new { command = LongRunningCommand, working_dir = _root }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        using var doc = JsonDocument.Parse(r.Text);
        var pid = doc.RootElement.GetProperty("pid").GetInt32();
        Assert.Contains(".aria-bg", doc.RootElement.GetProperty("log_file").GetString());
        Assert.Contains("wait_for", doc.RootElement.GetProperty("note").GetString());

        try
        {
            var list = await BuiltinTools.InvokeAsync("process_list", Args(new { }), Policy(), _db);
            Assert.False(list.IsError, list.Text);
            using var listDoc = JsonDocument.Parse(list.Text);
            Assert.Contains(listDoc.RootElement.EnumerateArray(),
                j => j.GetProperty("pid").GetInt32() == pid && j.GetProperty("command").GetString() == LongRunningCommand);
        }
        finally
        {
            await BuiltinTools.InvokeAsync("process_kill", Args(new { pid }), Policy(), _db);
        }
    }

    [Fact]
    public async Task RunBackground_BlockedCommand_IsRejected()
    {
        var policy = new SecurityPolicy(AllowedPaths: [_root], BlockedCommands: ["blocked-cmd"]);
        var r = await BuiltinTools.InvokeAsync("run_background",
            Args(new { command = "blocked-cmd --foo", working_dir = _root }), policy, _db);

        Assert.True(r.IsError);
        Assert.Contains("BLOCKED", r.Text);
    }

    [Fact]
    public async Task RunBackground_RequiresProjectsEnabled()
    {
        using var noProjectsDb = CreateDisabledProjectsDb();
        var r = await BuiltinTools.InvokeAsync("run_background",
            Args(new { command = LongRunningCommand, working_dir = _root }), Policy(), noProjectsDb);

        Assert.True(r.IsError);
        Assert.Contains("Agent Projects not enabled", r.Text);
    }

    private BridgeDbContext CreateDisabledProjectsDb()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"aria-proc-disabled-{Guid.NewGuid():N}.db");
        var opts = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var db = new BridgeDbContext(opts);
        db.Database.EnsureCreated();
        db.Souls.Add(new BridgeSoul { Name = "test", ProjectsEnabled = false });
        db.SaveChanges();
        return db;
    }
}
