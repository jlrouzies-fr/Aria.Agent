using System.Net;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Regression for the stale-model-list bug: after a user adds/removes models in their local server,
/// the web's ⟳ rediscover re-queried the endpoint but the fresh list was never written back to the
/// node — so the next /channels fetch reverted to the stored (stale) list, and the bridge status page
/// kept showing the old models too. /llm/discover-models must now PERSIST what it finds onto the
/// matching custom channel.
/// </summary>
public class ChannelDiscoveryPersistenceTests : IAsyncLifetime, IDisposable
{
    private readonly WebApplicationFactory<Aria.Bridge.Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbPath;

    // Fake "LM Studio" upstream whose /v1/models list the test can change between calls.
    private WebApplication? _upstream;
    private string _upstreamBase = "";
    private volatile string[] _upstreamModels = [];

    public ChannelDiscoveryPersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-chdisc-test-{Guid.NewGuid():N}.db");
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

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _upstream = builder.Build();
        _upstream.Urls.Add("http://127.0.0.1:0");
        _upstream.MapGet("/v1/models", () =>
            Results.Json(new { data = _upstreamModels.Select(m => new { id = m }).ToArray() }));
        await _upstream.StartAsync();
        _upstreamBase = _upstream.Urls.First() + "/v1";
    }

    public async Task DisposeAsync()
    {
        if (_upstream != null) await _upstream.DisposeAsync();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private static void AddLocalHeaders(HttpRequestMessage msg)
    {
        msg.Headers.Host = "localhost:5741";
        msg.Headers.Add("Origin", "http://localhost:5741");
    }

    private HttpContent Json(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private async Task PutChannelAsync(string name, string url, string[] models)
    {
        var msg = new HttpRequestMessage(HttpMethod.Put, $"/channels/{Uri.EscapeDataString(name)}")
        {
            Content = Json(new { url, models, isBridged = true })
        };
        AddLocalHeaders(msg);
        (await _client.SendAsync(msg)).EnsureSuccessStatusCode();
    }

    private async Task<string[]> GetChannelModelsAsync(string name)
    {
        var r = await _client.GetAsync("/channels");
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        foreach (var c in doc.RootElement.GetProperty("channels").EnumerateArray())
            if (c.GetProperty("name").GetString() == name)
                return c.GetProperty("models").EnumerateArray().Select(m => m.GetString()!).ToArray();
        return [];
    }

    [Fact]
    public async Task DiscoverModels_PersistsFreshListOntoChannel()
    {
        // Channel stored with a now-stale two-model list.
        await PutChannelAsync("Local", _upstreamBase, ["stale-a", "stale-b"]);
        Assert.Equal(["stale-a", "stale-b"], await GetChannelModelsAsync("Local"));

        // The upstream now advertises a different set (user added/removed models).
        _upstreamModels = ["fresh-1", "fresh-2", "fresh-3"];

        var msg = new HttpRequestMessage(HttpMethod.Post, "/llm/discover-models")
        {
            Content = Json(new { url = _upstreamBase, keyRef = "Local" })
        };
        AddLocalHeaders(msg);
        var resp = await _client.SendAsync(msg);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        // The critical assertion: the persisted channel list reflects the fresh upstream set,
        // proving the write survives a re-fetch (which is what reverted it before the fix).
        Assert.Equal(["fresh-1", "fresh-2", "fresh-3"], await GetChannelModelsAsync("Local"));
    }

    [Fact]
    public async Task DiscoverModels_UnknownChannel_DoesNotThrowOrCreate()
    {
        _upstreamModels = ["x1", "x2"];
        var msg = new HttpRequestMessage(HttpMethod.Post, "/llm/discover-models")
        {
            Content = Json(new { url = _upstreamBase, keyRef = "NoSuchChannel" })
        };
        AddLocalHeaders(msg);
        var resp = await _client.SendAsync(msg);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        // No custom channel by that name should have been created as a side effect.
        Assert.Empty(await GetChannelModelsAsync("NoSuchChannel"));
    }
}
