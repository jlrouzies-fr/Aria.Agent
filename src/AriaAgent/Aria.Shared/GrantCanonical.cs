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

    /// <summary>
    /// The bytes a node signs to REVOKE a grant (a tombstone replicated alongside grants). A distinct
    /// <c>revoke|</c> prefix guarantees a tombstone can never verify as a grant, nor a grant as a
    /// tombstone. Carries the expiry of the revoked grant instance so a later re-approval (longer
    /// expiry) is not blocked by a stale tombstone.
    /// </summary>
    public static byte[] RevocationPayload(string contextId, long grantExpiryUnix) =>
        Encoding.UTF8.GetBytes($"revoke|{contextId}|{grantExpiryUnix}");
}
