using Aria.Bridge.Data;
using Aria.Bridge.Infrastructure;
using Aria.Shared;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Verifies Wave 5 session path grants in <see cref="ContextGrantStore"/>: mint/verify/expiry/revoke,
/// session scoping, and that unsigned, foreign-signed, or tampered grants are rejected — the same
/// trust rules as context grants, with the path carried as part of the signed claim.
/// </summary>
public class SessionPathGrantStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly BridgeDbContext _db;

    public SessionPathGrantStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-spg-tests-{Guid.NewGuid():N}.db");
        var opts = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _db = new BridgeDbContext(opts);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static string TempDir(string name) =>
        Path.Combine(Path.GetTempPath(), $"aria-spg-{name}-{Guid.NewGuid():N}");

    [Fact]
    public async Task MintedGrant_AppearsInLiveList_AndVerifies()
    {
        var soul = TestCrypto.GenerateSoul(out _);
        var dir = TempDir("mint");

        var ok = await ContextGrantStore.GrantPathAsync(_db, soul, "sess-1", dir, TimeSpan.FromHours(8));

        Assert.True(ok);
        var live = await ContextGrantStore.GetLiveSessionPathGrantsAsync(_db, soul, "sess-1");
        var grant = Assert.Single(live);
        Assert.Equal(dir, grant.Path);
        Assert.True(grant.ExpiryUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.True(await ContextGrantStore.HasLivePathGrantAsync(_db, soul, "sess-1", dir));
    }

    [Fact]
    public async Task Grant_IsScopedToItsSession_Only()
    {
        var soul = TestCrypto.GenerateSoul(out _);
        var dir = TempDir("scope");
        await ContextGrantStore.GrantPathAsync(_db, soul, "sess-1", dir, TimeSpan.FromHours(8));

        Assert.Empty(await ContextGrantStore.GetLiveSessionPathGrantsAsync(_db, soul, "sess-2"));
        Assert.False(await ContextGrantStore.HasLivePathGrantAsync(_db, soul, "sess-2", dir));
        Assert.Empty(await ContextGrantStore.GetLiveSessionPathGrantsAsync(_db, soul, null));
    }

    [Fact]
    public async Task Grant_RequiresSessionAndPath()
    {
        var soul = TestCrypto.GenerateSoul(out _);

        Assert.False(await ContextGrantStore.GrantPathAsync(_db, soul, null, "/tmp/x", TimeSpan.FromHours(1)));
        Assert.False(await ContextGrantStore.GrantPathAsync(_db, soul, "sess-1", "", TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task ExpiredGrant_FailsClosed()
    {
        var soul = TestCrypto.GenerateSoul(out var soulKey);
        var dir = TempDir("expired");
        var ctxId = ContextGrantStore.PathGrantContextId(soul.ServerSoulId!, "sess-1", dir);

        // Insert an already-expired row with a VALID signature — expiry alone must exclude it.
        var expiry = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var payload = GrantCanonical.Payload(ContextGrantStore.PathGrantType, ctxId, ctxId, expiry);
        _db.ContextGrants.Add(new ContextGrant
        {
            ContextId = ctxId,
            GrantType = ContextGrantStore.PathGrantType,
            ExpiryUnix = expiry,
            SignatureBase64 = Convert.ToBase64String(soulKey.SignData(payload, System.Security.Cryptography.HashAlgorithmName.SHA256)),
        });
        await _db.SaveChangesAsync();

        Assert.Empty(await ContextGrantStore.GetLiveSessionPathGrantsAsync(_db, soul, "sess-1"));
        Assert.False(await ContextGrantStore.HasLivePathGrantAsync(_db, soul, "sess-1", dir));
    }

    [Fact]
    public async Task PastExpiryMint_IsRefused()
    {
        var soul = TestCrypto.GenerateSoul(out _);
        var ctxId = ContextGrantStore.PathGrantContextId(soul.ServerSoulId!, "sess-1", "/tmp/x");

        var ok = await ContextGrantStore.GrantAtAsync(
            _db, soul, ctxId, DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(),
            grantType: ContextGrantStore.PathGrantType);

        Assert.False(ok);
    }

    [Fact]
    public async Task RevokedGrant_FailsClosed()
    {
        var soul = TestCrypto.GenerateSoul(out _);
        var dir = TempDir("revoked");
        await ContextGrantStore.GrantPathAsync(_db, soul, "sess-1", dir, TimeSpan.FromHours(8));

        await ContextGrantStore.RevokePathAsync(_db, soul.ServerSoulId!, "sess-1", dir);

        Assert.Empty(await ContextGrantStore.GetLiveSessionPathGrantsAsync(_db, soul, "sess-1"));
        Assert.False(await ContextGrantStore.HasLivePathGrantAsync(_db, soul, "sess-1", dir));
    }

    [Fact]
    public async Task RogueSignedGrant_IsRejected()
    {
        var soul = TestCrypto.GenerateSoul(out _);
        var rogue = TestCrypto.GenerateNode(out var rogueKey);
        var dir = TempDir("rogue");
        var ctxId = ContextGrantStore.PathGrantContextId(soul.ServerSoulId!, "sess-1", dir);

        var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = GrantCanonical.Payload(ContextGrantStore.PathGrantType, ctxId, ctxId, expiry);
        _db.ContextGrants.Add(new ContextGrant
        {
            ContextId = ctxId,
            GrantType = ContextGrantStore.PathGrantType,
            ExpiryUnix = expiry,
            SignatureBase64 = Convert.ToBase64String(rogueKey.SignData(payload, System.Security.Cryptography.HashAlgorithmName.SHA256)),
        });
        await _db.SaveChangesAsync();

        Assert.Empty(await ContextGrantStore.GetLiveSessionPathGrantsAsync(_db, soul, "sess-1"));
        Assert.False(await ContextGrantStore.HasLivePathGrantAsync(_db, soul, "sess-1", dir));
    }

    [Fact]
    public async Task TamperedPath_InContextId_IsRejected()
    {
        var soul = TestCrypto.GenerateSoul(out var soulKey);
        var signedDir = TempDir("signed");
        var otherDir  = TempDir("other");

        // The node signed signedDir; a tampered local row (or a server relay) points the claim at
        // otherDir instead. The signature no longer matches the context id → rejected.
        var signedCtxId = ContextGrantStore.PathGrantContextId(soul.ServerSoulId!, "sess-1", signedDir);
        var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = GrantCanonical.Payload(ContextGrantStore.PathGrantType, signedCtxId, signedCtxId, expiry);
        _db.ContextGrants.Add(new ContextGrant
        {
            ContextId = ContextGrantStore.PathGrantContextId(soul.ServerSoulId!, "sess-1", otherDir),
            GrantType = ContextGrantStore.PathGrantType,
            ExpiryUnix = expiry,
            SignatureBase64 = Convert.ToBase64String(soulKey.SignData(payload, System.Security.Cryptography.HashAlgorithmName.SHA256)),
        });
        await _db.SaveChangesAsync();

        Assert.Empty(await ContextGrantStore.GetLiveSessionPathGrantsAsync(_db, soul, "sess-1"));
        Assert.False(await ContextGrantStore.HasLivePathGrantAsync(_db, soul, "sess-1", otherDir));
    }

    [Fact]
    public async Task UnsignedGrant_IsRejected()
    {
        var soul = TestCrypto.GenerateSoul(out _);
        var dir = TempDir("unsigned");
        _db.ContextGrants.Add(new ContextGrant
        {
            ContextId = ContextGrantStore.PathGrantContextId(soul.ServerSoulId!, "sess-1", dir),
            GrantType = ContextGrantStore.PathGrantType,
            ExpiryUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            SignatureBase64 = null,
        });
        await _db.SaveChangesAsync();

        Assert.Empty(await ContextGrantStore.GetLiveSessionPathGrantsAsync(_db, soul, "sess-1"));
    }

    [Fact]
    public async Task PathGrant_NeverSatisfiesTheContextGate()
    {
        var soul = TestCrypto.GenerateSoul(out _);
        var dir = TempDir("noctx");
        await ContextGrantStore.GrantPathAsync(_db, soul, "sess-1", dir, TimeSpan.FromHours(8));

        // A path expansion is NOT a Layer B sensitive-ops pass — different grant namespace.
        Assert.False(await ContextGrantStore.HasValidGrantForRequestAsync(_db, soul, "sess-1"));
    }
}
