using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Endpoints;
using Aria.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Verifies the Inquisitorial Seal approval pages and lifecycle:
/// pending page, approval/rejection, already-resolved pages, and unknown-seal page.
/// </summary>
public class SealEndpointsTests : IDisposable
{
    private readonly WebApplicationFactory<Aria.Bridge.Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbPath;

    private readonly Action<string> _originalLauncher;

    public SealEndpointsTests()
    {
        _originalLauncher = Aria.Bridge.Endpoints.SealEndpoints.LaunchSealPage;
        Aria.Bridge.Endpoints.SealEndpoints.LaunchSealPage = _ => { };

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-seal-test-{Guid.NewGuid():N}.db");

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

    private async Task<string> RequestAndApproveSealAsync(string toolName)
    {
        var id = await RequestSealAsync(toolName);
        var approveMsg = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/approve");
        AddLocalHeaders(approveMsg);
        var approve = await _client.SendAsync(approveMsg);
        approve.EnsureSuccessStatusCode();
        return id;
    }

    private async Task<string> RequestSealAsync(string toolName = "soul-export", bool signStatement = true, byte[]? nonce = null)
    {
        nonce ??= Encoding.UTF8.GetBytes("test-nonce");
        var reqMsg = new HttpRequestMessage(HttpMethod.Post, "/seal/request")
        {
            Content = JsonContent(new
            {
                toolName,
                reason = "test",
                argsPreview = "test args",
                nonceBase64 = Convert.ToBase64String(nonce),
                signStatement
            })
        };
        AddLocalHeaders(reqMsg);
        var req = await _client.SendAsync(reqMsg);
        req.EnsureSuccessStatusCode();
        var reqBody = await req.Content.ReadAsStringAsync();
        return JsonDocument.Parse(reqBody).RootElement.GetProperty("id").GetString()!;
    }

    private async Task<string?> GetSoulPublicKeyAsync()
    {
        var r = await _client.GetAsync("/soul/pubkey");
        if (r.StatusCode != HttpStatusCode.OK) return null;
        var body = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("publicKey").GetString();
    }

    private static void AddLocalHeaders(HttpRequestMessage msg)
    {
        msg.Headers.Host = "localhost:5741";
        msg.Headers.Add("Origin", "http://localhost:5741");
    }

    private HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    [Fact]
    public async Task RequestSeal_ReturnsId()
    {
        var id = await RequestSealAsync();
        Assert.False(string.IsNullOrWhiteSpace(id));
    }

    [Fact]
    public async Task GetPendingSealPage_ShowsApproveAndRejectButtons()
    {
        var id = await RequestSealAsync();

        var r = await _client.GetAsync($"/seal/{id}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("GRANT SEAL", html);
        Assert.Contains("REFUSE", html);
        Assert.Contains("seal-watermark", html);
    }

    [Fact]
    public async Task ApproveSeal_ReturnsSuccessPageAndPollsApproved()
    {
        await CreateSoulAsync();
        var id = await RequestSealAsync();

        var approveMsg = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/approve");
        AddLocalHeaders(approveMsg);
        var approve = await _client.SendAsync(approveMsg);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var html = await approve.Content.ReadAsStringAsync();
        Assert.Contains("SEAL GRANTED", html);

        var pollMsg = new HttpRequestMessage(HttpMethod.Post, "/seal/poll")
        {
            Content = JsonContent(new { id })
        };
        AddLocalHeaders(pollMsg);
        var poll = await _client.SendAsync(pollMsg);
        poll.EnsureSuccessStatusCode();
        var pollBody = await poll.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(pollBody);
        Assert.Equal("approved", doc.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("signatureBase64").GetString()));
    }

    // ── JSON mode (the seal page's fetch path — keeps the tab single-history so it can self-close) ──

    [Fact]
    public async Task ApproveSeal_WithJsonAccept_ReturnsOkJsonAndPollsApproved()
    {
        await CreateSoulAsync();
        var id = await RequestSealAsync();

        var approveMsg = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/approve");
        AddLocalHeaders(approveMsg);
        approveMsg.Headers.Accept.ParseAdd("application/json");
        var approve = await _client.SendAsync(approveMsg);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal("application/json", approve.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await approve.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("SEAL GRANTED", doc.RootElement.GetProperty("message").GetString());

        var pollMsg = new HttpRequestMessage(HttpMethod.Post, "/seal/poll") { Content = JsonContent(new { id }) };
        AddLocalHeaders(pollMsg);
        var poll = await _client.SendAsync(pollMsg);
        using var pollDoc = JsonDocument.Parse(await poll.Content.ReadAsStringAsync());
        Assert.Equal("approved", pollDoc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RejectSeal_WithJsonAccept_ReturnsOkJson()
    {
        var id = await RequestSealAsync();

        var rejectMsg = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/reject");
        AddLocalHeaders(rejectMsg);
        rejectMsg.Headers.Accept.ParseAdd("application/json");
        var reject = await _client.SendAsync(rejectMsg);
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);

        using var doc = JsonDocument.Parse(await reject.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("SEAL REFUSED", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ApproveUnknownSeal_WithJsonAccept_ReturnsNotOkJson()
    {
        var approveMsg = new HttpRequestMessage(HttpMethod.Post, "/seal/does-not-exist/approve");
        AddLocalHeaders(approveMsg);
        approveMsg.Headers.Accept.ParseAdd("application/json");
        var approve = await _client.SendAsync(approveMsg);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        using var doc = JsonDocument.Parse(await approve.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task PendingSealPage_UsesFetchButtons_NoFormNavigation()
    {
        var id = await RequestSealAsync();
        var html = await (await _client.GetAsync($"/seal/{id}")).Content.ReadAsStringAsync();

        Assert.Contains("GRANT SEAL &amp; CLOSE", html);
        Assert.Contains("REFUSE &amp; CLOSE", html);
        Assert.DoesNotContain("<form", html);          // no navigation → tab stays script-closable
        Assert.Contains($"/seal/{id}/", html);          // fetch target embedded in the script
    }

    [Fact]
    public async Task GetApprovedSealPage_ShowsAlreadyGranted()
    {
        await CreateSoulAsync();
        var id = await RequestSealAsync();

        var approveMsg = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/approve");
        AddLocalHeaders(approveMsg);
        await _client.SendAsync(approveMsg);

        var r = await _client.GetAsync($"/seal/{id}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("SEAL ALREADY GRANTED", html);
        Assert.DoesNotContain("GRANT SEAL", html);
    }

    [Fact]
    public async Task RejectSeal_ReturnsRefusedPageAndPollsRejected()
    {
        var id = await RequestSealAsync();

        var rejectMsg = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/reject");
        AddLocalHeaders(rejectMsg);
        var reject = await _client.SendAsync(rejectMsg);
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);

        var html = await reject.Content.ReadAsStringAsync();
        Assert.Contains("SEAL REFUSED", html);

        var pollMsg = new HttpRequestMessage(HttpMethod.Post, "/seal/poll")
        {
            Content = JsonContent(new { id })
        };
        AddLocalHeaders(pollMsg);
        var poll = await _client.SendAsync(pollMsg);
        poll.EnsureSuccessStatusCode();
        var pollBody = await poll.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(pollBody);
        Assert.Equal("rejected", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetRejectedSealPage_ShowsAlreadyRefused()
    {
        var id = await RequestSealAsync();

        var rejectMsg = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/reject");
        AddLocalHeaders(rejectMsg);
        await _client.SendAsync(rejectMsg);

        var r = await _client.GetAsync($"/seal/{id}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("SEAL ALREADY REFUSED", html);
        Assert.DoesNotContain("GRANT SEAL", html);
    }

    [Fact]
    public async Task GetUnknownSealPage_IsStyledNotFound()
    {
        var id = Guid.NewGuid().ToString("N");
        var r = await _client.GetAsync($"/seal/{id}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("SEAL NOT FOUND OR EXPIRED", html);
        Assert.Contains("seal-badge", html);
        Assert.Contains("<style>", html);
    }

    [Fact]
    public async Task ReApproveAlreadyResolvedSeal_ShowsAlreadyGranted()
    {
        await CreateSoulAsync();
        var id = await RequestSealAsync();

        var first = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/approve");
        AddLocalHeaders(first);
        await _client.SendAsync(first);

        var second = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/approve");
        AddLocalHeaders(second);
        var r = await _client.SendAsync(second);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("already been resolved", html);
    }

    [Fact]
    public async Task ApproveSeal_SignsCanonicalStatement()
    {
        await CreateSoulAsync();
        var pubKey = await GetSoulPublicKeyAsync();
        Assert.False(string.IsNullOrEmpty(pubKey));

        var id = await RequestSealAsync("terminal_pty");

        var approveMsg = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/approve");
        AddLocalHeaders(approveMsg);
        var approve = await _client.SendAsync(approveMsg);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var pollMsg = new HttpRequestMessage(HttpMethod.Post, "/seal/poll")
        {
            Content = JsonContent(new { id })
        };
        AddLocalHeaders(pollMsg);
        var poll = await _client.SendAsync(pollMsg);
        poll.EnsureSuccessStatusCode();

        var pollBody = await poll.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(pollBody);
        Assert.Equal("approved", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("signStatement").GetBoolean());

        var statement = doc.RootElement.GetProperty("statement").GetString();
        Assert.False(string.IsNullOrWhiteSpace(statement));
        Assert.Contains("Capability: terminal_pty", statement);

        var sig = doc.RootElement.GetProperty("signatureBase64").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sig));

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubKey!), out _);
        Assert.True(ecdsa.VerifyData(Encoding.UTF8.GetBytes(statement!), Convert.FromBase64String(sig!), HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task ApproveSeal_RawMode_SignsNonceBytesAndOmitsStatement()
    {
        await CreateSoulAsync();
        var pubKey = await GetSoulPublicKeyAsync();
        Assert.False(string.IsNullOrEmpty(pubKey));

        var nonce = Encoding.UTF8.GetBytes("raw-mode-nonce");
        var id = await RequestSealAsync("trust-device", signStatement: false, nonce: nonce);

        var approveMsg = new HttpRequestMessage(HttpMethod.Post, $"/seal/{id}/approve");
        AddLocalHeaders(approveMsg);
        var approve = await _client.SendAsync(approveMsg);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var pollMsg = new HttpRequestMessage(HttpMethod.Post, "/seal/poll")
        {
            Content = JsonContent(new { id })
        };
        AddLocalHeaders(pollMsg);
        var poll = await _client.SendAsync(pollMsg);
        poll.EnsureSuccessStatusCode();

        var pollBody = await poll.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(pollBody);
        Assert.Equal("approved", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.GetProperty("signStatement").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("statement", out var st) && st.ValueKind == JsonValueKind.Null);

        var sig = doc.RootElement.GetProperty("signatureBase64").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sig));

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubKey!), out _);
        Assert.True(ecdsa.VerifyData(nonce, Convert.FromBase64String(sig!), HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task ConsumeSeal_WrongCapability_ReturnsFalse()
    {
        await CreateSoulAsync();
        var id = await RequestAndApproveSealAsync("soul-export");

        Assert.False(SealEndpoints.TryConsumeSeal(id, "terminal_pty"));
        Assert.True(SealEndpoints.TryConsumeSeal(id, "soul-export"));
    }
}
