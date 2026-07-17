using System.Text;

namespace Aria.Shared;

/// <summary>
/// The single source of truth for the bytes a node signs to issue an authorisation grant
/// (defense-in-depth plan §5). Server (Aria.Web) and node (Aria.Bridge) both build the signed bytes
/// here, so a signature made on one side verifies on the other — essential once grants are relayed
/// through the (untrusted) server between nodes. Fields are pipe-delimited and MUST NOT contain '|'.
/// </summary>
public static class GrantCanonical
{
    public static byte[] Payload(string grantType, string subjectId, string contextId, long expiryUnix) =>
        Encoding.UTF8.GetBytes($"grant|{grantType}|{subjectId}|{contextId}|{expiryUnix}");
}
