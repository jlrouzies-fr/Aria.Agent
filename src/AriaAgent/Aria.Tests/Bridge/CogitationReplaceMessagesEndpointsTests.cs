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
/// Covers POST /cogitations/{id}/messages/replace and that /compact shares the same rewrite path.
/// </summary>
public class CogitationReplaceMessagesEndpointsTests : IDisposable
{
    private readonly WebApplicationFactory<Aria.Bridge.Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbPath;

    public CogitationReplaceMessagesEndpointsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-cog-replace-{Guid.NewGuid():N}.db");

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
        _client.Dispose();
        _factory.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static void AddLocalHeaders(HttpRequestMessage msg)
    {
        msg.Headers.Host = "localhost:5741";
        msg.Headers.Add("Origin", "http://localhost:5741");
    }

    private HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private async Task<string> SeedCogitationWithMessagesAsync()
    {
        var soulMsg = new HttpRequestMessage(HttpMethod.Post, "/soul")
        {
            Content = JsonContent(new { name = "Replace Soul", avatarSpriteKey = (string?)null, accentColor = (string?)null })
        };
        AddLocalHeaders(soulMsg);
        (await _client.SendAsync(soulMsg)).EnsureSuccessStatusCode();

        var serverUserId = $"test-soul-{Guid.NewGuid():N}";
        var putSoul = new HttpRequestMessage(HttpMethod.Put, "/soul")
        {
            Content = JsonContent(new { serverSoulId = serverUserId })
        };
        AddLocalHeaders(putSoul);
        (await _client.SendAsync(putSoul)).EnsureSuccessStatusCode();

        var cogId = $"sv-test-{Guid.NewGuid():N}";
        var init = new HttpRequestMessage(HttpMethod.Post, "/cogitations/init")
        {
            Content = JsonContent(new
            {
                id = cogId,
                serverUserId,
                ariaAvatarKey = (string?)null,
                subAgentId = (string?)null,
            })
        };
        AddLocalHeaders(init);
        (await _client.SendAsync(init)).EnsureSuccessStatusCode();

        foreach (var (role, content) in new[]
                 {
                     ("user", "first"),
                     ("assistant", "reply-one"),
                     ("user", "second"),
                     ("assistant", "reply-two"),
                 })
        {
            var add = new HttpRequestMessage(HttpMethod.Post, $"/cogitations/{cogId}/messages")
            {
                Content = JsonContent(new { role, content, thinkingContent = (string?)null })
            };
            AddLocalHeaders(add);
            (await _client.SendAsync(add)).EnsureSuccessStatusCode();
        }

        return cogId;
    }

    [Fact]
    public async Task ReplaceMessages_KeepsPrefixOrder()
    {
        var cogId = await SeedCogitationWithMessagesAsync();

        var replace = new HttpRequestMessage(HttpMethod.Post, $"/cogitations/{cogId}/messages/replace")
        {
            Content = JsonContent(new
            {
                messages = new object[]
                {
                    new { role = "user", content = "first", thinkingContent = (string?)null },
                    new { role = "assistant", content = "reply-one", thinkingContent = (string?)null },
                }
            })
        };
        AddLocalHeaders(replace);
        var resp = await _client.SendAsync(replace);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var get = new HttpRequestMessage(HttpMethod.Get, $"/cogitations/{cogId}/messages");
        AddLocalHeaders(get);
        var getResp = await _client.SendAsync(get);
        getResp.EnsureSuccessStatusCode();
        var msgs = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, msgs.GetArrayLength());
        Assert.Equal("first", msgs[0].GetProperty("content").GetString());
        Assert.Equal("reply-one", msgs[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Compact_LeavesSingleSummary()
    {
        var cogId = await SeedCogitationWithMessagesAsync();

        var compact = new HttpRequestMessage(HttpMethod.Post, $"/cogitations/{cogId}/compact")
        {
            Content = JsonContent(new { summary = "all prior turns" })
        };
        AddLocalHeaders(compact);
        var resp = await _client.SendAsync(compact);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var get = new HttpRequestMessage(HttpMethod.Get, $"/cogitations/{cogId}/messages");
        AddLocalHeaders(get);
        var getResp = await _client.SendAsync(get);
        getResp.EnsureSuccessStatusCode();
        var msgs = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, msgs.GetArrayLength());
        Assert.Equal("assistant", msgs[0].GetProperty("role").GetString());
        Assert.Equal("all prior turns", msgs[0].GetProperty("content").GetString());
    }
}
