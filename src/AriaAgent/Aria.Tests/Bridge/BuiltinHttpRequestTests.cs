using System.Net;
using System.Text;
using System.Text.Json;
using Aria.Bridge;
using Aria.Shared;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Behaviour of the http_request builtin: method/headers/body round-trip against a tiny local
/// listener, status-code and redirect surfacing (no auto-follow), scheme and timeout validation,
/// and the Layer B classification (Sensitive — it can carry data out of the node).
/// Serialized: parallel tests racing for ephemeral ports destabilise HttpListener's endpoint
/// manager (Close can throw "Address already in use" when another listener took the port).
/// </summary>
[Collection("BuiltinHttpRequest")]
public class BuiltinHttpRequestTests
{
    private static Dictionary<string, JsonElement> Args(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // Runs one request/response exchange on a throwaway local listener; returns the port and the
    // handler task (awaited after the tool call so server-side assertions surface).
    private static (int Port, Task Done) StartSingleRequestServer(Func<HttpListenerContext, Task> handle)
    {
        // FreePort() has a TOCTOU window before Start() — under full-suite parallelism another
        // test can grab the port. Retry the bind a few times with a fresh port.
        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException) when (attempt < 4)
            {
                continue;
            }
            var done = Task.Run(async () =>
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    await handle(ctx);
                }
                finally
                {
                    try { listener.Stop(); } catch { /* cleanup must not fail the test */ }
                    try { listener.Close(); } catch { /* endpoint manager race under parallel load */ }
                }
            });
            return (port, done);
        }
    }

    private static async Task Respond(
        HttpListenerContext ctx, int status, string body, params (string Name, string Value)[] headers)
    {
        ctx.Response.StatusCode = status;
        foreach (var (name, value) in headers) ctx.Response.Headers[name] = value;
        var bytes = Encoding.UTF8.GetBytes(body);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    [Fact]
    public async Task Get_RoundTrip_MethodHeadersStatusAndBodySurface()
    {
        string? gotMethod = null, gotHeader = null;
        var (port, done) = StartSingleRequestServer(async ctx =>
        {
            gotMethod = ctx.Request.HttpMethod;
            gotHeader = ctx.Request.Headers["X-Test"];
            await Respond(ctx, 200, "hello body", ("X-Reply", "yes"));
        });

        var result = await BuiltinTools.InvokeAsync("http_request", Args(new
        {
            method  = "GET",
            url     = $"http://127.0.0.1:{port}/api",
            headers = new Dictionary<string, string> { ["X-Test"] = "abc" },
        }), policy: null);
        await done;

        Assert.False(result.IsError);
        Assert.Equal("GET", gotMethod);
        Assert.Equal("abc", gotHeader);
        Assert.Contains("HTTP 200", result.Text);
        Assert.Contains("X-Reply: yes", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hello body", result.Text);
    }

    [Fact]
    public async Task Post_WithBodyAndContentType_EchoesBack()
    {
        string? gotBody = null, gotContentType = null;
        var (port, done) = StartSingleRequestServer(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            gotBody        = await reader.ReadToEndAsync();
            gotContentType = ctx.Request.ContentType;
            await Respond(ctx, 201, $"echo:{gotBody}");
        });

        var result = await BuiltinTools.InvokeAsync("http_request", Args(new
        {
            method  = "POST",
            url     = $"http://127.0.0.1:{port}/",
            headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            body    = """{"k":1}""",
        }), policy: null);
        await done;

        Assert.False(result.IsError);
        Assert.Equal("""{"k":1}""", gotBody);
        Assert.StartsWith("application/json", gotContentType);
        Assert.Contains("HTTP 201", result.Text);
        Assert.Contains("""echo:{"k":1}""", result.Text);
    }

    [Fact]
    public async Task Redirect_IsReported_NotFollowed()
    {
        var (port, done) = StartSingleRequestServer(async ctx =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = "/elsewhere";
            ctx.Response.Close();
            await Task.CompletedTask;
        });

        var result = await BuiltinTools.InvokeAsync("http_request",
            Args(new { method = "GET", url = $"http://127.0.0.1:{port}/" }), policy: null);
        await done;

        Assert.False(result.IsError);
        Assert.Contains("HTTP 302", result.Text);
        Assert.Contains("/elsewhere", result.Text);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/x")]
    [InlineData("not-a-url")]
    public async Task NonHttpScheme_Refused(string url)
    {
        var result = await BuiltinTools.InvokeAsync("http_request",
            Args(new { method = "GET", url }), policy: null);

        Assert.True(result.IsError);
        Assert.Contains("http:// or https://", result.Text);
    }

    [Fact]
    public async Task TimeoutOver60_Rejected()
    {
        var result = await BuiltinTools.InvokeAsync("http_request",
            Args(new { method = "GET", url = "http://127.0.0.1:1/", timeout_seconds = 61 }), policy: null);

        Assert.True(result.IsError);
        Assert.Contains("between 1 and 60", result.Text);
    }

    [Fact]
    public async Task BadMethod_Rejected()
    {
        var result = await BuiltinTools.InvokeAsync("http_request",
            Args(new { method = "YEET", url = "http://127.0.0.1:1/" }), policy: null);

        Assert.True(result.IsError);
        Assert.Contains("method", result.Text);
    }

    [Fact]
    public void Classification_IsSensitive_NotBenign()
    {
        // Not on the read-only Benign list: http_request can reach LAN/localhost and carry data out.
        Assert.Equal(RequestSensitivity.Sensitive,
            RequestClassifier.Classify("POST", "/tools/call", """{"toolName":"http_request"}"""));
    }
}
