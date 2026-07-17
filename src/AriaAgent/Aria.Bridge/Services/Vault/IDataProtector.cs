namespace Aria.Bridge.Services.Vault;

/// <summary>
/// Platform-specific protector for the vault encryption key (F-7). On Windows this is DPAPI;
/// on macOS the Keychain; on Linux Secret Service or a fallback. The protected blob may be stored
/// next to the vault; it is useless without the OS credential store.
/// </summary>
public interface IDataProtector
{
    string Name { get; }

    /// <summary>Protect a small secret (the vault DEK) with the OS-backed store.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>Unprotect a secret previously returned by <see cref="Protect"/>.</summary>
    byte[] Unprotect(byte[] ciphertext);

    /// <summary>True if this protector is backed by an OS keychain/DPAPI/Secret Service.</summary>
    bool IsHardwareBacked { get; }
}
