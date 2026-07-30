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
    public void JoinedNode_ResolvesSoulMasterKey_WhenPrimaryEnrolledIt()
    {
        var (soulPub, soulKey) = NewKey();
        var (nodePub, _) = NewKey();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var cert = SignEnrollment(soulKey, "soul-1", nodePub, "windows", expiry);

        var roster = new List<SoulNodeRosterEntry>
        {
            new(soulPub, null, null, "primary", 0, IsPrimary: true),
            new(nodePub, cert, soulPub, "windows", expiry, IsPrimary: false),
        };
        var joined = new BridgeSoul
        {
            ServerSoulId = "soul-1",
            NodePublicKeyBase64 = nodePub,
        };

        Assert.Equal(soulPub, SiblingRoster.TryResolveSoulMasterPublicKey(joined, roster));
    }

    [Fact]
    public void JoinedNode_DoesNotTrustUnverifiedPrimaryKey()
    {
        var (soulPub, soulKey) = NewKey();
        var (nodePub, _) = NewKey();
        var (roguePub, rogueKey) = NewKey();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        // Cert signed by rogue key, not the soul key — server cannot swap primary and fool us.
        var badCert = SignEnrollment(rogueKey, "soul-1", nodePub, "windows", expiry);

        var roster = new List<SoulNodeRosterEntry>
        {
            new(soulPub, null, null, "primary", 0, IsPrimary: true),
            new(nodePub, badCert, roguePub, "windows", expiry, IsPrimary: false),
        };
        var joined = new BridgeSoul
        {
            ServerSoulId = "soul-1",
            NodePublicKeyBase64 = nodePub,
        };

        Assert.Null(SiblingRoster.TryResolveSoulMasterPublicKey(joined, roster));
    }
}
