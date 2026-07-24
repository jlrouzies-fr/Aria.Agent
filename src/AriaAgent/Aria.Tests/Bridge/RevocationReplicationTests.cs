using Aria.Bridge.Data;
using Aria.Bridge.Infrastructure;
using Aria.Shared;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Verifies revocation replication: a revoke on one node mints a node-signed tombstone that rides the
/// grant export/import channel to its siblings — killing the replicated grant there before expiry,
/// winning even when it arrives before the grant (out-of-order delivery), and rejecting foreign-signed
/// or unsigned tombstones. Covers both path grants (Wave 5) and plain context grants, which share
/// <see cref="ContextGrantStore.RevokeAsync"/>.
/// </summary>
public class RevocationReplicationTests : IDisposable
{
    // _nodeA mints and revokes; _nodeB is the sibling the grant (and tombstone) replicates to.
    private readonly string _dbPathA;
    private readonly string _dbPathB;
    private readonly BridgeDbContext _nodeA;
    private readonly BridgeDbContext _nodeB;
    private readonly BridgeSoul _soul;

    public RevocationReplicationTests()
    {
        _dbPathA = Path.Combine(Path.GetTempPath(), $"aria-rev-a-{Guid.NewGuid():N}.db");
        _dbPathB = Path.Combine(Path.GetTempPath(), $"aria-rev-b-{Guid.NewGuid():N}.db");
        _nodeA = NewDb(_dbPathA);
        _nodeB = NewDb(_dbPathB);
        _soul = TestCrypto.GenerateSoul(out _);
        _nodeA.Souls.Add(_soul);
        _nodeA.SaveChanges();
    }

