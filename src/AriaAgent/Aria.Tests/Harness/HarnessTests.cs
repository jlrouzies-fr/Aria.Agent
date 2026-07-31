using Aria.Agent;
using Aria.Console.Harness;
using Aria.Harness.Context;
using Aria.Harness.Core;
using Aria.Harness.Formats;
using Aria.Harness.Tools;
using Aria.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aria.Tests.HarnessCore;

public class HarnessTests
{
    [Fact]
    public async Task DetectThinkingFormatAsync_PublicProvider_ReturnsNone()
    {
        var runtime = new FakeHarnessRuntime();
        runtime.AddSource(new ModelSource
        {
            Name = "OpenAI",
            Url = "https://api.openai.com/v1",
            IsPublicProvider = true,
            Models = ["gpt-4o"]
        });

        var harness = new Aria.Harness.Core.Harness(NullLogger<Aria.Harness.Core.Harness>.Instance, runtime);
        var format = await harness.DetectThinkingFormatAsync("OpenAI", "gpt-4o", HarnessContext.Empty);

        Assert.Equal(ThinkingFormat.None, format);
    }

    [Fact]
    public async Task DetectToolCallFormatAsync_PublicProvider_ReturnsNone()
    {
        var runtime = new FakeHarnessRuntime();
        runtime.AddSource(new ModelSource
        {
            Name = "OpenAI",
            Url = "https://api.openai.com/v1",
            IsPublicProvider = true,
            Models = ["gpt-4o"]
        });

        var harness = new Aria.Harness.Core.Harness(NullLogger<Aria.Harness.Core.Harness>.Instance, runtime);
        var format = await harness.DetectToolCallFormatAsync("OpenAI", "gpt-4o", HarnessContext.Empty);

        Assert.Equal(ToolCallFormat.None, format);
    }

    [Fact]
    public async Task CreateSessionAsync_PublicProvider_ReturnsAgentAndSession()
    {
        var runtime = new FakeHarnessRuntime();
        runtime.AddSource(new ModelSource
        {
            Name = "OpenAI",
            Url = "https://api.openai.com/v1",
            IsPublicProvider = true,
            Models = ["gpt-4o"]
        });

        var harness = new Aria.Harness.Core.Harness(NullLogger<Aria.Harness.Core.Harness>.Instance, runtime);
        var options = new HarnessOptions
        {
            SelectedSourceName = "OpenAI",
            SelectedModel = "gpt-4o",
            EnabledTools = [new ActiveToolConfig("datetime")]
        };

        var (agent, session) = await harness.CreateSessionAsync(options, HarnessContext.Empty);

        Assert.NotNull(agent);
        Assert.NotNull(session);
    }

    [Fact]
    public async Task CreateSessionAsync_EnablesMessageInjectingChatClient()
    {
        var runtime = new FakeHarnessRuntime();
        runtime.AddSource(new ModelSource
        {
            Name = "OpenAI",
            Url = "https://api.openai.com/v1",
            IsPublicProvider = true,
            Models = ["gpt-4o"]
        });

        var harness = new Aria.Harness.Core.Harness(NullLogger<Aria.Harness.Core.Harness>.Instance, runtime);
        var options = new HarnessOptions
        {
            SelectedSourceName = "OpenAI",
            SelectedModel = "gpt-4o",
            EnabledTools = [new ActiveToolConfig("datetime")]
        };

        var (agent, _) = await harness.CreateSessionAsync(options, HarnessContext.Empty);

        Assert.IsType<Microsoft.Agents.AI.ChatClientAgent>(agent);
        var chatAgent = (Microsoft.Agents.AI.ChatClientAgent)agent;
#pragma warning disable MAAI001
        var injector = chatAgent.GetService(typeof(Microsoft.Agents.AI.MessageInjectingChatClient));
#pragma warning restore MAAI001
        Assert.NotNull(injector);
    }

    [Fact]
    public void ConsoleHarnessRuntime_FindSource_ByName_ReturnsMatchingSource()
    {
        var runtime = new ConsoleHarnessRuntime(new ConfigurationBuilder().Build(), bridgeBaseUrl: "http://127.0.0.1:1");
        runtime.AddSource(new ModelSource
        {
            Name = "Local",
            Url = "http://localhost:1234/v1",
            Models = ["model1"]
        });

        var source = runtime.FindSource("Local", HarnessContext.Empty);

        Assert.NotNull(source);
        Assert.Equal("Local", source!.Name);
    }

