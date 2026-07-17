using System.Net;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Verifies F-8: sensitive capability invocations are recorded in the node-side audit trail and
/// exposed via GET /audit/log.
/// </summary>
public class SecurityAuditLogTests : IDisposable
{
    private readonly WebApplicationFactory<Aria.Bridge.Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbPath;
    private readonly Action<string> _originalLauncher;

    public SecurityAuditLogTests()
    {
        _originalLauncher = SealEndpoints.LaunchSealPage;
        SealEndpoints.LaunchSealPage = _ => { };

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-audit-test-{Guid.NewGuid():N}.db");

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
        SealEndpoints.LaunchSealPage = _originalLauncher;
        _client.Dispose();
        _factory.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private async Task CreateSoulAsync()
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul")
        {
            Content = JsonContent(new { name = "Audit Test Soul", avatarSpriteKey = (string?)null, accentColor = (string?)null })
        };
        AddLocalHeaders(msg);
        (await _client.SendAsync(msg)).EnsureSuccessStatusCode();
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

    private async Task<AuditEventDto[]> GetAuditEventsAsync()
    {
        var r = await _client.GetAsync("/audit/log?limit=100");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<AuditEventDto[]>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    private static void AddLocalHeaders(HttpRequestMessage msg)
    {
        msg.Headers.Host = "localhost:5741";
        msg.Headers.Add("Origin", "http://localhost:5741");
    }

    private HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    [Fact]
    public async Task ApproveSeal_WritesAuditEvent()
    {
        await CreateSoulAsync();
        await RequestAndApproveSealAsync("soul-export");

        // The audit write is fire-and-forget; give it a moment to land.
        AuditEventDto[] events = [];
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(100);
            events = await GetAuditEventsAsync();
            if (events.Any(e => e.Action == "approved" && e.Capability == "soul-export")) break;
        }

        Assert.Contains(events, e => e.Category == "seal" && e.Action == "approved" && e.Capability == "soul-export" && e.Allowed);
    }

    [Fact]
    public async Task RejectSeal_WritesDeniedAuditEvent()
    {
        var reqMsg = new HttpRequestMessage(HttpMethod.Post, "/seal/request")
        {
            Content = JsonContent(new
            {
                toolName = "soul-export",
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

        var rejectMsg = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/reject");
        AddLocalHeaders(rejectMsg);
        (await _client.SendAsync(rejectMsg)).EnsureSuccessStatusCode();

        AuditEventDto[] events = [];
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(100);
            events = await GetAuditEventsAsync();
            if (events.Any(e => e.Action == "rejected" && e.Capability == "soul-export")) break;
        }

        Assert.Contains(events, e => e.Category == "seal" && e.Action == "rejected" && e.Capability == "soul-export" && !e.Allowed);
    }

    private sealed record AuditEventDto(int Id, DateTime Timestamp, string Category, string Action, string? Capability, string? Detail, bool Allowed);
}
