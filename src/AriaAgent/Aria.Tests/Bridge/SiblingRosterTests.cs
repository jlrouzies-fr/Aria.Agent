using System.Security.Cryptography;
using Aria.Bridge.Data;
using Aria.Bridge.Services.Trust;
using Aria.Shared;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Trust-critical tests for Layer B Phase 2 co-equal approval: a bridge accepts a sibling node key only
/// when its enrollment certificate chains to a key the bridge already trusts (the soul master key or a
/// previously-verified sibling). The server cannot inject an acceptable key by adding a row to the roster.
/// </summary>
public class SiblingRosterTests
{
    private static (string PubB64, ECDsa Key) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), key);
    }

    private static string SignEnrollment(ECDsa signer, string soulId, string nodePub, string label, long expiry)
    {
        var payload = NodeCrypto.EnrollPayload(soulId, nodePub, label, expiry);
        return Convert.ToBase64String(signer.SignData(payload, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void SoulSignedCert_IsTrusted()
    {
        var (soulPub, soulKey) = NewKey();
        var (nodePub, _) = NewKey();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var cert = SignEnrollment(soulKey, "soul-1", nodePub, "laptop", expiry);

        var entry = new SoulNodeRosterEntry(nodePub, cert, soulPub, "laptop", expiry, IsPrimary: false);
        var trusted = SiblingRoster.TryVerifyEntry("soul-1", soulPub, new Dictionary<string, string>(), entry);

        Assert.NotNull(trusted);
        Assert.Equal(nodePub, trusted.NodePublicKeyBase64);
        Assert.Equal(NodeCrypto.Thumbprint(nodePub), trusted.NodeId);
        Assert.Equal(soulPub, trusted.CertifiedByPublicKeyBase64);
    }

    [Fact]
    public void SiblingSignedCert_WhenSiblingAlreadyTrusted_IsTrusted()
    {
        var (soulPub, soulKey) = NewKey();
        var (siblingAPub, siblingAKey) = NewKey();
        var (siblingBPub, _) = NewKey();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();

        // Sibling A was previously trusted (e.g., enrolled directly by the soul).
        var siblingAThumb = NodeCrypto.Thumbprint(siblingAPub);
        var existing = new Dictionary<string, string> { [siblingAThumb] = siblingAPub };

        // Sibling B is enrolled by Sibling A.
        var certB = SignEnrollment(siblingAKey, "soul-1", siblingBPub, "server", expiry);
        var entry = new SoulNodeRosterEntry(siblingBPub, certB, siblingAPub, "server", expiry, IsPrimary: false);
        var trusted = SiblingRoster.TryVerifyEntry("soul-1", soulPub, existing, entry);

        Assert.NotNull(trusted);
        Assert.Equal(siblingBPub, trusted.NodePublicKeyBase64);
        Assert.Equal(siblingAPub, trusted.CertifiedByPublicKeyBase64);
    }

    [Fact]
    public void RogueApprover_NotTrusted()
    {
        var (soulPub, _) = NewKey();
        var (roguePub, rogueKey) = NewKey();
        var (nodePub, _) = NewKey();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();

        var cert = SignEnrollment(rogueKey, "soul-1", nodePub, "evil", expiry);
        var entry = new SoulNodeRosterEntry(nodePub, cert, roguePub, "evil", expiry, IsPrimary: false);
        var trusted = SiblingRoster.TryVerifyEntry("soul-1", soulPub, new Dictionary<string, string>(), entry);

        Assert.Null(trusted);
    }

    [Fact]
    public void TamperedLabel_NotTrusted()
    {
        var (soulPub, soulKey) = NewKey();
        var (nodePub, _) = NewKey();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();

        var cert = SignEnrollment(soulKey, "soul-1", nodePub, "laptop", expiry);
        // Server changes the label in transit.
        var entry = new SoulNodeRosterEntry(nodePub, cert, soulPub, "server", expiry, IsPrimary: false);
        var trusted = SiblingRoster.TryVerifyEntry("soul-1", soulPub, new Dictionary<string, string>(), entry);

        Assert.Null(trusted);
    }

    [Fact]
    public void TamperedNodeKey_NotTrusted()
    {
        var (soulPub, soulKey) = NewKey();
        var (nodePub, _) = NewKey();
        var (otherPub, _) = NewKey();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();

        var cert = SignEnrollment(soulKey, "soul-1", nodePub, "laptop", expiry);
        // Server swaps in a different node public key but keeps the same cert.
        var entry = new SoulNodeRosterEntry(otherPub, cert, soulPub, "laptop", expiry, IsPrimary: false);
        var trusted = SiblingRoster.TryVerifyEntry("soul-1", soulPub, new Dictionary<string, string>(), entry);

        Assert.Null(trusted);
    }

    [Fact]
    public void PrimaryNode_IsNotAddedAsSibling()
    {
        var (soulPub, _) = NewKey();
        var entry = new SoulNodeRosterEntry(soulPub, null, null, "primary", 0, IsPrimary: true);
        var trusted = SiblingRoster.TryVerifyEntry("soul-1", soulPub, new Dictionary<string, string>(), entry);

        Assert.Null(trusted);
    }

    [Fact]
    public void MissingCert_NotTrusted()
    {
        var (soulPub, _) = NewKey();
        var (nodePub, _) = NewKey();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();

        var entry = new SoulNodeRosterEntry(nodePub, EnrollmentCertB64: null, soulPub, "laptop", expiry, IsPrimary: false);
        var trusted = SiblingRoster.TryVerifyEntry("soul-1", soulPub, new Dictionary<string, string>(), entry);

        Assert.Null(trusted);
    }

    [Fact]
    public void JoinedNode_UsesPinnedSoulKey_WhenRosterPrimaryMatches()
    {
        var (soulPub, _) = NewKey();
        var (nodePub, _) = NewKey();

        var roster = new List<SoulNodeRosterEntry>
        {
            new(soulPub, null, null, "primary", 0, IsPrimary: true),
        };
        var joined = new BridgeSoul
        {
            ServerSoulId        = "soul-1",
            NodePublicKeyBase64 = nodePub,
            PublicKeyBase64     = soulPub,
            SoulKeyPinnedAt     = DateTime.UtcNow,
        };

        var resolved = SiblingRoster.ResolveSoulMasterPublicKey(joined, roster, out var trust);

        Assert.Equal(soulPub, resolved);
        Assert.Equal(SiblingRoster.SoulKeyTrust.Trusted, trust);
    }

    [Fact]
    public void JoinedNode_WithoutPin_RefusesToResolveAnyKey()
    {
        var (soulPub, soulKey) = NewKey();
        var (nodePub, _) = NewKey();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        // A perfectly genuine roster is still not an anchor — only a human at the node is.
        var cert = SignEnrollment(soulKey, "soul-1", nodePub, "windows", expiry);

        var roster = new List<SoulNodeRosterEntry>
        {
            new(soulPub, null, null, "primary", 0, IsPrimary: true),
            new(nodePub, cert, soulPub, "windows", expiry, IsPrimary: false),
        };
        var joined = new BridgeSoul
        {
            ServerSoulId        = "soul-1",
            NodePublicKeyBase64 = nodePub,
        };

        Assert.Null(SiblingRoster.ResolveSoulMasterPublicKey(joined, roster, out var trust));
        Assert.Equal(SiblingRoster.SoulKeyTrust.NotPinned, trust);
    }

    [Fact]
    public void JoinedNode_IgnoresKeyCachedByOlderBuildWithoutAPin()
    {
        var (soulPub, _) = NewKey();
        var (nodePub, _) = NewKey();

        var roster = new List<SoulNodeRosterEntry> { new(soulPub, null, null, "primary", 0, IsPrimary: true) };
        // PublicKeyBase64 present but never human-confirmed: written by the roster-deriving build.
        var joined = new BridgeSoul
        {
            ServerSoulId        = "soul-1",
            NodePublicKeyBase64 = nodePub,
            PublicKeyBase64     = soulPub,
            SoulKeyPinnedAt     = null,
        };

        Assert.Null(SiblingRoster.ResolveSoulMasterPublicKey(joined, roster, out var trust));
        Assert.Equal(SiblingRoster.SoulKeyTrust.NotPinned, trust);
    }

    /// <summary>
    /// The attack the roster-deriving build was vulnerable to: a malicious server nominates its OWN
    /// key as primary and self-signs the joined node's enrollment certificate under it, so the cert
    /// and the claimed primary agree with each other. Only an out-of-band pin catches this.
    /// </summary>
    [Fact]
    public void JoinedNode_RejectsServerSuppliedPrimary_WithSelfConsistentForgedCert()
    {
        var (soulPub, _) = NewKey();
        var (nodePub, _) = NewKey();
        var (roguePub, rogueKey) = NewKey();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        // Server knows nodePub (the node registered it at /soul/join) and signs a cert over it with R.
        var forgedCert = SignEnrollment(rogueKey, "soul-1", nodePub, "windows", expiry);

        var roster = new List<SoulNodeRosterEntry>
        {
            new(roguePub, null, null, "primary", 0, IsPrimary: true),
            new(nodePub, forgedCert, roguePub, "windows", expiry, IsPrimary: false),
        };
        var pinned = new BridgeSoul
        {
            ServerSoulId        = "soul-1",
            NodePublicKeyBase64 = nodePub,
            PublicKeyBase64     = soulPub,
            SoulKeyPinnedAt     = DateTime.UtcNow,
        };

        Assert.Null(SiblingRoster.ResolveSoulMasterPublicKey(pinned, roster, out var trust));
        Assert.Equal(SiblingRoster.SoulKeyTrust.PinMismatch, trust);
    }

    [Fact]
    public void PrimaryNode_UsesItsOwnSoulKey_WithoutAPin()
    {
        var (soulPub, _) = NewKey();
        var primary = new BridgeSoul { ServerSoulId = "soul-1", PublicKeyBase64 = soulPub };

        var resolved = SiblingRoster.ResolveSoulMasterPublicKey(primary, [], out var trust);

        Assert.Equal(soulPub, resolved);
        Assert.Equal(SiblingRoster.SoulKeyTrust.Trusted, trust);
    }
}
