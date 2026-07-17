using Aria.Bridge.Infrastructure;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aria.Tests.Bridge;

public class LocalRequestGuardTests
{
    [Theory]
    [InlineData("localhost:5741", null)]
    [InlineData("localhost:5741", "")]
    [InlineData("127.0.0.1:5741", null)]
    [InlineData("localhost:5741", "http://localhost:5741")]
    [InlineData("127.0.0.1:5741", "http://127.0.0.1:5741")]
    [InlineData("LOCALHOST:5741", "http://localhost:5741")]
    public void LocalHostAndOrigin_AreAllowed(string host, string? origin)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Host = host;
        if (origin != null) ctx.Request.Headers.Origin = origin;
        Assert.True(LocalRequestGuard.IsLocalOrigin(ctx.Request));
    }

    [Theory]
    [InlineData("example.com:5741", "http://localhost:5741")]
    [InlineData("localhost:5741", "http://example.com")]
    [InlineData("localhost:5741", "https://localhost:5741")]
    [InlineData("localhost:5129", "http://localhost:5741")]
    [InlineData("localhost:5741", "null")]
    [InlineData("", "")]
    public void NonLocalHostOrOrigin_AreBlocked(string host, string origin)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Host = host;
        ctx.Request.Headers.Origin = origin;
        Assert.False(LocalRequestGuard.IsLocalOrigin(ctx.Request));
    }

    [Fact]
    public void MissingHost_IsBlocked()
    {
        var ctx = new DefaultHttpContext();
        Assert.False(LocalRequestGuard.IsLocalOrigin(ctx.Request));
    }
}
