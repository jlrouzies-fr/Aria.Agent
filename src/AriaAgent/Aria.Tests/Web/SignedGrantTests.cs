using System.Security.Cryptography;
using Aria.Shared;
using Aria.Web.Services.ModelBridge;
using Aria.Web.Services.Node;
using Xunit;

namespace Aria.Tests.Web;

/// <summary>
/// Proves the node-signed grant primitive (defense-in-depth plan §5) round-trips with EXACTLY what
/// the bridge produces: a P-256 keypair in the bridge's format (SubjectPublicKeyInfo public / PKCS#8
/// private), signing the canonical grant bytes with SHA-256 — mirroring
/// <c>Aria.Bridge SealEndpoints /seal/{id}/approve</c>. If the canonical layout or verification drift
/// apart, these fail.
/// </summary>
public class SignedGrantTests
{
    private static (string PubB64, ECDsa Key) NewSoulKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        return (pub, key);
    }

    // Signs canonical grant bytes exactly as the bridge's approve endpoint does.
    private static SignedGrant Mint(ECDsa key, string type, string subject, string context, long expiryUnix)
    {
        var payload = NodeCrypto.GrantPayload(type, subject, context, expiryUnix);
        var sig     = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256));
        return new SignedGrant(type, subject, context, expiryUnix, sig);
    }

    private static long InHours(int h) => DateTimeOffset.UtcNow.AddHours(h).ToUnixTimeSeconds();

    [Fact]
    public void ValidGrant_Verifies()
    {
        var (pub, key) = NewSoulKey();
        var grant = Mint(key, GrantService.DeviceGrant, "device-123", "soul-abc", InHours(24));
        Assert.True(GrantVerifier.Verify(pub, grant));
    }

    [Fact]
    public void TamperedField_FailsClosed()
    {
        var (pub, key) = NewSoulKey();
        var grant = Mint(key, GrantService.ContextGrant, "session-1", "soul-abc", InHours(1));

        Assert.False(GrantVerifier.Verify(pub, grant with { SubjectId = "session-2" }));
        Assert.False(GrantVerifier.Verify(pub, grant with { ContextId = "soul-xyz" }));
        Assert.False(GrantVerifier.Verify(pub, grant with { GrantType = "trust-device" }));
        Assert.False(GrantVerifier.Verify(pub, grant with { ExpiryUnix = grant.ExpiryUnix + 1 }));
    }

    [Fact]
    public void ExpiredGrant_FailsClosed()
    {
        var (pub, key) = NewSoulKey();
        var grant = Mint(key, GrantService.ContextGrant, "session-1", "soul-abc", InHours(-1));
        Assert.False(GrantVerifier.Verify(pub, grant));
    }

    [Fact]
    public void WrongSoulKey_FailsClosed()
    {
        var (_, signer)     = NewSoulKey();
        var (otherPub, _)   = NewSoulKey();
        var grant = Mint(signer, GrantService.DeviceGrant, "device-123", "soul-abc", InHours(24));
        Assert.False(GrantVerifier.Verify(otherPub, grant));
    }

    [Fact]
    public void PipeInField_FailsClosed()
    {
        // A '|' would make two different grants share canonical bytes — reject before verifying.
        var (pub, key) = NewSoulKey();
        var grant = Mint(key, GrantService.ContextGrant, "a|b", "soul-abc", InHours(1));
        Assert.False(GrantVerifier.Verify(pub, grant));
    }

    [Fact]
    public void MissingInputs_FailClosed()
    {
        var (pub, key) = NewSoulKey();
        var grant = Mint(key, GrantService.DeviceGrant, "device-123", "soul-abc", InHours(24));
        Assert.False(GrantVerifier.Verify(null, grant));
        Assert.False(GrantVerifier.Verify(pub, null));
        Assert.False(GrantVerifier.Verify("", grant));
    }

    // Cross-boundary guarantee: the bridge and the server build the signed bytes from the SAME shared
    // source (Aria.Shared.GrantCanonical). NodeCrypto must delegate to it, and a signature over those
    // bytes must verify with the server's GrantVerifier — otherwise a bridge-signed grant wouldn't
    // verify server-side (Layer A) or on a sibling node (Layer B replication).
    [Fact]
    public void NodeCrypto_DelegatesTo_SharedCanonical()
    {
        var a = NodeCrypto.GrantPayload("context", "s", "c", 123);
        var b = Aria.Shared.GrantCanonical.Payload("context", "s", "c", 123);
        Assert.Equal(b, a);
    }

    [Fact]
    public void SignatureOverSharedCanonical_VerifiesServerSide()
    {
        var (pub, key) = NewSoulKey();
        long expiry = InHours(6);
        var payload = Aria.Shared.GrantCanonical.Payload("context", "soul-x", "soul-x", expiry);
        var sig     = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256));
        var grant   = new SignedGrant("context", "soul-x", "soul-x", expiry, sig);
        Assert.True(GrantVerifier.Verify(pub, grant));
    }
}