    [Fact]
    public void ConsoleHarnessRuntime_FindSource_EmptyName_ReturnsFirstSource()
    {
        var runtime = new ConsoleHarnessRuntime(new ConfigurationBuilder().Build(), bridgeBaseUrl: "http://127.0.0.1:1");
        runtime.AddSource(new ModelSource
        {
            Name = "First",
            Url = "http://first/v1",
            Models = ["model1"]
        });
        runtime.AddSource(new ModelSource
        {
            Name = "Second",
            Url = "http://second/v1",
            Models = ["model2"]
        });

        var source = runtime.FindSource(null, HarnessContext.Empty);

        Assert.NotNull(source);
        Assert.Equal("First", source!.Name);
    }

    [Fact]
    public async Task ConsoleHarnessRuntime_IsBridgeAvailable_ReturnsTrue()
    {
        var runtime = new ConsoleHarnessRuntime(new ConfigurationBuilder().Build(), bridgeBaseUrl: "http://127.0.0.1:1");
        var available = await runtime.IsBridgeAvailableAsync(HarnessContext.Empty);
        Assert.True(available);
    }

    [Fact]
    public async Task CreateSessionAsync_WithAskUserAndContextStatusWired_Succeeds()
    {
        var runtime = new FakeHarnessRuntime();
        runtime.AddSource(new ModelSource
        {
            Name = "OpenAI",
            Url = "https://api.openai.com/v1",
            IsPublicProvider = true,
            Models = ["gpt-4o"]
        });

        var harness = new Aria.Harness.Core.Harness(NullLogger<Aria.Harness.Core.Harness>.Instance, runtime);
        var options = new HarnessOptions
        {
            SelectedSourceName = "OpenAI",
            SelectedModel = "gpt-4o",
            EnabledTools = [],
            OnAskUser = (_, _, _) => Task.FromResult<string?>(null),
            ContextStatusProvider = () => new ContextStatusSnapshot(null, 0, null, 0, 0),
        };

        var (agent, session) = await harness.CreateSessionAsync(options, HarnessContext.Empty);

        Assert.NotNull(agent);
        Assert.NotNull(session);
    }

    [Fact]
    public void ConsoleHarnessRuntime_FindSource_LoadsBridgeLocalSource()
    {
        var handler = new Aria.Tests.Fakes.FakeBridgeHttpHandler();
        handler.SetResponse("console/sources", new[]
        {
            new { id = 1, name = "Mac Local", url = "http://127.0.0.1:1234/v1", modelsJson = "[\"qwen3.6-35b\"]", isBridged = false, sortOrder = 0, bridgeNodeId = (string?)null }
        });

        var runtime = new ConsoleHarnessRuntime(
            new ConfigurationBuilder().Build(),
            httpClient: new HttpClient(handler) { BaseAddress = new Uri("http://bridge/") });

        var source = runtime.FindSource("Mac Local", HarnessContext.Empty);

        Assert.NotNull(source);
        Assert.Equal("Mac Local", source!.Name);
        Assert.Equal("http://127.0.0.1:1234/v1", source.Url);
        Assert.Contains("qwen3.6-35b", source.Models);
    }

    [Fact]
    public async Task ConsoleHarnessRuntime_GetApiKeyAsync_ReturnsConfiguredWhenProviderListed()
    {
        var handler = new Aria.Tests.Fakes.FakeBridgeHttpHandler();
        handler.SetResponse("keys", new { providers = new[] { "OpenAI", "Groq" } });

        var runtime = new ConsoleHarnessRuntime(
            new ConfigurationBuilder().Build(),
            httpClient: new HttpClient(handler) { BaseAddress = new Uri("http://bridge/") });

        var openAi = await runtime.GetApiKeyAsync("OpenAI", HarnessContext.Empty);
        var missing = await runtime.GetApiKeyAsync("Anthropic", HarnessContext.Empty);

        Assert.Equal("configured", openAi);
        Assert.Null(missing);
    }

    [Fact]
    public void PublicModelSourceCatalog_ContainsMajorProviders()
    {
        var names = Aria.Agent.PublicModelSourceCatalog.Providers.Select(p => p.Name).ToList();
        Assert.Contains("OpenAI", names);
        Assert.Contains("Anthropic", names);
        Assert.Contains("Google Gemini", names);
        Assert.Contains("Mistral", names);
        Assert.Contains("Groq", names);
    }
}
