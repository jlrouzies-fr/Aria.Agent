using System.Text;
using Aria.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aria.Tests.Shared;

/// <summary>
/// Regression tests for FormatProber against a real local HTTP server (the prober builds its own
/// HttpClient, so an in-memory TestServer can't reach it).
///
/// The live failure these guard (observed with glm-4.7-flash-mlx on LM Studio): a long-reasoning
/// model streams valid reasoning_content deltas but outruns the probe timeout, and the old code
/// discarded everything it had collected and reported "Unknown" → "FORMAT NOT RECOGNISED", even
/// though the very first delta had already decided the verdict. The unbounded probe body also left
/// the local model generating for minutes after the probe gave up.
/// </summary>
public class FormatProberTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string _baseUrl = "";

    // What each test wants the fake endpoint to do; set before calling the prober.
    private volatile Func<HttpContext, Task>? _handler;

    // Last request body seen by the fake endpoint (to assert on max_tokens).
    private volatile string _lastRequestBody = "";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");

        _app.MapPost("/v1/chat/completions", async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            _lastRequestBody = await reader.ReadToEndAsync();
            var handler = _handler ?? throw new InvalidOperationException("test handler not set");
            await handler(ctx);
        });

        await _app.StartAsync();
        _baseUrl = _app.Urls.First() + "/v1/chat/completions";
    }

    public async Task DisposeAsync()
    {
        if (_app != null) await _app.DisposeAsync();
    }

    private static async Task WriteSseAsync(HttpContext ctx, string deltaJson)
    {
        var payload = $"data: {{\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":null}}]}}\n\n";
        await ctx.Response.WriteAsync(payload);
        await ctx.Response.Body.FlushAsync();
    }

    private static void StartSse(HttpContext ctx) => ctx.Response.ContentType = "text/event-stream";

    // ── Thinking probe ────────────────────────────────────────────────────────

    [Fact]
    public async Task Thinking_ReasoningContentModel_NeverEndingStream_StillDetected()
    {
        // GLM-style: reasoning_content deltas forever, stream never finishes.
        _handler = async ctx =>
        {
            StartSse(ctx);
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                await WriteSseAsync(ctx, "{\"reasoning_content\":\"thinking...\"}");
                await Task.Delay(50, ctx.RequestAborted);
            }
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var verdict = await FormatProber.ProbeThinkingAsync(_baseUrl, "fake-glm");
        sw.Stop();

        Assert.Equal("ReasoningContent", verdict);
        // Early exit: the first delta decides — must not sit out the full 45s budget.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"probe took {sw.Elapsed}");
        // Runaway guard: the probe must never hand the model an unbounded budget.
        Assert.Contains("max_tokens", _lastRequestBody);
    }

    [Fact]
    public async Task Thinking_ThinkTagsModel_CompletedStream_Detected()
    {
        _handler = async ctx =>
        {
            StartSse(ctx);
            await WriteSseAsync(ctx, "{\"content\":\"<think>hmm\"}");
            await WriteSseAsync(ctx, "{\"content\":\"</think>answer\"}");
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
        };

        Assert.Equal("ThinkTags", await FormatProber.ProbeThinkingAsync(_baseUrl, "fake"));
    }

    [Fact]
    public async Task Thinking_PlainModel_CompletedStream_IsNone()
    {
        _handler = async ctx =>
        {
            StartSse(ctx);
            await WriteSseAsync(ctx, "{\"content\":\"Quicksort is in-place...\"}");
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
        };

        Assert.Equal("None", await FormatProber.ProbeThinkingAsync(_baseUrl, "fake"));
    }

    [Fact]
    public async Task Thinking_HttpError_IsUnknownNotNone()
    {
        // A rejected probe says nothing about the model — must never be a confident "None".
        _handler = ctx => { ctx.Response.StatusCode = 500; return Task.CompletedTask; };

        Assert.Equal("Unknown", await FormatProber.ProbeThinkingAsync(_baseUrl, "fake"));
    }

    // ── Tool-call probe ───────────────────────────────────────────────────────

    [Fact]
    public async Task ToolCall_NativeDeltaThenNeverEndingStream_IsNone()
    {
        _handler = async ctx =>
        {
            StartSse(ctx);
            await WriteSseAsync(ctx, "{\"reasoning_content\":\"deciding...\"}");
            await WriteSseAsync(ctx, "{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"get_time\"}}]}");
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                await WriteSseAsync(ctx, "{\"reasoning_content\":\"...\"}");
                await Task.Delay(50, ctx.RequestAborted);
            }
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var verdict = await FormatProber.ProbeToolCallAsync(_baseUrl, "fake-glm");
        sw.Stop();

        Assert.Equal("None", verdict);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"probe took {sw.Elapsed}");
        Assert.Contains("max_tokens", _lastRequestBody);
    }

    [Fact]
    public async Task ToolCall_ClientParsedMarker_SplitAcrossDeltas_NeverEndingStream_Detected()
    {
        _handler = async ctx =>
        {
            StartSse(ctx);
            await WriteSseAsync(ctx, "{\"content\":\"<tool_\"}");
            await WriteSseAsync(ctx, "{\"content\":\"call>{\\\"name\\\":\\\"get_time\\\"}\"}");
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                await WriteSseAsync(ctx, "{\"content\":\".\"}");
                await Task.Delay(50, ctx.RequestAborted);
            }
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var verdict = await FormatProber.ProbeToolCallAsync(_baseUrl, "fake");
        sw.Stop();

        Assert.Equal("ToolCallTag", verdict);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"probe took {sw.Elapsed}");
    }
}
