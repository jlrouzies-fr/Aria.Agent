using Aria.Shared;

namespace Aria.Web.Services.Node;

/// <summary>
/// A node-signed authorisation grant (defense-in-depth plan, §5). The signature is produced by a
/// node's soul key over <see cref="NodeCrypto.GrantPayload"/> and can only be created at a node —
/// the hosted server can relay a grant but cannot forge one. Both layers reduce to this:
/// <list type="bullet">
///   <item><c>GrantType = "trust-device"</c> — Layer A, verified server-side against the soul key.</item>
///   <item><c>GrantType = "context"</c> — Layer B, verified bridge-side before a sensitive relayed op.</item>
/// </list>
/// The grant is self-describing: a verifier recomputes the signed bytes from the fields alone (no
/// external nonce store), so grants survive persistence and mesh replication.
/// </summary>
public sealed record SignedGrant(
    string GrantType,
    string SubjectId,
    string ContextId,
    long   ExpiryUnix,
    string SignatureBase64)
{
    public bool IsExpired(DateTimeOffset? now = null) =>
        (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() > ExpiryUnix;
}

public static class GrantVerifier
{
    /// <summary>
    /// True iff the grant's signature verifies against <paramref name="soulPublicKeyBase64"/> AND the
    /// grant has not expired. Any missing field, malformed key/signature, or a '|' in a field (which
    /// would make the canonical layout ambiguous) fails closed.
    /// </summary>
    public static bool Verify(string? soulPublicKeyBase64, SignedGrant? grant, DateTimeOffset? now = null)
    {
        if (grant is null || string.IsNullOrEmpty(soulPublicKeyBase64)) return false;
        if (string.IsNullOrEmpty(grant.GrantType) || string.IsNullOrEmpty(grant.SubjectId) ||
            string.IsNullOrEmpty(grant.ContextId) || string.IsNullOrEmpty(grant.SignatureBase64))
            return false;
        // A '|' in a field would let two different grants share canonical bytes — reject outright.
        if (grant.GrantType.Contains('|') || grant.SubjectId.Contains('|') || grant.ContextId.Contains('|'))
            return false;
        if (grant.IsExpired(now)) return false;

        var payload = NodeCrypto.GrantPayload(grant.GrantType, grant.SubjectId, grant.ContextId, grant.ExpiryUnix);
        return NodeCrypto.Verify(soulPublicKeyBase64, payload, grant.SignatureBase64);
    }

    /// <summary>
    /// Verifies against ANY of the supplied public keys (co-equal model: the soul master key OR any
    /// non-revoked node key of the soul). Because only *current, non-revoked* keys are passed, revoking
    /// the node that approved a grant automatically invalidates that grant — its signature no longer
    /// matches any accepted key.
    /// </summary>
    public static bool VerifyAny(IEnumerable<string> acceptablePublicKeys, SignedGrant? grant, DateTimeOffset? now = null)
    {
        foreach (var key in acceptablePublicKeys)
            if (Verify(key, grant, now)) return true;
        return false;
    }
}
