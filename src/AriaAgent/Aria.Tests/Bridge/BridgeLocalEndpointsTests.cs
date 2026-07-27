using Aria.Bridge.Infrastructure;
using Xunit;

namespace Aria.Tests.Bridge;

public class BridgeLocalEndpointsTests
{
    [Theory]
    [InlineData(null, "http://localhost:5741")]
    [InlineData("", "http://localhost:5741")]
    [InlineData("http://localhost:5742", "http://localhost:5742")]
    [InlineData("http://127.0.0.1:5742", "http://127.0.0.1:5742")]
    [InlineData("http://+:5742", "http://localhost:5742")]
    [InlineData("http://*:5742", "http://localhost:5742")]
    [InlineData("http://0.0.0.0:5742", "http://localhost:5742")]
    [InlineData("http://localhost:5742;http://localhost:5743", "http://localhost:5742")]
    [InlineData("not a url", "http://localhost:5741")]
    public void ResolveBaseUrl_NormalizesAspNetCoreUrls(string? urls, string expected)
    {
        Assert.Equal(expected, BridgeLocalEndpoints.ResolveBaseUrl(urls));
    }
}
