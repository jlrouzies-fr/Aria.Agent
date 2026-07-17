using System.Security.Cryptography;
using System.Text;
using Aria.Bridge.Data;
using Aria.Bridge.Services.Logging;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Services.Llm;

// Shared local key-vault lookup — used by the LLM proxy endpoints and, in-process, by Noosphere
// (extraction/embeddings calls never need to leave the bridge to resolve a stored provider key).
public static class LlmKeyStore
{
    public static async Task<string?> GetPlaintextKeyAsync(BridgeDbContext db, string provider)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT KeyB64 FROM LlmKeys WHERE Provider = @p LIMIT 1;";
            var p = cmd.CreateParameter();
            p.ParameterName = "@p";
            p.Value = provider;
            cmd.Parameters.Add(p);
            var keyB64 = await cmd.ExecuteScalarAsync() as string;
            if (keyB64 == null) return null;

            // Decrypt at-rest ciphertext; legacy plaintext values pass through unchanged.
            if (db.Vault != null)
            {
                try
                {
                    keyB64 = db.Vault.Decrypt(keyB64);
                }
                catch (CryptographicException ex)
                {
                    // The stored key was encrypted under a previous vault DEK that is no longer
                    // available (e.g. keychain/DPAPI secret regenerated). Treat it as missing so the
                    // caller can prompt for re-entry instead of crashing the request.
                    BridgeLogger.Log("WARN", $"Stored key for '{provider}' is unreadable (vault DEK mismatch): {ex.Message}. Re-save the key in Channels.");
                    return null;
                }
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(keyB64));
            }
            catch (FormatException ex)
            {
                BridgeLogger.Log("WARN", $"Stored key for '{provider}' is not valid base64: {ex.Message}. Re-save the key in Channels.");
                return null;
            }
        }
        finally { await conn.CloseAsync(); }
    }
}
