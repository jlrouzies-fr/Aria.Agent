using System.Net;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Verifies F-4a: terminal allowed-paths / blocked-commands are owned by the node and the web request
/// may only narrow them.
/// </summary>
public class TerminalNodeConfigTests : IDisposable
{
    private readonly WebApplicationFactory<Aria.Bridge.Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbPath;
    private readonly Action<string> _originalLauncher;

    public TerminalNodeConfigTests()
    {
        _originalLauncher = Aria.Bridge.Endpoints.SealEndpoints.LaunchSealPage;
        Aria.Bridge.Endpoints.SealEndpoints.LaunchSealPage = _ => { };

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-terminal-config-test-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Aria.Bridge.Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BridgeDbContext>));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddDbContext<BridgeDbContext>(opts => opts.UseSqlite($"Data Source={_dbPath}"));
                });
            });

        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        Aria.Bridge.Endpoints.SealEndpoints.LaunchSealPage = _originalLauncher;
        _client.Dispose();
        _factory.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private async Task CreateSoulAndEnableTerminalAsync()
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/soul")
        {
            Content = JsonContent(new { name = "Terminal Config Soul", avatarSpriteKey = (string?)null, accentColor = (string?)null })
        };
        AddLocalHeaders(create);
        (await _client.SendAsync(create)).EnsureSuccessStatusCode();

        var enable = new HttpRequestMessage(HttpMethod.Post, "/terminal/enable");
        AddLocalHeaders(enable);
        (await _client.SendAsync(enable)).EnsureSuccessStatusCode();
    }

    private async Task<string> RequestAndApproveSealAsync(string toolName)
    {
        var reqMsg = new HttpRequestMessage(HttpMethod.Post, "/seal/request")
        {
            Content = JsonContent(new
            {
                toolName,
                reason = "test",
                argsPreview = "test args",
                nonceBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-nonce")),
                signStatement = true
            })
        };
        AddLocalHeaders(reqMsg);
        var req = await _client.SendAsync(reqMsg);
        req.EnsureSuccessStatusCode();
        var reqBody = await req.Content.ReadAsStringAsync();
        var id = JsonDocument.Parse(reqBody).RootElement.GetProperty("id").GetString()!;

        var approveMsg = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/approve");
        AddLocalHeaders(approveMsg);
        (await _client.SendAsync(approveMsg)).EnsureSuccessStatusCode();
        return id;
    }

    private static void AddLocalHeaders(HttpRequestMessage msg)
    {
        msg.Headers.Host = "localhost:5741";
        msg.Headers.Add("Origin", "http://localhost:5741");
    }

    private HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    [Fact]
    public async Task GetConfig_InitiallyEmpty()
    {
        await CreateSoulAndEnableTerminalAsync();

        var r = await _client.GetAsync("/terminal/config");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var body = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("allowedPaths").ValueKind);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("blockedCommands").ValueKind);
        Assert.Empty(doc.RootElement.GetProperty("allowedPaths").EnumerateArray());
        Assert.Empty(doc.RootElement.GetProperty("blockedCommands").EnumerateArray());
    }

    [Fact]
    public async Task PostConfig_PersistsNodePolicy()
    {
        await CreateSoulAndEnableTerminalAsync();

        var post = new HttpRequestMessage(HttpMethod.Post, "/terminal/config")
        {
            Content = JsonContent(new
            {
                allowedPaths = new[] { "/home/user/projects" },
                blockedCommands = new[] { "npm publish", "git push --force" }
            })
        };
        AddLocalHeaders(post);
        var postR = await _client.SendAsync(post);
        Assert.Equal(HttpStatusCode.OK, postR.StatusCode);

        var get = await _client.GetAsync("/terminal/config");
        var body = await get.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var allowed = doc.RootElement.GetProperty("allowedPaths").EnumerateArray().Select(e => e.GetString()).ToArray();
        var blocked = doc.RootElement.GetProperty("blockedCommands").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "/home/user/projects" }, allowed);
        Assert.Equal(new[] { "npm publish", "git push --force" }, blocked);
    }

    [Fact]
    public async Task Exec_RespectsNodeAllowedPaths()
    {
        await CreateSoulAndEnableTerminalAsync();

        var cfg = new HttpRequestMessage(HttpMethod.Post, "/terminal/config")
        {
            Content = JsonContent(new
            {
                allowedPaths = new[] { "/tmp" },
                blockedCommands = Array.Empty<string>()
            })
        };
        AddLocalHeaders(cfg);
        (await _client.SendAsync(cfg)).EnsureSuccessStatusCode();

        // cwd outside node allowed paths should be rejected.
        var exec = new HttpRequestMessage(HttpMethod.Post, "/terminal/exec")
        {
            Content = JsonContent(new { command = "pwd", cwd = "/etc" })
        };
        AddLocalHeaders(exec);
        var r = await _client.SendAsync(exec);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("blocked", body);
    }

    [Fact]
    public async Task Exec_RespectsNodeBlockedCommands()
    {
        await CreateSoulAndEnableTerminalAsync();

        var cfg = new HttpRequestMessage(HttpMethod.Post, "/terminal/config")
        {
            Content = JsonContent(new
            {
                allowedPaths = new[] { "/tmp" },
                blockedCommands = new[] { "echo NODE_BLOCKED" }
            })
        };
        AddLocalHeaders(cfg);
        (await _client.SendAsync(cfg)).EnsureSuccessStatusCode();

        var exec = new HttpRequestMessage(HttpMethod.Post, "/terminal/exec")
        {
            Content = JsonContent(new { command = "echo NODE_BLOCKED", cwd = "/tmp" })
        };
        AddLocalHeaders(exec);
        var r = await _client.SendAsync(exec);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Exec_EmptyNodeAllowedPaths_BlocksAllPaths()
    {
        await CreateSoulAndEnableTerminalAsync();

        var cfg = new HttpRequestMessage(HttpMethod.Post, "/terminal/config")
        {
            Content = JsonContent(new
            {
                allowedPaths = Array.Empty<string>(),
                blockedCommands = Array.Empty<string>()
            })
        };
        AddLocalHeaders(cfg);
        (await _client.SendAsync(cfg)).EnsureSuccessStatusCode();

        // A compromised server cannot widen an empty node policy by sending its own paths.
        var exec = new HttpRequestMessage(HttpMethod.Post, "/terminal/exec")
        {
            Content = JsonContent(new { command = "pwd", cwd = "/tmp", allowedPaths = new[] { "/tmp" } })
        };
        AddLocalHeaders(exec);
        var r = await _client.SendAsync(exec);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task GetProjects_ReturnsBridgeAuthoritativePaths()
    {
        await CreateSoulAndEnableTerminalAsync();

        var cfg = new HttpRequestMessage(HttpMethod.Post, "/terminal/config")
        {
            Content = JsonContent(new
            {
                allowedPaths = new[] { "/home/user/projects", "/home/user/another-project" },
                blockedCommands = Array.Empty<string>()
            })
        };
        AddLocalHeaders(cfg);
        (await _client.SendAsync(cfg)).EnsureSuccessStatusCode();

        var r = await _client.GetAsync("/terminal/projects");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var body = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var projects = doc.RootElement.GetProperty("projects").EnumerateArray().ToArray();
        Assert.Equal(2, projects.Length);
        Assert.Contains(projects, p => p.GetProperty("path").GetString() == "/home/user/projects");
        Assert.Contains(projects, p => p.GetProperty("path").GetString() == "/home/user/another-project");
        Assert.All(projects, p =>
        {
            Assert.True(p.TryGetProperty("name", out _));
            Assert.True(p.TryGetProperty("platform", out _));
            Assert.True(p.TryGetProperty("nodeId", out _));
        });
    }

    [Fact]
    public async Task Exec_RequestNarrowsNodeAllowedPaths()
    {
        await CreateSoulAndEnableTerminalAsync();

        var tmpSubdir = Path.Combine(Path.GetTempPath(), $"aria-tcfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpSubdir);

        var cfg = new HttpRequestMessage(HttpMethod.Post, "/terminal/config")
        {
            Content = JsonContent(new
            {
                allowedPaths = new[] { Path.GetTempPath() },
                blockedCommands = Array.Empty<string>()
            })
        };
        AddLocalHeaders(cfg);
        (await _client.SendAsync(cfg)).EnsureSuccessStatusCode();

        // Request narrows to a subdir of the temp path; command succeeds.
        try
        {
            var exec = new HttpRequestMessage(HttpMethod.Post, "/terminal/exec")
            {
                Content = JsonContent(new { command = "pwd", cwd = tmpSubdir, allowedPaths = new[] { tmpSubdir } })
            };
            AddLocalHeaders(exec);
            var r = await _client.SendAsync(exec);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
        finally
        {
            try { Directory.Delete(tmpSubdir, true); } catch { }
        }
    }

    [Fact]
    public async Task Exec_RequestCannotWidenNodeAllowedPaths()
    {
        await CreateSoulAndEnableTerminalAsync();

        var cfg = new HttpRequestMessage(HttpMethod.Post, "/terminal/config")
        {
            Content = JsonContent(new
            {
                allowedPaths = new[] { "/tmp" },
                blockedCommands = Array.Empty<string>()
            })
        };
        AddLocalHeaders(cfg);
        (await _client.SendAsync(cfg)).EnsureSuccessStatusCode();

        // Request tries to allow /etc while node only allows /tmp -> still blocked.
        var exec = new HttpRequestMessage(HttpMethod.Post, "/terminal/exec")
        {
            Content = JsonContent(new { command = "pwd", cwd = "/etc", allowedPaths = new[] { "/etc" } })
        };
        AddLocalHeaders(exec);
        var r = await _client.SendAsync(exec);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task PtyEnable_RequiresTerminalPtySeal()
    {
        await CreateSoulAndEnableTerminalAsync();

        // A seal approved for a different capability must not unlock PTY (confused-deputy).
        var wrongSealId = await RequestAndApproveSealAsync("soul-export");
        var ptyEnable = new HttpRequestMessage(HttpMethod.Post, "/terminal/pty-enable")
        {
            Content = JsonContent(new { sealId = wrongSealId })
        };
        AddLocalHeaders(ptyEnable);
        var r = await _client.SendAsync(ptyEnable);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("terminal_pty", body);
    }
}
