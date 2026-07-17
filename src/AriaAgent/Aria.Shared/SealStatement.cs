using System.Text;

namespace Aria.Shared;

/// <summary>
/// Canonical human-readable statement for the Inquisitorial Seal (F-6). The node signs this exact
/// text; the server reconstructs it to verify that the signature covers what the human saw.
/// </summary>
public static class SealStatement
{
    /// <summary>
    /// Builds the canonical, human-readable statement that the node signs on approval. The server
    /// reconstructs this exact text to verify the signature, and the approval page renders it verbatim
    /// so the human sees exactly what they are authorising.
    /// </summary>
    public static string Build(string toolName, string reason, string argsPreview, DateTime expiresAt, byte[] nonce)
    {
        var sb = new StringBuilder();
        sb.AppendLine("INQUISITORIAL SEAL");
        sb.AppendLine($"Capability: {toolName}");
        sb.AppendLine($"Scope: {reason}");
        sb.AppendLine($"Details: {argsPreview}");
        sb.AppendLine($"Expires (UTC): {expiresAt:O}");
        sb.AppendLine($"Nonce (base64): {Convert.ToBase64String(nonce)}");
        return sb.ToString().TrimEnd();
    }
}
