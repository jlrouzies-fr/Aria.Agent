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
/// Verifies the Wave 5 "/scope" ceremony end-to-end on the bridge: the server can only ASK
/// (POST /context/approve/request with kind="scope"), the human approves on the local page, the node
/// mints a signed path grant, and the granted path then passes node-side path enforcement — but only
/// for requests carrying that session's stamp.
/// </summary>
[Collection("BridgeApprovalCeremony")]
public class ScopeApprovalEndpointsTests : IDisposable
{
    private readonly WebApplicationFactory<Aria.Bridge.Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbPath;
    private readonly Action<string> _originalLauncher;

    public ScopeApprovalEndpointsTests()
    {
        _originalLauncher = ContextEndpoints.LaunchContextPage;
        ContextEndpoints.LaunchContextPage = _ => { };

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-scope-approval-test-{Guid.NewGuid():N}.db");

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

    private static string NewDir(string name) =>
        Path.Combine(Path.GetTempPath(), $"aria-scope-{name}-{Guid.NewGuid():N}");

    private async Task CreateSoulAsync()
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/soul")
        {
            Content = JsonContent(new { name = "Scope Approval Soul", avatarSpriteKey = (string?)null, accentColor = (string?)null })
        };
        AddLocalHeaders(msg);
        var r = await _client.SendAsync(msg);
        r.EnsureSuccessStatusCode();

        // ServerSoulId is required for grants; set it directly for tests.
        var putMsg = new HttpRequestMessage(HttpMethod.Put, "/soul")
        {
            Content = JsonContent(new { serverSoulId = $"test-soul-{Guid.NewGuid():N}" })
        };
        AddLocalHeaders(putMsg);
        var put = await _client.SendAsync(putMsg);
        put.EnsureSuccessStatusCode();
    }

    // Turns the Projects capability on and declares one allowed path — direct DB write, as PUT /soul
    // does not expose Terminal config.
    private async Task ConfigureTerminalAsync(string declaredPath)
    {
        var opts = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        await using var db = new BridgeDbContext(opts);
        var soul = await db.Souls.FirstAsync();
        soul.ProjectsEnabled = true;
        soul.TerminalAllowedPathsJson = JsonSerializer.Serialize(new[] { declaredPath });
        await db.SaveChangesAsync();
    }

    private async Task<string?> RequestScopeAsync(string? sessionId, string? path)
    {
        var reqMsg = new HttpRequestMessage(HttpMethod.Post, "/context/approve/request")
        {
            Content = JsonContent(new { sessionId, kind = "scope", path })
        };
        AddLocalHeaders(reqMsg);
        var req = await _client.SendAsync(reqMsg);
        req.EnsureSuccessStatusCode();
        var body = await req.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(body).RootElement;
        return root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
    }

