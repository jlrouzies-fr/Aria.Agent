using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Aria.Bridge.Services.Vault;

/// <summary>
/// Encrypts sensitive values in the bridge SQLite vault at rest (F-7). A random data encryption key
/// (DEK) is protected by the OS keychain/DPAPI/Secret Service; the protected DEK is stored in a
/// sidecar file. Individual properties marked with <see cref="EncryptedAttribute"/> are encrypted
/// with AES-256-GCM under the DEK.
/// </summary>
public sealed class VaultEncryption
{
    private const string KeyFileName = "vault-dek.bin";

    private readonly IDataProtector _protector;
    private readonly ILogger<VaultEncryption> _logger;
    private readonly string _keyFilePath;
    private readonly Lazy<byte[]> _dek;

    public VaultEncryption(ILogger<VaultEncryption> logger, VaultEncryptionOptions? options = null)
    {
        var keyDir = options?.KeyDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "aria-bridge");
        _protector = CreateProtector(keyDir);
        _logger = logger;
        _keyFilePath = Path.Combine(keyDir, KeyFileName);
        _dek = new Lazy<byte[]>(LoadOrCreateDek);
    }

    public string ProtectorName => _protector.Name;
    public bool IsHardwareBacked => _protector.IsHardwareBacked;

    /// <summary>Encrypt a plaintext string; returns base64 ciphertext with a version prefix.</summary>
    public string? Encrypt(string? plaintext)
    {
        if (plaintext == null) return null;
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = AesGcmHelper.Encrypt(bytes, _dek.Value);
        return $"enc:1:{Convert.ToBase64String(encrypted)}";
    }

    /// <summary>Decrypt a value produced by <see cref="Encrypt"/>.</summary>
    public string? Decrypt(string? ciphertext)
    {
        if (ciphertext == null) return null;
        if (!ciphertext.StartsWith("enc:1:", StringComparison.Ordinal))
            // Plaintext legacy value — returned as-is. Migration will re-encrypt on save.
            return ciphertext;

        var b64 = ciphertext["enc:1:".Length..];
        var encrypted = Convert.FromBase64String(b64);
        var bytes = AesGcmHelper.Decrypt(encrypted, _dek.Value);
        return Encoding.UTF8.GetString(bytes);
    }

    private byte[] LoadOrCreateDek()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_keyFilePath)!);

        if (File.Exists(_keyFilePath))
        {
            try
            {
                var protectedDek = File.ReadAllBytes(_keyFilePath);
                var dek = _protector.Unprotect(protectedDek);
                _logger.LogInformation("Vault DEK loaded (protector: {Protector}, hardware-backed: {Backed})",
                    _protector.Name, _protector.IsHardwareBacked);
                return dek;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to unprotect vault DEK with {Protector}. If the OS keychain/DPAPI secret was lost, the vault cannot be recovered.",
                    _protector.Name);
                throw;
            }
        }

        var newDek = RandomNumberGenerator.GetBytes(32);
        var protectedNew = _protector.Protect(newDek);
        File.WriteAllBytes(_keyFilePath, protectedNew);
        _logger.LogInformation("Generated new vault DEK (protector: {Protector}, hardware-backed: {Backed})",
            _protector.Name, _protector.IsHardwareBacked);
        return newDek;
    }

    private static IDataProtector CreateProtector(string keyDirectory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsDpapiProtector();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new MacKeychainProtector(keyDirectory);
        return new LinuxSecretServiceProtector(keyDirectory);
    }
}
