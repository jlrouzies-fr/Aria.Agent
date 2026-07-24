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
/// Verifies the reactive in-chat context approval ceremony endpoints:
/// request, local approval page, approve/reject, poll, and grant creation.
/// </summary>
[Collection("BridgeApprovalCeremony")]   // shares the static pending-approval store + page launcher with ScopeApprovalEndpointsTests
public class ContextApprovalEndpointsTests : IDisposable
{
    private readonly WebApplicationFactory<Aria.Bridge.Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbPath;
    private readonly Action<string> _originalLauncher;

    public ContextApprovalEndpointsTests()
    {
        _originalLauncher = ContextEndpoints.LaunchContextPage;
        ContextEndpoints.LaunchContextPage = _ => { };

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-ctx-approval-test-{Guid.NewGuid():N}.db");

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
        ContextEndpoints.LaunchContextPage = _originalLauncher;
        _client.Dispose();
        _factory.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private async Task CreateSoulAsync()
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul")
        {
            Content = JsonContent(new { name = "Context Approval Soul", avatarSpriteKey = (string?)null, accentColor = (string?)null })
        };
        AddLocalHeaders(msg);
        var r = await _client.SendAsync(msg);
        r.EnsureSuccessStatusCode();

        // ServerSoulId is required for context grants; set it directly for tests.
        var putMsg = new HttpRequestMessage(HttpMethod.Put, "/soul")
        {
            Content = JsonContent(new { serverSoulId = $"test-soul-{Guid.NewGuid():N}" })
        };
        AddLocalHeaders(putMsg);
        var put = await _client.SendAsync(putMsg);
        put.EnsureSuccessStatusCode();
    }

    private async Task<string> RequestApprovalAsync(string? sessionId = null)
    {
        var reqMsg = new HttpRequestMessage(HttpMethod.Post, "/context/approve/request")
        {
            Content = JsonContent(new { sessionId })
        };
        AddLocalHeaders(reqMsg);
        var req = await _client.SendAsync(reqMsg);
        req.EnsureSuccessStatusCode();
        var body = await req.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
    }

    private static void AddLocalHeaders(HttpRequestMessage msg)
    {
        msg.Headers.Host = "localhost:5741";
        msg.Headers.Add("Origin", "http://localhost:5741");
    }

    private HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    [Fact]
    public async Task RequestApproval_ReturnsId()
    {
        await CreateSoulAsync();
        var id = await RequestApprovalAsync("test-session");
        Assert.False(string.IsNullOrWhiteSpace(id));
    }

    [Fact]
    public async Task GetPendingApprovalPage_ShowsApproveAndRefuseButtons()
    {
        await CreateSoulAsync();
        var id = await RequestApprovalAsync("test-session");

        var r = await _client.GetAsync($"/context/approve/{id}?session=test-session");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("AUTHORISE SENSITIVE OPERATIONS", html);
        Assert.Contains("AUTHORISE 8h", html);
        Assert.Contains("REFUSE", html);
        Assert.Contains("test-session", html);
    }

    [Fact]
    public async Task ApproveApproval_ReturnsSuccessPageAndPollsApproved()
    {
        await CreateSoulAsync();
        var id = await RequestApprovalAsync("test-session");

        var approveMsg = new HttpRequestMessage(HttpMethod.Post, $"/context/approve/{id}/approve");
        AddLocalHeaders(approveMsg);
        var approve = await _client.SendAsync(approveMsg);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var html = await approve.Content.ReadAsStringAsync();
        Assert.Contains("Context authorised", html);

        var pollMsg = new HttpRequestMessage(HttpMethod.Post, $"/context/approve/{id}/poll");
        AddLocalHeaders(pollMsg);
        var poll = await _client.SendAsync(pollMsg);
        poll.EnsureSuccessStatusCode();
        var pollBody = await poll.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(pollBody);
        Assert.Equal("approved", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ApproveApproval_CreatesSessionScopedGrant()
    {
        await CreateSoulAsync();
        var id = await RequestApprovalAsync("test-session");

        var approveMsg = new HttpRequestMessage(HttpMethod.Post, $"/context/approve/{id}/approve");
        AddLocalHeaders(approveMsg);
        await _client.SendAsync(approveMsg);

        var status = await _client.GetAsync("/context/status?session=test-session");
        status.EnsureSuccessStatusCode();
        var body = await status.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("granted").GetBoolean());
    }

    [Fact]
    public async Task RejectApproval_ReturnsRefusedPageAndPollsRejected()
    {
        await CreateSoulAsync();
        var id = await RequestApprovalAsync("test-session");

        var rejectMsg = new HttpRequestMessage(HttpMethod.Post, $"/context/approve/{id}/reject");
        AddLocalHeaders(rejectMsg);
        var reject = await _client.SendAsync(rejectMsg);
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);

        var html = await reject.Content.ReadAsStringAsync();
        Assert.Contains("Context approval refused", html);

        var pollMsg = new HttpRequestMessage(HttpMethod.Post, $"/context/approve/{id}/poll");
        AddLocalHeaders(pollMsg);
        var poll = await _client.SendAsync(pollMsg);
        poll.EnsureSuccessStatusCode();
        var pollBody = await poll.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(pollBody);
        Assert.Equal("rejected", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetUnknownApprovalPage_IsStyledNotFound()
    {
        var id = Guid.NewGuid().ToString("N");
        var r = await _client.GetAsync($"/context/approve/{id}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("not found or expired", html);
    }

    [Fact]
    public async Task ReApproveAlreadyResolvedApproval_ShowsAlreadyGranted()
    {
        await CreateSoulAsync();
        var id = await RequestApprovalAsync("test-session");

        var first = new HttpRequestMessage(HttpMethod.Post, $"/context/approve/{id}/approve");
        AddLocalHeaders(first);
        await _client.SendAsync(first);

        var second = new HttpRequestMessage(HttpMethod.Post, $"/context/approve/{id}/approve");
        AddLocalHeaders(second);
        var r = await _client.SendAsync(second);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("not found or expired", html);
    }
}
