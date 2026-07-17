using System.Security.Cryptography;
using Aria.Bridge.Data;
using Aria.Shared;

namespace Aria.Tests.Bridge;

/// <summary>
/// Test helpers for generating bridge-compatible P-256 keypairs and souls.
/// </summary>
public static class TestCrypto
{
    public static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static string PubB64(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    public static string PrivB64(ECDsa key) => Convert.ToBase64String(key.ExportPkcs8PrivateKey());

    public static BridgeSoul GenerateSoul(out ECDsa soulKey)
    {
        soulKey = NewKey();
        return new BridgeSoul
        {
            ServerSoulId = $"soul-{Guid.NewGuid():N}",
            PublicKeyBase64 = PubB64(soulKey),
            PrivateKeyBase64 = PrivB64(soulKey),
        };
    }

    public static BridgeSoul GenerateNode(out ECDsa nodeKey)
    {
        nodeKey = NewKey();
        return new BridgeSoul
        {
            NodePublicKeyBase64 = PubB64(nodeKey),
            NodePrivateKeyBase64 = PrivB64(nodeKey),
        };
    }

    public static string SignEnrollment(ECDsa signer, string soulId, string nodePub, string label, long expiry)
    {
        var payload = NodeCrypto.EnrollPayload(soulId, nodePub, label, expiry);
        return Convert.ToBase64String(signer.SignData(payload, HashAlgorithmName.SHA256));
    }
}
