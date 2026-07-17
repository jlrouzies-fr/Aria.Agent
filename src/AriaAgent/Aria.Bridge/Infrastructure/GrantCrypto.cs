using System.Security.Cryptography;

namespace Aria.Bridge.Infrastructure;

/// <summary>
/// ECDSA P-256 sign/verify for Layer B context grants, matching the rest of the soul/node key scheme
/// (SubjectPublicKeyInfo public keys, PKCS#8 private keys, SHA-256). Signing a grant lets it be safely
/// relayed between a soul's nodes through the untrusted server — a sibling verifies the signature and
/// the server can neither forge nor tamper with it.
/// </summary>
public static class GrantCrypto
{
    public static string Sign(string privateKeyBase64, byte[] payload)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        return Convert.ToBase64String(ecdsa.SignData(payload, HashAlgorithmName.SHA256));
    }

    public static bool Verify(string? publicKeyBase64, byte[] payload, string? signatureBase64)
    {
        if (string.IsNullOrEmpty(publicKeyBase64) || string.IsNullOrEmpty(signatureBase64)) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return ecdsa.VerifyData(payload, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }
}
