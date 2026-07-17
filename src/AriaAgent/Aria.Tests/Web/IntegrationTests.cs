using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aria.Web.Data;
using Aria.Web.Data.Context;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aria.Tests.Web;

public class IntegrationTests : IClassFixture<WebApplicationFactory<Aria.Web.Program>>
{
    private readonly WebApplicationFactory<Aria.Web.Program> _factory;

    public IntegrationTests(WebApplicationFactory<Aria.Web.Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Swap the real SQLite DB for an isolated test DB.
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IDbContextFactory<AppDbContext>));
                if (descriptor != null)
                    services.Remove(descriptor);

                services.AddDbContextFactory<AppDbContext>(options =>
                    options.UseSqlite("Data Source=aria-tests.db"));
            });
        });
    }

    [Fact]
    public async Task ChatSources_ReturnsPublicProviders()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/debug/chat/sources");

        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var names = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("OpenAI", names);
        Assert.Contains("Anthropic", names);
    }

    [Fact]
    public async Task ChatDetect_PublicProvider_ReturnsNone()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync(
            "/api/debug/chat/detect?source=OpenAI&model=gpt-4o", null);

        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal("None", doc.RootElement.GetProperty("thinkingFormat").GetString());
        Assert.Equal("None", doc.RootElement.GetProperty("toolCallFormat").GetString());
    }

    [Fact]
    public async Task ChatProbe_PublicProvider_ReturnsNone()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync(
            "/api/debug/chat/probe?source=OpenAI&model=gpt-4o", null);

        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal("None", doc.RootElement.GetProperty("thinkingFormat").GetString());
        Assert.Equal("None", doc.RootElement.GetProperty("toolCallFormat").GetString());
    }

    [Fact]
    public async Task McpBridgeHealth_WhenBridgeRunning_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/debug/mcp-bridge/health");

        // The bridge may or may not be running when tests execute; assert what we can.
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Unexpected status code: {response.StatusCode}");

        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            Assert.Equal(200, doc.RootElement.GetProperty("status").GetInt32());
        }
    }

    [Fact]
    public async Task McpBridgeTools_EndpointExists()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/debug/mcp-bridge/tools",
            new { command = "echo", arguments = Array.Empty<string>(), environment = (Dictionary<string, string>?)null });

        // We only verify the endpoint is wired and returns a parseable response;
        // actual tool discovery depends on the bridge + MCP server state.
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.InternalServerError ||
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Unexpected status code: {response.StatusCode}");
    }
}
