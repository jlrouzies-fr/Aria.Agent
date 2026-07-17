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
/// Verifies that the project-file and git endpoints are gated by the node-authoritative policy
/// (NodeTerminalPolicy), exactly like /terminal/exec: the node's declared Allowed Paths are the
/// maximum scope, a server-supplied request may only narrow them, and an empty node list blocks
/// every path. This closes the gap where a compromised server could send its own AllowedPaths
/// (or an empty list) to read/write/git outside the directories the node declared.
/// </summary>
public class ProjectFileNodePolicyTests : IDisposable
{
    private readonly WebApplicationFactory<Aria.Bridge.Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbPath;
    private readonly Action<string> _originalLauncher;

    public ProjectFileNodePolicyTests()
    {
        _originalLauncher = Aria.Bridge.Endpoints.SealEndpoints.LaunchSealPage;
        Aria.Bridge.Endpoints.SealEndpoints.LaunchSealPage = _ => { };

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-projfile-policy-test-{Guid.NewGuid():N}.db");

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

    private async Task CreateSoulAsync()
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/soul")
        {
            Content = JsonContent(new { name = "ProjFile Policy Soul", avatarSpriteKey = (string?)null, accentColor = (string?)null })
        };
        AddLocalHeaders(create);
        (await _client.SendAsync(create)).EnsureSuccessStatusCode();
    }

    private async Task SetNodeAllowedPathsAsync(string[] allowedPaths)
    {
        var enable = new HttpRequestMessage(HttpMethod.Post, "/terminal/enable");
        AddLocalHeaders(enable);
        (await _client.SendAsync(enable)).EnsureSuccessStatusCode();

        var projectsEnable = new HttpRequestMessage(HttpMethod.Post, "/terminal/projects-enable");
        AddLocalHeaders(projectsEnable);
        (await _client.SendAsync(projectsEnable)).EnsureSuccessStatusCode();

        var cfg = new HttpRequestMessage(HttpMethod.Post, "/terminal/config")
        {
            Content = JsonContent(new { allowedPaths, blockedCommands = Array.Empty<string>() })
        };
        AddLocalHeaders(cfg);
        (await _client.SendAsync(cfg)).EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> ReadFileAsync(string path, string[]? requestAllowedPaths)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/project-files/read")
        {
            Content = JsonContent(new { path, allowedPaths = requestAllowedPaths })
        };
        AddLocalHeaders(msg);
        return await _client.SendAsync(msg);
    }

    [Fact]
    public async Task Read_EmptyNodePaths_BlockedEvenWhenRequestClaimsWideScope()
    {
        await CreateSoulAsync();
        await SetNodeAllowedPathsAsync([]); // node declares nothing

        // A compromised server sends its own wide AllowedPaths — must NOT widen an empty node policy.
        var r = await ReadFileAsync("/etc/hosts", ["/"]);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Read_OutsideNodePaths_CannotBeWidenedByRequest()
    {
        await CreateSoulAsync();
        await SetNodeAllowedPathsAsync(["/tmp"]);

        // Node allows /tmp only; request tries to grant itself /etc -> still blocked.
        var r = await ReadFileAsync("/etc/hosts", ["/etc"]);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Read_UnderNodePaths_Succeeds()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aria-projfile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "note.txt");
        await File.WriteAllTextAsync(file, "hello from the node");

        try
        {
            await CreateSoulAsync();
            await SetNodeAllowedPathsAsync([dir]);

            var r = await ReadFileAsync(file, [dir]);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var body = await r.Content.ReadAsStringAsync();
            Assert.Contains("hello from the node", body);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Git_EmptyNodePaths_BlockedEvenWhenRequestClaimsWideScope()
    {
        await CreateSoulAsync();
        await SetNodeAllowedPathsAsync([]);

        var msg = new HttpRequestMessage(HttpMethod.Post, "/project-git/run")
        {
            Content = JsonContent(new { root = "/tmp", mode = "status", allowedPaths = new[] { "/" } })
        };
        AddLocalHeaders(msg);
        var r = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    private static void AddLocalHeaders(HttpRequestMessage msg)
    {
        msg.Headers.Host = "localhost:5741";
        msg.Headers.Add("Origin", "http://localhost:5741");
    }

    private HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
