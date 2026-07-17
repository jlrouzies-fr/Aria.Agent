using System.Security.Cryptography;

namespace Aria.Bridge.Services.Vault;

/// <summary>
/// Windows DPAPI protector. Uses <see cref="ProtectedData"/> with <see cref="DataProtectionScope.CurrentUser"/>,
/// so the protected blob can only be unprotected by the same Windows user that created it.
/// </summary>
public sealed class WindowsDpapiProtector : IDataProtector
{
    public string Name => "windows-dpapi";
    public bool IsHardwareBacked => false;

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
}
