using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aria.Shared;

/// <summary>
/// End-to-end crypto for the bridge remote-nodes data sync (feature plan §11).
///
/// Two layers:
/// 1. <b>DEK</b> — a per-soul AES-256-GCM Data Encryption Key. Every synced record is encrypted with
///    it (<see cref="Encrypt"/>/<see cref="Decrypt"/>), so the server only ever stores ciphertext.
/// 2. <b>DEK wrapping</b> — the DEK is minted by the primary bridge and delivered to each new node at
///    enrollment, ECDH-wrapped to that node's P-256 public key (<see cref="WrapDek"/>/<see cref="UnwrapDek"/>).
///    The node keypairs are the same P-256 keys used for ECDSA signing (SubjectPublicKeyInfo public /
///    PKCS#8 private); .NET imports an id-ecPublicKey SPKI into <see cref="ECDiffieHellman"/> equally,
///    so the signing keys double as key-agreement keys — no second keypair per node.
///
/// Blob layout (record &amp; wrapped DEK alike): base64(nonce(12) || tag(16) || ciphertext).
/// </summary>
public static class SyncCrypto
{
    private const int NonceLen = 12;
    private const int TagLen   = 16;

    // ── DEK lifecycle ──────────────────────────────────────────────────────────────────────────

    /// <summary>Mints a fresh 256-bit DEK (base64). Called once by the primary bridge.</summary>
    public static string GenerateDek() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    // ── Record encryption (AES-256-GCM with the DEK) ─────────────────────────────────────────────

    public static string Encrypt(string dekBase64, string plaintext)
    {
        var key = Convert.FromBase64String(dekBase64);
        var pt  = Encoding.UTF8.GetBytes(plaintext);
        return Convert.ToBase64String(GcmSeal(key, pt));
    }

    public static string Decrypt(string dekBase64, string blobBase64)
    {
        var key = Convert.FromBase64String(dekBase64);
        return Encoding.UTF8.GetString(GcmOpen(key, Convert.FromBase64String(blobBase64)));
    }

    // ── DEK wrapping (ECDH-ES to a node's P-256 public key) ──────────────────────────────────────

    /// <summary>
    /// Wraps the DEK for delivery to a node, given that node's public key. An ephemeral keypair does
    /// ECDH against the recipient key; the derived secret AES-GCM-encrypts the DEK bytes. Returns JSON
    /// { epk, blob } — epk is the ephemeral public key the recipient needs to re-derive the secret.
    /// </summary>
    public static string WrapDek(string dekBase64, string recipientPublicKeyB64)
    {
        var dek = Convert.FromBase64String(dekBase64);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var recipient = ECDiffieHellman.Create();
        recipient.ImportSubjectPublicKeyInfo(Convert.FromBase64String(recipientPublicKeyB64), out _);

        var shared = ephemeral.DeriveKeyFromHash(recipient.PublicKey, HashAlgorithmName.SHA256);
        var blob   = GcmSeal(shared, dek);
        var epk    = Convert.ToBase64String(ephemeral.PublicKey.ExportSubjectPublicKeyInfo());
        return JsonSerializer.Serialize(new WrappedDek(epk, Convert.ToBase64String(blob)));
    }

    /// <summary>Reverses <see cref="WrapDek"/> using the recipient node's private key. Returns the DEK (base64).</summary>
    public static string UnwrapDek(string wrappedJson, string recipientPrivateKeyB64)
    {
        var w = JsonSerializer.Deserialize<WrappedDek>(wrappedJson)
                ?? throw new CryptographicException("Malformed wrapped DEK");
        using var recipient = ECDiffieHellman.Create();
        recipient.ImportPkcs8PrivateKey(Convert.FromBase64String(recipientPrivateKeyB64), out _);
        using var ephemeral = ECDiffieHellman.Create();
        ephemeral.ImportSubjectPublicKeyInfo(Convert.FromBase64String(w.Epk), out _);

        var shared = recipient.DeriveKeyFromHash(ephemeral.PublicKey, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(GcmOpen(shared, Convert.FromBase64String(w.Blob)));
    }

    // ── AES-GCM primitives (shared blob format) ──────────────────────────────────────────────────

    private static byte[] GcmSeal(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var ct    = new byte[plaintext.Length];
        var tag   = new byte[TagLen];
        using var aes = new AesGcm(key, TagLen);
        aes.Encrypt(nonce, plaintext, ct, tag);

        var blob = new byte[NonceLen + TagLen + ct.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceLen);
        Buffer.BlockCopy(tag,   0, blob, NonceLen, TagLen);
        Buffer.BlockCopy(ct,    0, blob, NonceLen + TagLen, ct.Length);
        return blob;
    }

    private static byte[] GcmOpen(byte[] key, byte[] blob)
    {
        if (blob.Length < NonceLen + TagLen) throw new CryptographicException("Blob too short");
        var nonce = blob.AsSpan(0, NonceLen);
        var tag   = blob.AsSpan(NonceLen, TagLen);
        var ct    = blob.AsSpan(NonceLen + TagLen);
        var pt    = new byte[ct.Length];
        using var aes = new AesGcm(key, TagLen);
        aes.Decrypt(nonce, ct, tag, pt);
        return pt;
    }

    private sealed record WrappedDek(string Epk, string Blob);
}
