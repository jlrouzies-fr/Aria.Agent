using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aria.Bridge;
using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// wait_for builtin: port, URL, and pid+pattern readiness conditions. Port-binding tests are
/// serialized because parallel tests racing for ephemeral ports destabilise listeners.
/// </summary>
[Collection("BuiltinBackgroundJobs")]
public class BuiltinWaitForTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly BridgeDbContext _db;

    public BuiltinWaitForTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aria-wait-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-wait-tests-{Guid.NewGuid():N}.db");
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

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static (int Port, Task Done, TcpListener Listener) StartTcpServer()
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
            }
            catch (SocketException) when (attempt < 4)
            {
                continue;
            }

            var done = Task.Run(async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync();
                }
                finally
                {
                    try { listener.Stop(); } catch { }
                }
            });
            return (port, done, listener);
        }
    }

    private static (int Port, Task Done, HttpListener Listener) StartHttpServer(Func<HttpListenerContext, Task> handle)
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException) when (attempt < 4)
            {
                continue;
            }

            var done = Task.Run(async () =>
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    await handle(ctx);
                }
                finally
                {
                    try { listener.Stop(); } catch { }
                    try { listener.Close(); } catch { }
                }
            });
            return (port, done, listener);
        }
    }

    private static async Task Respond(HttpListenerContext ctx, int status, string body)
    {
        ctx.Response.StatusCode = status;
        var bytes = Encoding.UTF8.GetBytes(body);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    [Fact]
    public async Task WaitFor_Port_Success()
    {
        var (port, done, listener) = StartTcpServer();
        try
        {
            var r = await BuiltinTools.InvokeAsync("wait_for",
                Args(new { port, timeout_seconds = 5 }), Policy(), _db);

            Assert.False(r.IsError, r.Text);
            using var doc = JsonDocument.Parse(r.Text);
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("port", doc.RootElement.GetProperty("condition").GetString());
            Assert.Equal(port, doc.RootElement.GetProperty("port").GetInt32());
        }
        finally
        {
            try { listener.Stop(); } catch { }
            try { await done.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    [Fact]
    public async Task WaitFor_Url_Success()
    {
        var (port, done, listener) = StartHttpServer(async ctx => await Respond(ctx, 200, "up"));
        try
        {
            var r = await BuiltinTools.InvokeAsync("wait_for",
                Args(new { url = $"http://127.0.0.1:{port}/", timeout_seconds = 5 }), Policy(), _db);

            Assert.False(r.IsError, r.Text);
            using var doc = JsonDocument.Parse(r.Text);
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("url", doc.RootElement.GetProperty("condition").GetString());
            Assert.Equal(200, doc.RootElement.GetProperty("status_code").GetInt32());
        }
        finally
        {
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
            try { await done.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    [Fact]
    public async Task WaitFor_Url_AnyStatusCountsAsUp()
    {
        var (port, done, listener) = StartHttpServer(async ctx => await Respond(ctx, 500, "error"));
        try
        {
            var r = await BuiltinTools.InvokeAsync("wait_for",
                Args(new { url = $"http://127.0.0.1:{port}/", timeout_seconds = 5 }), Policy(), _db);

            Assert.False(r.IsError, r.Text);
            using var doc = JsonDocument.Parse(r.Text);
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(500, doc.RootElement.GetProperty("status_code").GetInt32());
        }
        finally
        {
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
            try { await done.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    [Fact]
    public async Task WaitFor_Port_Timeout()
    {
        var port = FreePort();
        var r = await BuiltinTools.InvokeAsync("wait_for",
            Args(new { port, timeout_seconds = 1 }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        using var doc = JsonDocument.Parse(r.Text);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("port", doc.RootElement.GetProperty("condition").GetString());
    }

    [Fact]
    public async Task WaitFor_InvalidArgs_Refused()
    {
        var port = FreePort();
        var r = await BuiltinTools.InvokeAsync("wait_for",
            Args(new { port, url = "http://127.0.0.1:1/", timeout_seconds = 1 }), Policy(), _db);
        Assert.True(r.IsError);
        Assert.Contains("Exactly one condition", r.Text);

        var r2 = await BuiltinTools.InvokeAsync("wait_for",
            Args(new { pid = 1, timeout_seconds = 1 }), Policy(), _db);
        Assert.True(r2.IsError);
        Assert.Contains("Exactly one condition", r2.Text);
    }

    [Fact]
    public async Task WaitFor_PidPattern_Success()
    {
        var marker = "READY-MARKER-" + Guid.NewGuid();
        var command = IsWindows
            ? $"timeout /t 1 /nobreak >nul && echo {marker}"
            : $"sleep 1 && echo {marker}";

        var bg = await BuiltinTools.InvokeAsync("run_background",
            Args(new { command, working_dir = _root }), Policy(), _db);
        Assert.False(bg.IsError, bg.Text);
        using var bgDoc = JsonDocument.Parse(bg.Text);
        var pid = bgDoc.RootElement.GetProperty("pid").GetInt32();

        try
        {
            var r = await BuiltinTools.InvokeAsync("wait_for",
                Args(new { pid, pattern = Regex.Escape(marker), timeout_seconds = 10 }), Policy(), _db);

            Assert.False(r.IsError, r.Text);
            using var doc = JsonDocument.Parse(r.Text);
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("pid_pattern", doc.RootElement.GetProperty("condition").GetString());
            Assert.Contains(marker, doc.RootElement.GetProperty("matched_line").GetString());
        }
        finally
        {
            await BuiltinTools.InvokeAsync("process_kill", Args(new { pid }), Policy(), _db);
        }
    }

    [Fact]
    public async Task WaitFor_PidPattern_Timeout()
    {
        var bg = await BuiltinTools.InvokeAsync("run_background",
            Args(new { command = LongRunningCommand, working_dir = _root }), Policy(), _db);
        Assert.False(bg.IsError, bg.Text);
        using var bgDoc = JsonDocument.Parse(bg.Text);
        var pid = bgDoc.RootElement.GetProperty("pid").GetInt32();

        try
        {
            var r = await BuiltinTools.InvokeAsync("wait_for",
                Args(new { pid, pattern = "NEVER_MATCHES", timeout_seconds = 1 }), Policy(), _db);

            Assert.False(r.IsError, r.Text);
            using var doc = JsonDocument.Parse(r.Text);
            Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("pid_pattern", doc.RootElement.GetProperty("condition").GetString());
        }
        finally
        {
            await BuiltinTools.InvokeAsync("process_kill", Args(new { pid }), Policy(), _db);
        }
    }

    [Fact]
    public async Task WaitFor_UnknownPid_Refused()
    {
        var r = await BuiltinTools.InvokeAsync("wait_for",
            Args(new { pid = 1, pattern = "x", timeout_seconds = 1 }), Policy(), _db);
        Assert.True(r.IsError);
        Assert.Contains("Unknown pid", r.Text);
    }
}
