using System.Security.Cryptography;
using System.Text;

namespace Aria.Shared;

/// <summary>
/// ECDSA P-256 helpers shared between Aria.Web and Aria.Bridge. SubjectPublicKeyInfo public keys,
/// PKCS#8 private keys, SHA-256 signatures. Matches the bridge's keypair format and the canonical
/// payload layouts used for enrollment, revocation, and signed grants.
/// </summary>
public static class NodeCrypto
{
    /// <summary>Short stable handle for a node = Base64Url(SHA256(pubkey))[..16].</summary>
    public static string Thumbprint(string publicKeyBase64)
    {
        var hash = SHA256.HashData(Convert.FromBase64String(publicKeyBase64));
        return Convert.ToBase64String(hash)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=')[..16];
    }

    /// <summary>
    /// Groups a thumbprint for humans (<c>abcd-efgh-ijkl-mnop</c>). Comparison code must strip
    /// dashes/spaces first — see soul-key pinning <c>Normalize</c>.
    /// </summary>
    public static string GroupThumbprint(string thumbprint)
    {
        var raw = new string((thumbprint ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (raw.Length == 0) return "";
        var sb = new StringBuilder(raw.Length + raw.Length / 4);
        for (var i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(raw[i]);
        }
        return sb.ToString();
    }

    /// <summary>Human-readable fingerprint of a public key (grouped thumbprint).</summary>
    public static string FormatThumbprint(string publicKeyBase64) =>
        GroupThumbprint(Thumbprint(publicKeyBase64));

    /// <summary>Verify an ECDSA signature over <paramref name="data"/> against a SubjectPublicKeyInfo key.</summary>
    public static bool Verify(string publicKeyBase64, byte[] data, string signatureBase64)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return ecdsa.VerifyData(data, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    // ── Canonical signed-payload layouts (must byte-match the bridge & docs §9.3) ──

    public static byte[] EnrollPayload(string serverSoulId, string newNodePubB64, string label, long expiryUnix) =>
        Encoding.UTF8.GetBytes($"enroll|{serverSoulId}|{newNodePubB64}|{label}|{expiryUnix}");

    public static byte[] RevokePayload(string serverSoulId, string targetNodePubB64, long nowUnix) =>
        Encoding.UTF8.GetBytes($"revoke|{serverSoulId}|{targetNodePubB64}|{nowUnix}");

    /// <summary>
    /// Canonical bytes a node signs to issue a context/device grant (defense-in-depth plan, §5).
    /// Delegates to <see cref="GrantCanonical.Payload"/> — the single source of truth shared
    /// with the bridge, so a signature made on either side verifies on the other.
    /// </summary>
    public static byte[] GrantPayload(string grantType, string subjectId, string contextId, long expiryUnix) =>
        GrantCanonical.Payload(grantType, subjectId, contextId, expiryUnix);
}
