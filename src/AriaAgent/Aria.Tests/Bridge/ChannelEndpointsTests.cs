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
/// Verifies channels are node-authoritative: the bridge seeds public providers, lets local-origin
/// callers author custom channels + keys, and reports key presence — without any server involvement.
/// </summary>
public class ChannelEndpointsTests : IDisposable
{
    private readonly WebApplicationFactory<Aria.Bridge.Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbPath;

    public ChannelEndpointsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-channels-test-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Aria.Bridge.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BridgeDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<BridgeDbContext>(opts => opts.UseSqlite($"Data Source={_dbPath}"));
            }));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static void Local(HttpRequestMessage m)
    {
        m.Headers.Host = "localhost:5741";
        m.Headers.Add("Origin", "http://localhost:5741");
    }

    private static HttpContent Json(object v) =>
        new StringContent(JsonSerializer.Serialize(v), Encoding.UTF8, "application/json");

    private async Task<JsonElement[]> GetChannelsAsync()
    {
        var body = await _client.GetStringAsync("/channels");
        using var doc = JsonDocument.Parse(body);
        // Clone so the elements survive the JsonDocument being disposed.
        return doc.RootElement.GetProperty("channels").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    [Fact]
    public async Task GetChannels_SeedsPublicProviders()
    {
        var chans = await GetChannelsAsync();
        Assert.Contains(chans, c => c.GetProperty("name").GetString() == "OpenAI" && c.GetProperty("isPublic").GetBoolean());
        Assert.Contains(chans, c => c.GetProperty("name").GetString() == "Anthropic");
        // No key stored yet.
        var openai = chans.First(c => c.GetProperty("name").GetString() == "OpenAI");
        Assert.False(openai.GetProperty("hasKey").GetBoolean());
        Assert.Equal("https://api.openai.com/v1", openai.GetProperty("url").GetString());
    }

    [Fact]
    public async Task StoringKey_MarksProviderConfigured()
    {
        var put = new HttpRequestMessage(HttpMethod.Put, "/keys/OpenAI") { Content = Json(new { key = "sk-test-123" }) };
        Local(put);
        (await _client.SendAsync(put)).EnsureSuccessStatusCode();

        var chans = await GetChannelsAsync();
        var openai = chans.First(c => c.GetProperty("name").GetString() == "OpenAI");
        Assert.True(openai.GetProperty("hasKey").GetBoolean());
    }

    [Fact]
    public async Task CustomChannel_CanBeAuthoredAndDeleted()
    {
        var put = new HttpRequestMessage(HttpMethod.Put, "/channels/My%20Local%20LLM")
        {
            Content = Json(new { url = "http://127.0.0.1:1234/v1", models = new[] { "gemma-3-27b" }, isBridged = true })
        };
        Local(put);
        (await _client.SendAsync(put)).EnsureSuccessStatusCode();

        var chans = await GetChannelsAsync();
        var custom = chans.First(c => c.GetProperty("name").GetString() == "My Local LLM");
        Assert.False(custom.GetProperty("isPublic").GetBoolean());
        Assert.Equal("http://127.0.0.1:1234/v1", custom.GetProperty("url").GetString());
        Assert.Contains("gemma-3-27b", custom.GetProperty("models").EnumerateArray().Select(m => m.GetString()));

        var del = new HttpRequestMessage(HttpMethod.Delete, "/channels/My%20Local%20LLM");
        Local(del);
        (await _client.SendAsync(del)).EnsureSuccessStatusCode();

        chans = await GetChannelsAsync();
        Assert.DoesNotContain(chans, c => c.GetProperty("name").GetString() == "My Local LLM");
    }

    [Fact]
    public async Task PublicProviderName_CannotBeAuthoredAsCustom()
    {
        var put = new HttpRequestMessage(HttpMethod.Put, "/channels/OpenAI")
        {
            Content = Json(new { url = "https://attacker.example/v1", models = Array.Empty<string>() })
        };
        Local(put);
        var r = await _client.SendAsync(put);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task ChannelWrite_RequiresLocalOrigin()
    {
        // A non-local origin (as a relayed/cross-origin request would present) is refused.
        var put = new HttpRequestMessage(HttpMethod.Put, "/channels/Evil")
        {
            Content = Json(new { url = "http://127.0.0.1:1234/v1", models = Array.Empty<string>() })
        };
        put.Headers.Host = "evil.example";
        put.Headers.Add("Origin", "http://evil.example");
        var r = await _client.SendAsync(put);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }
}