    private async Task<JsonElement> PostJsonAsync(string url, object? body = null, string? sessionHeader = null)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, url);
        if (body != null) msg.Content = JsonContent(body);
        msg.Headers.Accept.ParseAdd("application/json");
        if (sessionHeader != null) msg.Headers.Add("X-Aria-Session", sessionHeader);
        AddLocalHeaders(msg);
        var resp = await _client.SendAsync(msg);
        var text = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement;
    }

    private static void AddLocalHeaders(HttpRequestMessage msg)
    {
        msg.Headers.Host = "localhost:5741";
        msg.Headers.Add("Origin", "http://localhost:5741");
    }

    private HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    [Fact]
    public async Task RequestScope_ReturnsId_AndPageShowsPath()
    {
        await CreateSoulAsync();
        var dir = NewDir("page");
        var id = await RequestScopeAsync("sess-1", dir);

        Assert.False(string.IsNullOrWhiteSpace(id));

        var r = await _client.GetAsync($"/context/approve/{id}?session=sess-1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("AUTHORISE PATH EXPANSION", html);
        Assert.Contains(dir, html);
        Assert.Contains("sess-1", html);
        Assert.Contains("REFUSE", html);
    }

    [Fact]
    public async Task ApproveScope_MintsVerifiableGrant()
    {
        await CreateSoulAsync();
        var dir = NewDir("mint");
        var id = await RequestScopeAsync("sess-1", dir);
        Assert.False(string.IsNullOrWhiteSpace(id));

        var approve = await PostJsonAsync($"/context/approve/{id}/approve");
        Assert.True(approve.GetProperty("ok").GetBoolean());

        var poll = await PostJsonAsync($"/context/approve/{id}/poll");
        Assert.Equal("approved", poll.GetProperty("status").GetString());

        var status = await _client.GetAsync($"/scope/status?session=sess-1&path={Uri.EscapeDataString(dir)}");
        var statusBody = JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement;
        Assert.True(statusBody.GetProperty("granted").GetBoolean());

        var list = await _client.GetAsync("/scope/list?session=sess-1");
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.Contains(dir, listBody);

        // … and only for that session.
        var other = await _client.GetAsync($"/scope/status?session=sess-2&path={Uri.EscapeDataString(dir)}");
        var otherBody = JsonDocument.Parse(await other.Content.ReadAsStringAsync()).RootElement;
        Assert.False(otherBody.GetProperty("granted").GetBoolean());
    }

    [Fact]
    public async Task RejectScope_MintsNothing()
    {
        await CreateSoulAsync();
        var dir = NewDir("reject");
        var id = await RequestScopeAsync("sess-1", dir);
        Assert.False(string.IsNullOrWhiteSpace(id));

        await PostJsonAsync($"/context/approve/{id}/reject");

        var poll = await PostJsonAsync($"/context/approve/{id}/poll");
        Assert.Equal("rejected", poll.GetProperty("status").GetString());

        var status = await _client.GetAsync($"/scope/status?session=sess-1&path={Uri.EscapeDataString(dir)}");
        var statusBody = JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement;
        Assert.False(statusBody.GetProperty("granted").GetBoolean());
    }

    [Fact]
    public async Task RevokeScope_EndsGrant()
    {
        await CreateSoulAsync();
        var dir = NewDir("revoke");
        var id = await RequestScopeAsync("sess-1", dir);
        await PostJsonAsync($"/context/approve/{id}/approve");

        var revoke = await PostJsonAsync("/scope/revoke", new { sessionId = "sess-1", path = dir });
        Assert.True(revoke.GetProperty("revoked").GetBoolean());

        var status = await _client.GetAsync($"/scope/status?session=sess-1&path={Uri.EscapeDataString(dir)}");
        var statusBody = JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement;
        Assert.False(statusBody.GetProperty("granted").GetBoolean());
    }

    [Fact]
    public async Task RequestScope_WithUnsignablePath_IsRefused()
    {
        await CreateSoulAsync();

        // '|' is the grant-payload field separator — such a path can never become a claim.
        Assert.Null(await RequestScopeAsync("sess-1", "/tmp/bad|path"));
        // … and an expansion is always session-scoped.
        Assert.Null(await RequestScopeAsync(null, "/tmp/ok"));
    }

    [Fact]
    public async Task GrantedPath_PassesEnforcement_OnlyWithSessionStamp()
    {
        await CreateSoulAsync();
        var declared = NewDir("declared");
        var granted  = NewDir("granted");
        Directory.CreateDirectory(granted);
        await File.WriteAllTextAsync(Path.Combine(granted, "hello.txt"), "hi");
        await ConfigureTerminalAsync(declared);

        // Before any grant: the path is outside the declared base → 403, even with a session stamp.
        var before = await PostWithSession("/project-files/list", new { root = granted, filter = "", limit = 10, allowedPaths = (string[]?)null }, "sess-fs");
        Assert.Equal(HttpStatusCode.Forbidden, before);

        // The server ASKS; the human approves at the node; the node mints the grant.
        var id = await RequestScopeAsync("sess-fs", granted);
        Assert.False(string.IsNullOrWhiteSpace(id));
        var approve = await PostJsonAsync($"/context/approve/{id}/approve");
        Assert.True(approve.GetProperty("ok").GetBoolean());

        // Requests without the session stamp still cannot use the expansion.
        var noStamp = await PostWithSession("/project-files/list", new { root = granted, filter = "", limit = 10, allowedPaths = (string[]?)null }, null);
        Assert.Equal(HttpStatusCode.Forbidden, noStamp);
        var wrongStamp = await PostWithSession("/project-files/list", new { root = granted, filter = "", limit = 10, allowedPaths = (string[]?)null }, "sess-other");
        Assert.Equal(HttpStatusCode.Forbidden, wrongStamp);

        // With the stamp: the node-signed expansion unions into the effective paths.
        var after = await PostWithSession("/project-files/list", new { root = granted, filter = "", limit = 10, allowedPaths = (string[]?)null }, "sess-fs");
        Assert.Equal(HttpStatusCode.OK, after);
    }

    private async Task<HttpStatusCode> PostWithSession(string url, object body, string? sessionHeader)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent(body) };
        if (sessionHeader != null) msg.Headers.Add("X-Aria-Session", sessionHeader);
        AddLocalHeaders(msg);
        var resp = await _client.SendAsync(msg);
        return resp.StatusCode;
    }
}
