using System.Text.Json;
using Aria.Agent;
using Aria.Harness.Formats;
using Aria.Web.Data;
using Aria.Web.Data.Context;
using Aria.Web.Data.Users;
using Aria.Web.Services;
using Aria.Web.Services.AgentServices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using SkippableFactAttribute = Xunit.SkippableFactAttribute;

namespace Aria.Tests.Web;

/// <summary>
/// Integration tests that exercise the harness against the real local LM configured in Aria.Web's aria.db.
/// These tests are conditional on the local model server being reachable.
/// </summary>
public class LocalLmIntegrationTests : IClassFixture<WebApplicationFactory<Aria.Web.Program>>
{
    private readonly WebApplicationFactory<Aria.Web.Program> _factory;

    public LocalLmIntegrationTests(WebApplicationFactory<Aria.Web.Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IDbContextFactory<AppDbContext>));
                if (descriptor != null)
                    services.Remove(descriptor);

                services.AddDbContextFactory<AppDbContext>(options =>
                    options.UseSqlite("Data Source=aria-tests-local.db"));
            });
        });
    }

    private static string RealAriaDbPath
    {
        get
        {
            // Output is Aria.Tests/bin/Debug/net10.0/osx-arm64 -> walk up to src/AriaAgent.
            var baseDir = AppContext.BaseDirectory;
            var ariaAgentDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
            return Path.Combine(ariaAgentDir, "Aria.Web", "bin", "Debug", "net10.0", "osx-arm64", "aria.db");
        }
    }

    private sealed class LocalSourceInfo
    {
        public string UserId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public List<string> Models { get; set; } = [];
    }

    // Channels are now node-authoritative (stored on the bridge, not the server). There is no
    // server-side UserLocalSources table to read from, so these legacy integration tests always skip.
    private static LocalSourceInfo? ReadRealLocalSource() => null;

    private Task SeedLocalSourceAsync(LocalSourceInfo source) => Task.CompletedTask;

    [SkippableFact]
    public async Task DetectThinkingFormat_LocalSource_ReturnsKnownFormat()
    {
        var source = ReadRealLocalSource();
        Skip.If(source == null, "No local source configured in aria.db");

        await SeedLocalSourceAsync(source!);

        var agentService = _factory.Services.GetRequiredService<AgentService>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var format = await agentService.DetectThinkingFormatAsync(
            source!.Name, source.Models[0], cts.Token, source.UserId);

        Assert.True(
            format is ThinkingFormat.None
                or ThinkingFormat.ThinkTags
                or ThinkingFormat.ReasoningContent
                or ThinkingFormat.StartsInThinkMode,
            $"Unexpected thinking format: {format}");
    }

    [SkippableFact]
    public async Task DetectToolCallFormat_LocalSource_ReturnsKnownFormat()
    {
        var source = ReadRealLocalSource();
        Skip.If(source == null, "No local source configured in aria.db");

        await SeedLocalSourceAsync(source!);

        var agentService = _factory.Services.GetRequiredService<AgentService>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var format = await agentService.DetectToolCallFormatAsync(
            source!.Name, source.Models[0], cts.Token, source.UserId);

        Assert.True(
            format is ToolCallFormat.None
                or ToolCallFormat.ToolCallTag
                or ToolCallFormat.StartFunctionCall
                or ToolCallFormat.MistralToolCalls
                or ToolCallFormat.MinimaxToolCall
                or ToolCallFormat.KimiK2
                or ToolCallFormat.Longcat
                or ToolCallFormat.GlmXml
                or ToolCallFormat.Unknown,
            $"Unexpected tool-call format: {format}");
    }

    [SkippableFact]
    public async Task StreamAsync_LocalSource_ReportsTokenUsage()
    {
        var source = ReadRealLocalSource();
        Skip.If(source == null, "No local source configured in aria.db");

        await SeedLocalSourceAsync(source!);

        var agentService = _factory.Services.GetRequiredService<AgentService>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (agent, session) = await agentService.CreateSessionAsync(
            enabledTools: [],
            selectedSourceName: source!.Name,
            selectedModel: source.Models[0],
            userId: source.UserId);

        OpenAI.Chat.ChatTokenUsage? usage = null;
        var reply = "";
        await foreach (var token in agentService.StreamAsync(
            "Reply with exactly the word: pong", agent, session, cts.Token, onUsage: u => usage = u))
        {
            reply += token;
        }

        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.NotNull(usage);
        Assert.True(usage!.InputTokenCount > 0, "Expected InputTokenCount > 0");
        Assert.True(usage.OutputTokenCount > 0, "Expected OutputTokenCount > 0");
    }

    [SkippableFact]
    public async Task ForceRedetect_LocalSource_UpdatesCache()
    {
        var source = ReadRealLocalSource();
        Skip.If(source == null, "No local source configured in aria.db");

        await SeedLocalSourceAsync(source!);

        var agentService = _factory.Services.GetRequiredService<AgentService>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var (thinking, toolCall) = await agentService.ForceRedetectAsync(
            source!.Name, source.Models[0], cts.Token);

        Assert.True(
            thinking is ThinkingFormat.None
                or ThinkingFormat.ThinkTags
                or ThinkingFormat.ReasoningContent
                or ThinkingFormat.StartsInThinkMode,
            $"Unexpected thinking format after forced redetect: {thinking}");
        Assert.True(
            Enum.IsDefined(typeof(ToolCallFormat), toolCall),
            $"Forced redetect returned an invalid tool-call format value: {toolCall}");
    }
}
