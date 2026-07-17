using Aria.Bridge.Data;
using Aria.Bridge.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Guards the format-detection probe URL. Regression: <see cref="LlmKeyEndpoints.ResolveProbeTargetAsync"/>
/// used to pin a keyed probe to the channel's declared BASE url verbatim, dropping the request PATH — so
/// the probe POSTed to the bare base (…/v1) and LM Studio answered with its "unexpected endpoint" help
/// body, making format detection fail forever. It must pin only the HOST and keep /chat/completions, the
/// same rule the /llm/proxy path already enforced.
/// </summary>
public class LlmProbeTargetTests : IDisposable
{
    private readonly WebApplicationFactory<Aria.Bridge.Program> _factory;
    private readonly string _dbPath;

    public LlmProbeTargetTests()
    {
        // WebApplicationFactory so full startup runs the raw-SQL schema init (the LlmKeys table read by
        // the key-custody step isn't an EF entity, so EnsureCreated alone wouldn't create it).
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-probe-tests-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Aria.Bridge.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BridgeDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<BridgeDbContext>(opts => opts.UseSqlite($"Data Source={_dbPath}"));
            }));
        _ = _factory.Server; // force startup (schema init)
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task<(string Url, string? Key)> ResolveAsync(string? keyRef, string requestedUrl, string? channelBaseUrl)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        if (channelBaseUrl != null && keyRef != null &&
            !await db.Channels.AnyAsync(c => c.Name == keyRef))
        {
            db.Channels.Add(new BridgeChannel { Name = keyRef, Url = channelBaseUrl, IsBridged = true });
            await db.SaveChangesAsync();
        }
        return await LlmKeyEndpoints.ResolveProbeTargetAsync(db, keyRef, requestedUrl, apiKey: null);
    }

    [Fact]
    public async Task ResolveProbeTarget_KeepsCompletionsPath()
    {
        // LM Studio channel: base url ends at /v1, the harness requests /v1/chat/completions.
        var (url, _) = await ResolveAsync(
            "My Local LLM", "http://127.0.0.1:1234/v1/chat/completions", channelBaseUrl: "http://127.0.0.1:1234/v1");

        // The whole point: the path survives pinning (it did NOT become the bare "…/v1").
        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", url);
    }

    [Fact]
    public async Task ResolveProbeTarget_PinsTamperedHostButKeepsPath()
    {
        // A compromised server points the probe at its own host; pinning forces it back to the node's
        // declared host while still keeping the completions path.
        var (url, _) = await ResolveAsync(
            "My Local LLM", "http://evil.example/v1/chat/completions", channelBaseUrl: "http://127.0.0.1:1234/v1");

        Assert.StartsWith("http://127.0.0.1:1234/", url);
        Assert.DoesNotContain("evil", url);
        Assert.EndsWith("/v1/chat/completions", url);
    }

    [Fact]
    public async Task ResolveProbeTarget_UnknownChannelUsesRequestedUrl()
    {
        // No channel record for this keyRef: connectivity is still probed against the requested url (no
        // authoritative base to pin to), and no stored key is attached.
        var (url, key) = await ResolveAsync(
            "Nonexistent", "http://127.0.0.1:1234/v1/chat/completions", channelBaseUrl: null);

        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", url);
        Assert.Null(key);
    }
}
