using System.Net;
using System.Text;
using System.Text.Json;
using Aria.Bridge;
using Aria.Bridge.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Guards POST /project-files/revert-checkpoint: multi-file newest-first restore, hash-guard skip
/// reporting, rewind-of-rewind under a fresh checkpoint, and null-checkpoint rows left untouched.
/// </summary>
public class RevertCheckpointTests : IDisposable
{
    private readonly WebApplicationFactory<Aria.Bridge.Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbPath;
    private readonly string _root;
    private readonly Action<string> _originalLauncher;

    public RevertCheckpointTests()
    {
        _originalLauncher = Aria.Bridge.Endpoints.SealEndpoints.LaunchSealPage;
        Aria.Bridge.Endpoints.SealEndpoints.LaunchSealPage = _ => { };

        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-rewind-test-{Guid.NewGuid():N}.db");
        _root = Path.Combine(Path.GetTempPath(), $"aria-rewind-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _factory = new WebApplicationFactory<Aria.Bridge.Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BridgeDbContext>));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddDbContext<BridgeDbContext>(opts => opts.UseSqlite($"Data Source={_dbPath}"));
                });
            });

        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        Aria.Bridge.Endpoints.SealEndpoints.LaunchSealPage = _originalLauncher;
        _client.Dispose();
        _factory.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task SeedNodeAsync()
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/soul")
        {
            Content = JsonContent(new { name = "Rewind Soul", avatarSpriteKey = (string?)null, accentColor = (string?)null })
        };
        AddLocalHeaders(create);
        (await _client.SendAsync(create)).EnsureSuccessStatusCode();

        foreach (var path in new[] { "/terminal/enable", "/terminal/projects-enable" })
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, path);
            AddLocalHeaders(msg);
            (await _client.SendAsync(msg)).EnsureSuccessStatusCode();
        }

        var cfg = new HttpRequestMessage(HttpMethod.Post, "/terminal/config")
        {
            Content = JsonContent(new { allowedPaths = new[] { _root }, blockedCommands = Array.Empty<string>() })
        };
        AddLocalHeaders(cfg);
        (await _client.SendAsync(cfg)).EnsureSuccessStatusCode();
    }

    private BridgeDbContext OpenDb()
    {
        var opts = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new BridgeDbContext(opts);
    }

    private static Dictionary<string, JsonElement> Args(object o)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(o));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private SecurityPolicy Policy() => new(AllowedPaths: [_root]);

    private async Task WriteAsync(BridgeDbContext db, string path, string content, string? checkpoint)
    {
        var r = await BuiltinTools.InvokeAsync(
            "write_file",
            Args(new { path, content }),
            Policy(),
            db,
            checkpoint: checkpoint);
        Assert.False(r.IsError, r.Text);
    }

    private async Task<HttpResponseMessage> RevertCheckpointAsync(string checkpoint)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/project-files/revert-checkpoint")
        {
            Content = JsonContent(new { checkpoint, allowedPaths = new[] { _root } })
        };
        AddLocalHeaders(msg);
        return await _client.SendAsync(msg);
    }

    [Fact]
    public async Task MultiFileCheckpoint_RevertsNewestFirstAndRestoresContent()
    {
        await SeedNodeAsync();
        using var db = OpenDb();

        var a = Path.Combine(_root, "a.txt");
        var b = Path.Combine(_root, "b.txt");
        await File.WriteAllTextAsync(a, "a0");
        await File.WriteAllTextAsync(b, "b0");

        var checkpoint = Guid.NewGuid().ToString("N");
        await WriteAsync(db, a, "a1", checkpoint);
        // Distinct CreatedAt so newest-first ordering is deterministic under SQLite TEXT timestamps.
        await Task.Delay(15);
        await WriteAsync(db, b, "b1", checkpoint);

        var response = await RevertCheckpointAsync(checkpoint);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("reverted").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("skipped").GetInt32());

        var paths = body.RootElement.GetProperty("results").EnumerateArray()
            .Select(e => e.GetProperty("path").GetString())
            .ToList();
        Assert.Equal([b, a], paths); // newest first

        Assert.Equal("a0", await File.ReadAllTextAsync(a));
        Assert.Equal("b0", await File.ReadAllTextAsync(b));
    }

    [Fact]
    public async Task HashMismatch_SkipsChangedFileButRevertsOthers()
    {
        await SeedNodeAsync();
        using var db = OpenDb();

        var okPath = Path.Combine(_root, "ok.txt");
        var dirtyPath = Path.Combine(_root, "dirty.txt");
        await File.WriteAllTextAsync(okPath, "ok0");
        await File.WriteAllTextAsync(dirtyPath, "dirty0");

        var checkpoint = Guid.NewGuid().ToString("N");
        await WriteAsync(db, okPath, "ok1", checkpoint);
        await WriteAsync(db, dirtyPath, "dirty1", checkpoint);
        await File.WriteAllTextAsync(dirtyPath, "touched out of band");

        var response = await RevertCheckpointAsync(checkpoint);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("reverted").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("skipped").GetInt32());

        Assert.Equal("ok0", await File.ReadAllTextAsync(okPath));
        Assert.Equal("touched out of band", await File.ReadAllTextAsync(dirtyPath));
    }

    [Fact]
    public async Task RewindOfRewind_RestoresAgentMutationAgain()
    {
        await SeedNodeAsync();
        using var db = OpenDb();

        var path = Path.Combine(_root, "roundtrip.txt");
        await File.WriteAllTextAsync(path, "original");

        var checkpoint = Guid.NewGuid().ToString("N");
        await WriteAsync(db, path, "modified", checkpoint);

        var first = await RevertCheckpointAsync(checkpoint);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("original", await File.ReadAllTextAsync(path));

        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var rewindCheckpoint = firstBody.RootElement.GetProperty("rewindCheckpoint").GetString()!;

        var second = await RevertCheckpointAsync(rewindCheckpoint);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("modified", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task NullCheckpointRows_AreNotTouched()
    {
        await SeedNodeAsync();
        using var db = OpenDb();

        var tagged = Path.Combine(_root, "tagged.txt");
        var legacy = Path.Combine(_root, "legacy.txt");
        await File.WriteAllTextAsync(tagged, "t0");
        await File.WriteAllTextAsync(legacy, "l0");

        var checkpoint = Guid.NewGuid().ToString("N");
        await WriteAsync(db, tagged, "t1", checkpoint);
        await WriteAsync(db, legacy, "l1", checkpoint: null);

        var response = await RevertCheckpointAsync(checkpoint);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("t0", await File.ReadAllTextAsync(tagged));
        Assert.Equal("l1", await File.ReadAllTextAsync(legacy));

        var legacyUndo = await db.FileUndos.SingleAsync(u => u.Path == legacy);
        Assert.Null(legacyUndo.Checkpoint);
        Assert.Null(legacyUndo.RevertedAt);
    }

    private static void AddLocalHeaders(HttpRequestMessage msg)
    {
        msg.Headers.Host = "localhost:5741";
        msg.Headers.Add("Origin", "http://localhost:5741");
    }

    private static HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
