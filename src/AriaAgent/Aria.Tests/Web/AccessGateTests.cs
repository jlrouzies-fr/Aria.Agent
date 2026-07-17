using System.Net;
using Aria.Web.Data.Context;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aria.Tests.Web;

public class AccessGateTests : IClassFixture<WebApplicationFactory<Aria.Web.Program>>
{
    private readonly WebApplicationFactory<Aria.Web.Program> _factory;

    public AccessGateTests(WebApplicationFactory<Aria.Web.Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            // The gate is bypassed in Development; test it under Production conditions.
            builder.UseEnvironment("Production");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GuestAccess:Codes"] = "FRIEND-CODE-1234:2099-01-01T00:00:00Z"
                });
            });

            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IDbContextFactory<AppDbContext>));
                if (descriptor != null)
                    services.Remove(descriptor);

                services.AddDbContextFactory<AppDbContext>(options =>
                    options.UseSqlite("Data Source=aria-access-tests.db"));
            });
        });
    }

    private HttpClient CreateExternalClient()
    {
        // Use an https base address so the Production Secure flag on the invite cookie
        // is honored by the cookie container.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost/"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.42");
        return client;
    }

    [Fact]
    public async Task Health_IsAlwaysPublic()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AccessPage_IsAlwaysPublic()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/access/pathoftheworthy");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("PATH OF THE WORTHY", html);
    }

    [Fact]
    public async Task UnknownExternalIp_IsForbidden()
    {
        var client = CreateExternalClient();
        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("403", html);
        Assert.Contains("203.0.113.42", html);
        Assert.Contains("/access/pathoftheworthy", html);
    }

    [Fact]
    public async Task ValidInviteCode_SetsCookieAndOpensGate()
    {
        var client = CreateExternalClient();

        var response = await client.PostAsync("/access/pathoftheworthy",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "FRIEND-CODE-1234" }));
        var postBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Redirect,
            $"Expected redirect, got {response.StatusCode}. Body: {postBody[..Math.Min(500, postBody.Length)]}");
        Assert.Contains("aria-worthy", response.Headers.GetValues("Set-Cookie").First());

        // Reuse the cookie from a (simulated) external IP and verify the gate opens.
        var redirect = await client.GetAsync(response.Headers.Location?.ToString() ?? "/");
        Assert.Equal(HttpStatusCode.OK, redirect.StatusCode);
    }

    [Fact]
    public async Task InvalidInviteCode_RendersError()
    {
        var client = CreateExternalClient();

        var response = await client.PostAsync("/access/pathoftheworthy",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "WRONG" }));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Invalid or expired code", html);
    }
}
