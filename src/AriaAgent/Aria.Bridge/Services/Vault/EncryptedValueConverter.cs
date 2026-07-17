using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Aria.Bridge.Services.Vault;

/// <summary>
/// EF Core value converter that encrypts/decrypts string properties at rest (F-7). The conversion is
/// injected into every property marked with <see cref="EncryptedAttribute"/> during model creation.
/// </summary>
public sealed class EncryptedValueConverter : ValueConverter<string, string>
{
    public EncryptedValueConverter(VaultEncryption vault)
        : base(
            plain => vault.Encrypt(plain) ?? string.Empty,
            cipher => vault.Decrypt(cipher))
    {
    }
}
