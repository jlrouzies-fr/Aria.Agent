using System.Security.Cryptography;

namespace Aria.Bridge.Services.Vault;

/// <summary>
/// AES-256-GCM helpers used by the vault encryption layer (F-7). Nonce is prepended to the ciphertext
/// so each value is self-contained. The key is the OS-protected data encryption key (DEK).
/// </summary>
public static class AesGcmHelper
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        using var aes = new AesGcm(key, TagSize);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSize + plaintext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, NonceSize + ciphertext.Length, TagSize);
        return result;
    }

    public static byte[] Decrypt(byte[] encrypted, byte[] key)
    {
        if (encrypted.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted value too short");

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertextLength = encrypted.Length - NonceSize - TagSize;
        var ciphertext = new byte[ciphertextLength];
        Buffer.BlockCopy(encrypted, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(encrypted, NonceSize, ciphertext, 0, ciphertextLength);
        Buffer.BlockCopy(encrypted, NonceSize + ciphertextLength, tag, 0, TagSize);

        using var aes = new AesGcm(key, TagSize);
        var plaintext = new byte[ciphertextLength];
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
