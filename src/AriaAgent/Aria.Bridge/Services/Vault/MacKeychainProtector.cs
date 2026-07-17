using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Aria.Bridge.Services.Vault;

/// <summary>
/// macOS Keychain protector using the <c>security</c> command-line tool. The vault DEK is stored as a
/// generic password in the user's default keychain, accessible only to this user's login session.
/// Key-directory isolation prevents tests or alternate bridge instances from clobbering the
/// production keychain item.
/// </summary>
public sealed class MacKeychainProtector : IDataProtector
{
    private readonly string _service;
    private readonly string _account;

    public MacKeychainProtector(string? keyDirectory = null)
    {
        var defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "aria-bridge");
        if (string.IsNullOrEmpty(keyDirectory)
            || string.Equals(Path.GetFullPath(keyDirectory), Path.GetFullPath(defaultDir), StringComparison.OrdinalIgnoreCase))
        {
            // Backward-compatible names for the production app-data directory.
            _service = "aria-bridge-vault";
            _account = "vault-dek";
        }
        else
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyDirectory)))[..16];
            _service = $"aria-bridge-vault-{hash}";
            _account = $"vault-dek-{hash}";
        }
    }

    public string Name => "macos-keychain";
    public bool IsHardwareBacked => true;

    public byte[] Protect(byte[] plaintext)
    {
        var b64 = Convert.ToBase64String(plaintext);
        // Add or update the generic password.
        DeleteInternal();
        Run("add-generic-password", $"-s {_service} -a {_account} -w {b64} -U");
        return [];
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        var b64 = Run("find-generic-password", $"-s {_service} -a {_account} -w").Trim();
        return Convert.FromBase64String(b64);
    }

    private void DeleteInternal()
    {
        try { Run("delete-generic-password", $"-s {_service} -a {_account}"); }
        catch { /* may not exist */ }
    }

    private static string Run(string verb, string args)
    {
        var psi = new ProcessStartInfo("security", $"{verb} {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"security {verb} failed ({proc.ExitCode}): {stderr}");
        return stdout;
    }
}
