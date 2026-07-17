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
/// Verifies F-5: destructive / identity / re-homing soul endpoints require a fresh, capability-bound
/// Inquisitorial Seal approved at the node, in addition to the local-origin check.
/// </summary>
public class SoulSealGatedEndpointsTests : IDisposable
{
    private readonly WebApplicationFactory<Aria.Bridge.Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbPath;
    private readonly Action<string> _originalLauncher;

    public SoulSealGatedEndpointsTests()
    {
        _originalLauncher = Aria.Bridge.Endpoints.SealEndpoints.LaunchSealPage;
        Aria.Bridge.Endpoints.SealEndpoints.LaunchSealPage = _ => { };

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-soul-seal-test-{Guid.NewGuid():N}.db");

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
        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul")
        {
            Content = JsonContent(new { name = "Seal Test Soul", avatarSpriteKey = (string?)null, accentColor = (string?)null })
        };
        AddLocalHeaders(msg);
        var r = await _client.SendAsync(msg);
        r.EnsureSuccessStatusCode();
    }

    private async Task<string> RequestSealAsync(string toolName)
    {
        var reqMsg = new HttpRequestMessage(HttpMethod.Post, "/seal/request")
        {
            Content = JsonContent(new
            {
                toolName,
                reason = "test",
                argsPreview = "test args",
                nonceBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-nonce"))
            })
        };
        AddLocalHeaders(reqMsg);
        var req = await _client.SendAsync(reqMsg);
        req.EnsureSuccessStatusCode();
        var reqBody = await req.Content.ReadAsStringAsync();
        return JsonDocument.Parse(reqBody).RootElement.GetProperty("id").GetString()!;
    }

    private async Task<string> ApproveSealAsync(string id)
    {
        var approveMsg = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/approve");
        AddLocalHeaders(approveMsg);
        var approve = await _client.SendAsync(approveMsg);
        approve.EnsureSuccessStatusCode();
        return id;
    }

    private async Task<string> RequestAndApproveSealAsync(string toolName)
    {
        var id = await RequestSealAsync(toolName);
        return await ApproveSealAsync(id);
    }

    private static void AddLocalHeaders(HttpRequestMessage msg)
    {
        msg.Headers.Host = "localhost:5741";
        msg.Headers.Add("Origin", "http://localhost:5741");
    }

    private HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    [Fact]
    public async Task LinkServer_MissingSeal_ReturnsBadRequest()
    {
        await CreateSoulAsync();
        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul/link-server")
        {
            Content = JsonContent(new { serverUrl = "http://localhost:5129" })
        };
        AddLocalHeaders(msg);
        var r = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task LinkServer_UnapprovedSeal_ReturnsForbidden()
    {
        await CreateSoulAsync();
        var sealId = await RequestSealAsync("soul-link-server");
        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul/link-server")
        {
            Content = JsonContent(new { serverUrl = "http://localhost:5129", sealId })
        };
        AddLocalHeaders(msg);
        var r = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task LinkServer_WrongToolSeal_ReturnsForbidden()
    {
        await CreateSoulAsync();
        var sealId = await RequestAndApproveSealAsync("soul-export");
        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul/link-server")
        {
            Content = JsonContent(new { serverUrl = "http://localhost:5129", sealId })
        };
        AddLocalHeaders(msg);
        var r = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Unlink_ApprovedSeal_Succeeds()
    {
        await CreateSoulAsync();
        var sealId = await RequestAndApproveSealAsync("soul-unlink");
        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul/unlink")
        {
            Content = JsonContent(new { sealId })
        };
        AddLocalHeaders(msg);
        var r = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Unlink_MissingSeal_ReturnsBadRequest()
    {
        await CreateSoulAsync();
        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul/unlink")
        {
            Content = JsonContent(new { })
        };
        AddLocalHeaders(msg);
        var r = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task SwitchServer_ApprovedSeal_ProceedsPastSealCheck()
    {
        await CreateSoulAsync();
        var sealId = await RequestAndApproveSealAsync("soul-switch-server");
        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul/switch-server")
        {
            Content = JsonContent(new { serverSoulId = "nonexistent", sealId })
        };
        AddLocalHeaders(msg);
        var r = await _client.SendAsync(msg);
        // Seal passed; endpoint now fails because the saved link does not exist.
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("Server link not found", body);
    }

    [Fact]
    public async Task RotateKey_ApprovedSeal_ProceedsPastSealCheck()
    {
        await CreateSoulAsync();
        var sealId = await RequestAndApproveSealAsync("soul-rotate-key");
        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul/rotate-key")
        {
            Content = JsonContent(new { sealId })
        };
        AddLocalHeaders(msg);
        var r = await _client.SendAsync(msg);
        // Seal passed; endpoint now fails because the soul is not linked to a server.
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("linked to a server", body);
    }

    [Fact]
    public async Task Import_ApprovedSeal_ProceedsPastSealCheck()
    {
        // Seal approval needs a soul keypair to sign, so create one first.
        // The import endpoint itself will then reject because a soul already exists.
        await CreateSoulAsync();
        var sealId = await RequestAndApproveSealAsync("soul-import");
        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul/import")
        {
            Content = JsonContent(new { passphrase = "x", blob = "x", sealId })
        };
        AddLocalHeaders(msg);
        var r = await _client.SendAsync(msg);
        var body = await r.Content.ReadAsStringAsync();
        // Seal passed; endpoint now fails because a soul already exists.
        Assert.True(r.StatusCode == HttpStatusCode.Conflict,
            $"Expected Conflict, got {r.StatusCode}: {body}");
    }

    [Fact]
    public async Task LinkServer_NonLocalOrigin_ReturnsForbidden()
    {
        await CreateSoulAsync();
        var sealId = await RequestAndApproveSealAsync("soul-link-server");
        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul/link-server")
        {
            Content = JsonContent(new { serverUrl = "http://localhost:5129", sealId })
        };
        msg.Headers.Host = "example.com:5741";
        msg.Headers.Add("Origin", "http://localhost:5741");
        var r = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }
}
