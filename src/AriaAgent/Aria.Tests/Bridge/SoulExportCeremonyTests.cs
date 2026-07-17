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
/// Verifies the F-1 local-human-only soul export ceremony end-to-end on a minimal bridge host.
/// </summary>
public class SoulExportCeremonyTests : IDisposable
{
    private readonly WebApplicationFactory<Aria.Bridge.Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbPath;

    private readonly Action<string> _originalLauncher;

    public SoulExportCeremonyTests()
    {
        _originalLauncher = Aria.Bridge.Endpoints.SealEndpoints.LaunchSealPage;
        Aria.Bridge.Endpoints.SealEndpoints.LaunchSealPage = _ => { };

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-bridge-test-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Aria.Bridge.Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Replace the real SQLite DB with a per-test isolated file.
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
            Content = JsonContent(new { name = "Test Soul", avatarSpriteKey = (string?)null, accentColor = (string?)null })
        };
        AddLocalHeaders(msg);
        var r = await _client.SendAsync(msg);
        r.EnsureSuccessStatusCode();
    }

    private async Task<string> RequestAndApproveSealAsync(string toolName = "soul-export")
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
        var id = JsonDocument.Parse(reqBody).RootElement.GetProperty("id").GetString()!;

        var approveMsg = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/approve");
        AddLocalHeaders(approveMsg);
        var approve = await _client.SendAsync(approveMsg);
        approve.EnsureSuccessStatusCode();
        return id;
    }

    private static void AddLocalHeaders(HttpRequestMessage msg)
    {
        msg.Headers.Host = "localhost:5741";
        msg.Headers.Add("Origin", "http://localhost:5741");
    }

    private HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private HttpRequestMessage LocalExportRequest(string sealId, string passphrase)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul/export")
        {
            Content = JsonContent(new { sealId, passphrase })
        };
        msg.Headers.Host = "localhost:5741";
        msg.Headers.Add("Origin", "http://localhost:5741");
        return msg;
    }

    [Fact]
    public async Task ValidSealAndPassphrase_ReturnsEncryptedBlob()
    {
        await CreateSoulAsync();
        var sealId = await RequestAndApproveSealAsync();

        var r = await _client.SendAsync(LocalExportRequest(sealId, "strong-password-123"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var body = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("blob", out var blob));
        Assert.True(blob.GetString()?.Length > 0);
    }

    [Fact]
    public async Task MissingSeal_ReturnsBadRequest()
    {
        await CreateSoulAsync();
        var r = await _client.SendAsync(LocalExportRequest("", "strong-password-123"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task UnapprovedSeal_ReturnsForbidden()
    {
        await CreateSoulAsync();
        var reqMsg = new HttpRequestMessage(HttpMethod.Post, "/seal/request")
        {
            Content = JsonContent(new
            {
                toolName = "soul-export",
                reason = "test",
                argsPreview = "test args",
                nonceBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-nonce"))
            })
        };
        AddLocalHeaders(reqMsg);
        var req = await _client.SendAsync(reqMsg);
        req.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await req.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        var r = await _client.SendAsync(LocalExportRequest(id, "strong-password-123"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task WrongToolSeal_ReturnsForbidden()
    {
        await CreateSoulAsync();
        var sealId = await RequestAndApproveSealAsync(toolName: "some-other-tool");

        var r = await _client.SendAsync(LocalExportRequest(sealId, "strong-password-123"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task ConsumedSealCannotBeReused()
    {
        await CreateSoulAsync();
        var sealId = await RequestAndApproveSealAsync();

        var first = await _client.SendAsync(LocalExportRequest(sealId, "strong-password-123"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await _client.SendAsync(LocalExportRequest(sealId, "strong-password-123"));
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
    }

    [Theory]
    [InlineData("example.com:5741", "http://localhost:5741")]
    [InlineData("localhost:5741", "http://example.com")]
    [InlineData("localhost:5741", "https://localhost:5741")]
    public async Task NonLocalOrigin_ReturnsForbidden(string host, string origin)
    {
        await CreateSoulAsync();
        var sealId = await RequestAndApproveSealAsync();

        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul/export")
        {
            Content = JsonContent(new { sealId, passphrase = "strong-password-123" })
        };
        msg.Headers.Host = host;
        msg.Headers.Add("Origin", origin);

        var r = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }
}
