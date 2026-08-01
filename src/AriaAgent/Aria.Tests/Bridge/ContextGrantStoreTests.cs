using Aria.Bridge.Data;
using Aria.Bridge.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Verifies that <see cref="ContextGrantStore"/> accepts grants signed by the soul master key, this node's
/// own key, or a locally-verified sibling key — and rejects grants signed by an unverified/rogue key even
/// if the server relayed them.
/// </summary>
public class ContextGrantStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly BridgeDbContext _db;

    public ContextGrantStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-cg-tests-{Guid.NewGuid():N}.db");
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

    private static BridgeSoul Soul(string serverSoulId, string publicKey, string? nodePublicKey = null) =>
        new() { ServerSoulId = serverSoulId, PublicKeyBase64 = publicKey, NodePublicKeyBase64 = nodePublicKey };

    [Fact]
    public async Task SoulSignedGrant_Verifies()
    {
        var soul = TestCrypto.GenerateSoul(out var soulKey);
        var ctxId = ContextGrantStore.ContextId(soul.ServerSoulId!, null);
        await ContextGrantStore.GrantAsync(_db, soul, ctxId, TimeSpan.FromHours(1));

        Assert.True(await ContextGrantStore.HasValidGrantAsync(_db, soul, ctxId));
    }

    [Fact]
    public async Task NodeSignedGrant_Verifies()
    {
        var soul = TestCrypto.GenerateSoul(out _);
        var node = TestCrypto.GenerateNode(out _);
        soul.NodePublicKeyBase64 = node.NodePublicKeyBase64;
        soul.NodePrivateKeyBase64 = node.NodePrivateKeyBase64;

        var ctxId = ContextGrantStore.ContextId(soul.ServerSoulId!, null);
        await ContextGrantStore.GrantAsync(_db, soul, ctxId, TimeSpan.FromHours(1));

        Assert.True(await ContextGrantStore.HasValidGrantAsync(_db, soul, ctxId));
    }

    [Fact]
    public async Task SiblingSignedGrant_WhenSiblingTrusted_Verifies()
    {
        var soul = TestCrypto.GenerateSoul(out _);
        var sibling = TestCrypto.GenerateNode(out var siblingKey);

        // Seed the local trust table as if SiblingRoster verified this node.
        _db.TrustedSiblingKeys.Add(new TrustedSiblingKey
        {
            UserId = soul.ServerSoulId!,
            NodeId = Aria.Shared.NodeCrypto.Thumbprint(sibling.NodePublicKeyBase64!),
            NodePublicKeyBase64 = sibling.NodePublicKeyBase64!,
            CertifiedByPublicKeyBase64 = soul.PublicKeyBase64!,
        });
        await _db.SaveChangesAsync();

        // Sign a grant with the sibling key.
        var ctxId = ContextGrantStore.ContextId(soul.ServerSoulId!, null);
        var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = Aria.Shared.NodeCrypto.GrantPayload("context", ctxId, ctxId, expiry);
        var signature = Convert.ToBase64String(siblingKey.SignData(payload, System.Security.Cryptography.HashAlgorithmName.SHA256));

        _db.ContextGrants.Add(new ContextGrant
        {
            ContextId = ctxId,
            GrantType = "context",
            ExpiryUnix = expiry,
            SignatureBase64 = signature,
        });
        await _db.SaveChangesAsync();

        Assert.True(await ContextGrantStore.HasValidGrantAsync(_db, soul, ctxId));
    }

    [Fact]
    public async Task RogueSignedGrant_NotVerified()
    {
        var soul = TestCrypto.GenerateSoul(out _);
        var rogue = TestCrypto.GenerateNode(out var rogueKey);

        var ctxId = ContextGrantStore.ContextId(soul.ServerSoulId!, null);
        var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = Aria.Shared.NodeCrypto.GrantPayload("context", ctxId, ctxId, expiry);
        var signature = Convert.ToBase64String(rogueKey.SignData(payload, System.Security.Cryptography.HashAlgorithmName.SHA256));

        _db.ContextGrants.Add(new ContextGrant
        {
            ContextId = ctxId,
            GrantType = "context",
            ExpiryUnix = expiry,
            SignatureBase64 = signature,
        });
        await _db.SaveChangesAsync();

        // The rogue key is not in the trusted set, so the grant must be rejected.
        Assert.False(await ContextGrantStore.HasValidGrantAsync(_db, soul, ctxId));
    }

    [Fact]
    public async Task JoinedNode_AcceptsPrimarySignedGrant_AfterSoulKeyCached()
    {
        var soul = TestCrypto.GenerateSoul(out var soulKey);
        var joined = TestCrypto.GenerateNode(out _);
        joined.ServerSoulId = soul.ServerSoulId;
        // Simulate SiblingRoster caching the soul master public key on a joined node.
        joined.PublicKeyBase64 = soul.PublicKeyBase64;

        var ctxId = ContextGrantStore.ContextId(soul.ServerSoulId!, "sess-windows");
        var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = Aria.Shared.NodeCrypto.GrantPayload("context", ctxId, ctxId, expiry);
        var signature = Convert.ToBase64String(
            soulKey.SignData(payload, System.Security.Cryptography.HashAlgorithmName.SHA256));

        _db.ContextGrants.Add(new ContextGrant
        {
            ContextId = ctxId,
            GrantType = "context",
            ExpiryUnix = expiry,
            SignatureBase64 = signature,
        });
        await _db.SaveChangesAsync();

        Assert.True(await ContextGrantStore.HasValidGrantForRequestAsync(_db, joined, "sess-windows"));
    }

    [Fact]
    public async Task JoinedNode_WithoutSoulKeyCache_RejectsPrimarySignedGrant()
    {
        var soul = TestCrypto.GenerateSoul(out var soulKey);
        var joined = TestCrypto.GenerateNode(out _);
        joined.ServerSoulId = soul.ServerSoulId;
        joined.PublicKeyBase64 = null;

        var ctxId = ContextGrantStore.ContextId(soul.ServerSoulId!, "sess-windows");
        var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = Aria.Shared.NodeCrypto.GrantPayload("context", ctxId, ctxId, expiry);
        var signature = Convert.ToBase64String(
            soulKey.SignData(payload, System.Security.Cryptography.HashAlgorithmName.SHA256));

        _db.ContextGrants.Add(new ContextGrant
        {
            ContextId = ctxId,
            GrantType = "context",
            ExpiryUnix = expiry,
            SignatureBase64 = signature,
        });
        await _db.SaveChangesAsync();

        Assert.False(await ContextGrantStore.HasValidGrantForRequestAsync(_db, joined, "sess-windows"));
    }
}