    private static BridgeDbContext NewDb(string path)
    {
        var opts = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        var db = new BridgeDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    public void Dispose()
    {
        _nodeA.Dispose();
        _nodeB.Dispose();
        try { File.Delete(_dbPathA); } catch { }
        try { File.Delete(_dbPathB); } catch { }
    }

    private static string TempDir(string name) =>
        Path.Combine(Path.GetTempPath(), $"aria-rev-{name}-{Guid.NewGuid():N}");

    // One GrantReplicationService tick between the two nodes, at the store level.
    private async Task ReplicateAsync(BridgeDbContext from, BridgeDbContext to)
    {
        foreach (var g in await ContextGrantStore.ExportLiveGrantsAsync(from))
            await ContextGrantStore.ImportGrantAsync(to, _soul, g);
        foreach (var t in await ContextGrantStore.ExportRevocationsAsync(from))
            await ContextGrantStore.ImportRevocationAsync(to, _soul, t.ContextId, t.GrantExpiryUnix, t.SignatureBase64);
    }

    [Fact]
    public async Task PathGrant_Revocation_Replicates_KillsGrantBeforeExpiry()
    {
        var dir = TempDir("path");
        await ContextGrantStore.GrantPathAsync(_nodeA, _soul, "sess-1", dir, TimeSpan.FromHours(8));
        await ReplicateAsync(_nodeA, _nodeB);
        Assert.True(await ContextGrantStore.HasLivePathGrantAsync(_nodeB, _soul, "sess-1", dir));

        await ContextGrantStore.RevokePathAsync(_nodeA, _soul.ServerSoulId!, "sess-1", dir);
        await ReplicateAsync(_nodeA, _nodeB);

        // The replicated copy dies on the sibling too — 8h before it would merely have lapsed.
        Assert.False(await ContextGrantStore.HasLivePathGrantAsync(_nodeB, _soul, "sess-1", dir));
        Assert.Empty(await ContextGrantStore.GetLiveSessionPathGrantsAsync(_nodeB, _soul, "sess-1"));
    }

    [Fact]
    public async Task ContextGrant_Revocation_ReplicatesToo()
    {
        var ctxId = ContextGrantStore.ContextId(_soul.ServerSoulId!, "sess-1");
        await ContextGrantStore.GrantAsync(_nodeA, _soul, ctxId, TimeSpan.FromHours(8));
        await ReplicateAsync(_nodeA, _nodeB);
        Assert.True(await ContextGrantStore.HasValidGrantForRequestAsync(_nodeB, _soul, "sess-1"));

        await ContextGrantStore.RevokeAsync(_nodeA, ctxId);
        await ReplicateAsync(_nodeA, _nodeB);

        Assert.False(await ContextGrantStore.HasValidGrantForRequestAsync(_nodeB, _soul, "sess-1"));
    }

    [Fact]
    public async Task OutOfOrder_TombstoneArrivesBeforeGrant_StillWins()
    {
        var dir = TempDir("ooo");
        await ContextGrantStore.GrantPathAsync(_nodeA, _soul, "sess-1", dir, TimeSpan.FromHours(8));
        var original = Assert.Single(await ContextGrantStore.ExportLiveGrantsAsync(_nodeA));
        await ContextGrantStore.RevokePathAsync(_nodeA, _soul.ServerSoulId!, "sess-1", dir);

        // The tombstone replicates BEFORE the grant it kills (out-of-order delivery).
        foreach (var t in await ContextGrantStore.ExportRevocationsAsync(_nodeA))
            Assert.True(await ContextGrantStore.ImportRevocationAsync(
                _nodeB, _soul, t.ContextId, t.GrantExpiryUnix, t.SignatureBase64));

        // The revoked grant instance — the exact row the relay exported before the revoke — arrives
        // late: the tombstone blocks its import even though its signature is perfectly valid.
        Assert.False(await ContextGrantStore.ImportGrantAsync(_nodeB, _soul, original));

        Assert.False(await ContextGrantStore.HasLivePathGrantAsync(_nodeB, _soul, "sess-1", dir));
        Assert.Empty(await ContextGrantStore.GetLiveSessionPathGrantsAsync(_nodeB, _soul, "sess-1"));
    }

    [Fact]
    public async Task ForeignSigned_Revocation_IsRejected()
    {
        var dir = TempDir("rogue");
        await ContextGrantStore.GrantPathAsync(_nodeA, _soul, "sess-1", dir, TimeSpan.FromHours(8));
        await ReplicateAsync(_nodeA, _nodeB);
        Assert.True(await ContextGrantStore.HasLivePathGrantAsync(_nodeB, _soul, "sess-1", dir));

        // A revocation signed by a key the soul does not trust (server forgery attempt).
        TestCrypto.GenerateNode(out var rogueKey);
        var ctxId = ContextGrantStore.PathGrantContextId(_soul.ServerSoulId!, "sess-1", dir);
        var expiry = DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds();
        var payload = GrantCanonical.RevocationPayload(ctxId, expiry);
        var sig = Convert.ToBase64String(
            rogueKey.SignData(payload, System.Security.Cryptography.HashAlgorithmName.SHA256));

        Assert.False(await ContextGrantStore.ImportRevocationAsync(_nodeB, _soul, ctxId, expiry, sig));
        Assert.True(await ContextGrantStore.HasLivePathGrantAsync(_nodeB, _soul, "sess-1", dir));
    }

    [Fact]
    public async Task Unsigned_Revocation_IsRejected()
    {
        var dir = TempDir("unsigned");
        await ContextGrantStore.GrantPathAsync(_nodeA, _soul, "sess-1", dir, TimeSpan.FromHours(8));
        await ReplicateAsync(_nodeA, _nodeB);

        var ctxId = ContextGrantStore.PathGrantContextId(_soul.ServerSoulId!, "sess-1", dir);
        var expiry = DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds();

        Assert.False(await ContextGrantStore.ImportRevocationAsync(_nodeB, _soul, ctxId, expiry, null));
        Assert.True(await ContextGrantStore.HasLivePathGrantAsync(_nodeB, _soul, "sess-1", dir));
    }

    [Fact]
    public async Task Reapproval_WithLaterExpiry_ImportsDespiteOlderTombstone()
    {
        var dir = TempDir("regrant");
        await ContextGrantStore.GrantPathAsync(_nodeA, _soul, "sess-1", dir, TimeSpan.FromHours(1));
        await ContextGrantStore.RevokePathAsync(_nodeA, _soul.ServerSoulId!, "sess-1", dir);
        await ReplicateAsync(_nodeA, _nodeB);   // tombstone for the 1h instance lands on the sibling

        // The human re-approves: a fresh grant instance with a LATER expiry is not the revoked one.
        await ContextGrantStore.GrantPathAsync(_nodeA, _soul, "sess-1", dir, TimeSpan.FromHours(8));
        await ReplicateAsync(_nodeA, _nodeB);

        Assert.True(await ContextGrantStore.HasLivePathGrantAsync(_nodeB, _soul, "sess-1", dir));
    }

    [Fact]
    public async Task ExpiredTombstone_IsNotExported()
    {
        // A tombstone whose revoked grant would already fail closed on expiry is dead weight.
        _nodeA.ContextGrantTombstones.Add(new ContextGrantTombstone
        {
            ContextId = ContextGrantStore.PathGrantContextId(_soul.ServerSoulId!, "sess-1", "/tmp/x"),
            GrantExpiryUnix = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(),
            SignatureBase64 = "AAAA",
        });
        await _nodeA.SaveChangesAsync();

        Assert.Empty(await ContextGrantStore.ExportRevocationsAsync(_nodeA));
    }
}
