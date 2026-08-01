using Aria.Agent;
using Aria.Harness.Context;
using Aria.Harness.Core;
using Aria.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aria.Tests.Harness;

/// <summary>
/// Context-window resolution order: channel override beats provider discovery beats catalog beats
/// the 100k assumed fallback.
/// </summary>
public class ContextWindowTests
{
    private readonly FakeHarnessRuntime _runtime = new();
    private readonly Aria.Harness.Core.Harness _harness;

    public ContextWindowTests()
    {
        _harness = new Aria.Harness.Core.Harness(NullLogger<Aria.Harness.Core.Harness>.Instance, _runtime);
    }

    [Fact]
    public async Task Resolve_ChannelOverride_Wins()
    {
        var source = new ModelSource
        {
            Name = "local",
            Url = "http://localhost:1234/v1",
            Models = ["gemma"],
            ContextWindow = 8_192,
        };
        _runtime.AddSource(source);

        var window = await _harness.ResolveContextWindowAsync(source, "gemma", HarnessContext.Empty);

        Assert.Equal(8_192, window.Tokens);
        Assert.False(window.Assumed);
    }

    [Fact]
    public async Task Resolve_CachedDiscovery_WhenNoOverride()
    {
        var source = new ModelSource
        {
            Name = "local",
            Url = "http://localhost:1234/v1",
            Models = ["gemma"],
        };
        await _runtime.FormatCache.SetContextWindowAsync(source.Url, "gemma", new ContextWindow(4_096, false));

        var window = await _harness.ResolveContextWindowAsync(source, "gemma", HarnessContext.Empty);

        Assert.Equal(4_096, window.Tokens);
        Assert.False(window.Assumed);
    }

    [Fact]
    public async Task Resolve_PublicProvider_CatalogEntry()
    {
        var source = new ModelSource
        {
            Name = "OpenAI",
            Url = "https://api.openai.com/v1",
            Models = ["gpt-4o"],
            IsPublicProvider = true,
        };

        var window = await _harness.ResolveContextWindowAsync(source, "gpt-4o", HarnessContext.Empty);

        Assert.Equal(128_000, window.Tokens);
        Assert.False(window.Assumed);
    }

    [Fact]
    public async Task Resolve_UnknownModel_FallsBackToAssumed100k()
    {
        var source = new ModelSource
        {
            Name = "SomeCloud",
            Url = "https://example.com/v1",
            Models = ["unknown-model-v99"],
            IsPublicProvider = true,
        };

        var window = await _harness.ResolveContextWindowAsync(source, "unknown-model-v99", HarnessContext.Empty);

        Assert.Equal(100_000, window.Tokens);
        Assert.True(window.Assumed);
    }

    [Fact]
    public async Task Resolve_OverrideBeatsCachedDiscovery()
    {
        var source = new ModelSource
        {
            Name = "local",
            Url = "http://localhost:1234/v1",
            Models = ["gemma"],
            ContextWindow = 16_384,
        };
        await _runtime.FormatCache.SetContextWindowAsync(source.Url, "gemma", new ContextWindow(8_192, false));

        var window = await _harness.ResolveContextWindowAsync(source, "gemma", HarnessContext.Empty);

        Assert.Equal(16_384, window.Tokens);
        Assert.False(window.Assumed);
    }

    [Fact]
    public async Task Resolve_CachedAssumed_FallsBackToAssumed100k()
    {
        var source = new ModelSource
        {
            Name = "local",
            Url = "http://localhost:1234/v1",
            Models = ["gemma"],
        };
        // Simulating a migrated pre-existing cache row.
        await _runtime.FormatCache.SetContextWindowAsync(source.Url, "gemma", new ContextWindow(100_000, true));

        var window = await _harness.ResolveContextWindowAsync(source, "gemma", HarnessContext.Empty);

        Assert.Equal(100_000, window.Tokens);
        Assert.True(window.Assumed);
    }
}
