using System.Net;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Verifies F-3: state-changing requests to the bridge must originate from the local loopback UI,
/// except for the tiny allowlist of paths that legitimately serve cross-origin browser traffic.
/// </summary>
public class LocalOriginMiddlewareTests
{
    private static TestServer BuildServer()
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services => services.AddRouting())
            .Configure(app =>
            {
                app.UseRouting();
                app.UseMiddleware<Aria.Bridge.Infrastructure.LocalOriginMiddleware>();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapMethods("/health", new[] { "GET", "POST", "OPTIONS" }, () => Results.Ok());
                    endpoints.MapMethods("/node/attest", new[] { "GET", "POST" }, () => Results.Ok());
                    endpoints.MapMethods("/soul/export", new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS" }, () => Results.Ok(new { blob = "test" }));
                    endpoints.MapMethods("/soul/unlink", new[] { "GET", "HEAD", "POST", "DELETE" }, () => Results.Ok());
                    endpoints.MapMethods("/test-mutation", new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS" }, () => Results.Ok());
                });
            });
        return new TestServer(builder);
    }

    private static HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static HttpRequestMessage MutatingRequest(string method, string path, string host, string? origin)
    {
        var msg = new HttpRequestMessage(new HttpMethod(method), path);
        msg.Headers.Host = host;
        if (origin != null) msg.Headers.Add("Origin", origin);
        if (method is "POST" or "PUT" or "PATCH")
            msg.Content = JsonContent(new { });
        return msg;
    }

    [Theory]
    [InlineData("POST", "/soul/export", "localhost:5741", "http://localhost:5741")]
    [InlineData("POST", "/soul/unlink", "localhost:5741", null)]
    [InlineData("PUT", "/soul/export", "127.0.0.1:5741", "http://127.0.0.1:5741")]
    [InlineData("DELETE", "/test-mutation", "localhost:5741", null)]
    public async Task LocalOrigin_Mutating_Allowed(string method, string path, string host, string? origin)
    {
        using var server = BuildServer();
        using var client = server.CreateClient();
        var r = await client.SendAsync(MutatingRequest(method, path, host, origin));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/soul/export", "localhost:5741", "http://example.com")]
    [InlineData("POST", "/soul/unlink", "localhost:5741", "https://evil.test")]
    [InlineData("POST", "/test-mutation", "attacker.local", "http://localhost:5741")]
    [InlineData("PUT", "/soul/export", "localhost:5129", "http://localhost:5741")]
    [InlineData("DELETE", "/test-mutation", "localhost:5741", "null")]
    public async Task NonLocalOrigin_Mutating_Blocked(string method, string path, string host, string origin)
    {
        using var server = BuildServer();
        using var client = server.CreateClient();
        var r = await client.SendAsync(MutatingRequest(method, path, host, origin));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("localOriginRequired", body);
    }

    [Theory]
    [InlineData("POST", "/node/attest", "localhost:5741", "http://example.com")]
    [InlineData("POST", "/health", "localhost:5741", "http://example.com")]
    public async Task Allowlist_CrossOrigin_Allowed(string method, string path, string host, string origin)
    {
        using var server = BuildServer();
        using var client = server.CreateClient();
        var r = await client.SendAsync(MutatingRequest(method, path, host, origin));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/soul/export", "localhost:5741", "http://example.com")]
    [InlineData("HEAD", "/soul/export", "localhost:5741", "http://example.com")]
    public async Task ReadMethods_NotBlocked(string method, string path, string host, string origin)
    {
        using var server = BuildServer();
        using var client = server.CreateClient();
        var r = await client.SendAsync(MutatingRequest(method, path, host, origin));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task OptionsPreflight_NotBlocked()
    {
        using var server = BuildServer();
        using var client = server.CreateClient();
        var msg = new HttpRequestMessage(HttpMethod.Options, "/soul/export");
        msg.Headers.Host = "localhost:5741";
        msg.Headers.Add("Origin", "http://example.com");
        var r = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Theory]
    [InlineData("localhost:5742", "http://localhost:5742", true)]
    [InlineData("127.0.0.1:5742", "http://127.0.0.1:5742", true)]
    [InlineData("localhost:5741", "http://localhost:5741", false)]
    [InlineData("localhost:5742", "http://localhost:5741", false)]
    public void IsLocalOrigin_DerivesFromInjectedPort(string host, string origin, bool expectedAllowed)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Host = host;
        httpContext.Request.Headers.Origin = origin;

        var allowed = LocalRequestGuard.IsLocalOrigin(httpContext.Request, "http://localhost:5742", 5742);

        Assert.Equal(expectedAllowed, allowed);
    }
}
