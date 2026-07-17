namespace Aria.Web.Data.Bridge;

/// <summary>
/// The per-soul node allow-list (§9 of the bridge-remote-nodes plan). Each row is a bridge (node)
/// authorized to connect as this user. Co-equal owners: an enrollment/revocation is valid if signed
/// by the soul key OR any non-revoked node already in this list.
/// </summary>
public class SoulNodeKey
{
    public int      Id                   { get; set; }
    public string   UserId               { get; set; } = "";
    public string   NodeId               { get; set; } = "";   // thumbprint of NodePublicKeyBase64
    public string   NodePublicKeyBase64  { get; set; } = "";
    public string?  Label                { get; set; }
    public string?  Platform             { get; set; }
    public string?  EnrolledByNodeId     { get; set; }         // thumbprint of the approver (null = primary/soul)
    public bool     IsPrimary            { get; set; }         // the soul-key node; cannot be revoked
    public bool     Revoked              { get; set; }
    // The soul's Data Encryption Key (§11), ECDH-wrapped to this node's public key by the approving
    // bridge at enrollment. Opaque ciphertext: the server relays it but never reads it. The node
    // fetches and unwraps it on first connect (GetWrappedDek), then never needs it again.
    public string?  WrappedDek           { get; set; }

    // Layer B Phase 2: verifiable enrollment certificate. The bridge re-verifies this cert against
    // the soul public key or an already-trusted sibling key before accepting the node as a grant signer.
    // EnrollmentCertB64 is the base64 ECDSA signature over NodeCrypto.EnrollPayload(userId, nodePub, label, expiryUnix).
    public string?  EnrollmentCertB64    { get; set; }
    public string?  ApproverPublicKeyBase64 { get; set; }
    public long?    EnrollmentExpiryUnix { get; set; }

    public DateTime EnrolledAt           { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt           { get; set; }
    public DateTime LastSeenAt           { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
