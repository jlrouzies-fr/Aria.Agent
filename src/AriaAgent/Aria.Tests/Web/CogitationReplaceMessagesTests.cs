using Aria.Web.Data.Cogitations;
using Aria.Web.Data.Context;
using Aria.Web.Data.Users;
using Aria.Web.Services.Cogitations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aria.Tests.Web;

/// <summary>
/// Covers the shared transcript rewrite primitive used by Compact and edit-and-replay.
/// </summary>
public class CogitationReplaceMessagesTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ServiceProvider _provider;
    private readonly CogitationService _svc;

    public CogitationReplaceMessagesTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-replace-msgs-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var db = dbFactory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Set<User>().Add(new User { Id = "soul-1", Name = "Test Soul" });
        db.SaveChanges();

        _svc = new CogitationService(dbFactory);
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task<int> SeedLegacyCogitationAsync()
    {
        var cog = await _svc.CreateAsync("soul-1", originNodeId: null);
        await _svc.AddMessageAsync(cog.Id, "user", "first");
        await _svc.AddMessageAsync(cog.Id, "assistant", "reply-one");
        await _svc.AddMessageAsync(cog.Id, "user", "second");
        await _svc.AddMessageAsync(cog.Id, "assistant", "reply-two");
        return cog.Id;
    }

    [Fact]
    public async Task ReplaceMessages_KeepsOrderAndDropsTail()
    {
        var cogId = await SeedLegacyCogitationAsync();

        await _svc.ReplaceMessagesAsync(cogId,
        [
            new TranscriptMessageWrite("user", "first"),
            new TranscriptMessageWrite("assistant", "reply-one"),
        ]);

        var msgs = await _svc.GetMessagesAsync(cogId);
        Assert.Equal(2, msgs.Count);
        Assert.Equal("user", msgs[0].Role);
        Assert.Equal("first", msgs[0].Content);
        Assert.Equal("assistant", msgs[1].Role);
        Assert.Equal("reply-one", msgs[1].Content);
        Assert.True(msgs[0].CreatedAt <= msgs[1].CreatedAt);
    }

    [Fact]
    public async Task CompactAsync_LeavesSingleSummaryViaReplace()
    {
        var cogId = await SeedLegacyCogitationAsync();

        await _svc.CompactAsync(cogId, "summary of all prior turns");

        var msgs = await _svc.GetMessagesAsync(cogId);
        Assert.Single(msgs);
        Assert.Equal("assistant", msgs[0].Role);
        Assert.Equal("summary of all prior turns", msgs[0].Content);
    }

    [Fact]
    public async Task ReplaceMessages_IsNoOpForBridgeOwned()
    {
        var cog = await _svc.CreateAsync("soul-1", originNodeId: "node-a");
        // Bridge-owned AddMessage is also a no-op; seed directly.
        var dbFactory = _provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.CogitationMessages.Add(new CogitationMessage
            {
                CogitationId = cog.Id,
                Role = "user",
                Content = "should-remain",
            });
            await db.SaveChangesAsync();
        }

        await _svc.ReplaceMessagesAsync(cog.Id,
        [
            new TranscriptMessageWrite("assistant", "should-not-write"),
        ]);

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var msgs = await db.CogitationMessages
                .Where(m => m.CogitationId == cog.Id)
                .ToListAsync();
            Assert.Single(msgs);
            Assert.Equal("should-remain", msgs[0].Content);
        }
    }
}
