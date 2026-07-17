namespace Aria.Web.Data;

/// <summary>
/// A browser device that a node has approved (Layer A, defense-in-depth plan §3). The row is only
/// created after a node signs a <c>trust-device</c> grant — the server cannot mint one itself. A
/// device-id cookie whose row here still verifies (unexpired, unrevoked, signature valid against the
/// approving soul's public key) passes the access gate regardless of IP. The stored signature is the
/// node's proof; <see cref="LastIp"/> is a human-readable hint only, never a gate.
/// </summary>
public class TrustedDevice
{
    public int Id { get; set; }

    /// <summary>The soul that approved this device (grant ContextId; verify against its public key).</summary>
    public string UserId { get; set; } = "";

    /// <summary>Random, opaque; matches the browser's device-id cookie (grant SubjectId).</summary>
    public string DeviceId { get; set; } = "";

    public string? Label { get; set; }

    /// <summary>Display/anomaly hint only — the Fly-Client-IP resolved at approval time.</summary>
    public string? LastIp { get; set; }

    /// <summary>Node signature over the canonical grant payload (see <c>NodeCrypto.GrantPayload</c>).</summary>
    public string SignatureBase64 { get; set; } = "";

    /// <summary>Grant expiry as a unix timestamp — must match what was signed.</summary>
    public long ExpiryUnix { get; set; }

    /// <summary>Thumbprint of the node that approved (for the device list / audit).</summary>
    public string? ApprovedByNodeId { get; set; }

    public bool Revoked { get; set; }

    public DateTime? RevokedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
