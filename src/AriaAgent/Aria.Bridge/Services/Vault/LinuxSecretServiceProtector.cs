using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Aria.Bridge.Services.Vault;

/// <summary>
/// Linux protector. Prefers the freedesktop Secret Service via <c>secret-tool</c>; if unavailable,
/// falls back to a key file in the bridge app-data directory encrypted with a machine-specific key
/// derived from /etc/machine-id. Key-directory isolation prevents tests or alternate instances from
/// clobbering the production keyring item or fallback key file.
/// </summary>
public sealed class LinuxSecretServiceProtector : IDataProtector
{
    private readonly string _attribute;
    private readonly string _label;
    private readonly string _fallbackKeyPath;

    public LinuxSecretServiceProtector(string? keyDirectory = null)
    {
        var defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "aria-bridge");
        if (string.IsNullOrEmpty(keyDirectory)
            || string.Equals(Path.GetFullPath(keyDirectory), Path.GetFullPath(defaultDir), StringComparison.OrdinalIgnoreCase))
        {
            _attribute = "aria-bridge";
            _label = "Aria Bridge vault DEK";
            _fallbackKeyPath = Path.Combine(defaultDir, "vault.key");
        }
        else
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyDirectory)))[..16];
            _attribute = $"aria-bridge-{hash}";
            _label = $"Aria Bridge vault DEK ({hash})";
            _fallbackKeyPath = Path.Combine(keyDirectory, "vault.key");
        }
    }

    public string Name => HasSecretTool() ? "linux-secret-service" : "linux-file-fallback";
    public bool IsHardwareBacked => HasSecretTool();

    public byte[] Protect(byte[] plaintext)
    {
        if (HasSecretTool())
        {
            var b64 = Convert.ToBase64String(plaintext);
            RunSecretTool($"store --label='{_label}' {_attribute} vault-dek", input: b64 + "\n");
            return [];
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_fallbackKeyPath)!);
        var key = GetFallbackKey();
        var encrypted = AesGcmHelper.Encrypt(plaintext, key);
        File.WriteAllBytes(_fallbackKeyPath, encrypted);
        return [];
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (HasSecretTool())
        {
            var b64 = RunSecretTool($"lookup {_attribute} vault-dek").Trim();
            return Convert.FromBase64String(b64);
        }

        if (!File.Exists(_fallbackKeyPath))
            throw new InvalidOperationException("Linux fallback key file not found");

        var key = GetFallbackKey();
        return AesGcmHelper.Decrypt(File.ReadAllBytes(_fallbackKeyPath), key);
    }

    private static bool HasSecretTool()
    {
        try
        {
            var psi = new ProcessStartInfo("secret-tool", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(1000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string RunSecretTool(string args, string? input = null)
    {
        var psi = new ProcessStartInfo("secret-tool", args)
        {
            RedirectStandardInput = input != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        if (input != null)
        {
            proc.StandardInput.Write(input);
            proc.StandardInput.Dispose();
        }
        proc.WaitForExit();
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"secret-tool failed ({proc.ExitCode}): {stderr}");
        return stdout;
    }

    private static byte[] GetFallbackKey()
    {
        // Derive a stable machine+user key from /etc/machine-id and the current user SID.
        var machineId = File.Exists("/etc/machine-id") ? File.ReadAllText("/etc/machine-id").Trim() : Environment.MachineName;
        var user = Environment.UserName;
        return System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"aria-bridge:{machineId}:{user}"));
    }
}
