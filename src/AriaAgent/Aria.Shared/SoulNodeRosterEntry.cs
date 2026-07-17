namespace Aria.Shared;

/// <summary>
/// One entry in the soul's verifiable node roster, relayed by the server to each bridge. The bridge
/// re-verifies <see cref="EnrollmentCertB64"/> locally against the soul public key or an already-trusted
/// sibling key before accepting <see cref="NodePublicKeyBase64"/> as a grant signer. The server cannot
/// inject a trusted key: it can only relay these signed entries; verification happens on the bridge.
/// </summary>
public sealed record SoulNodeRosterEntry(
    string NodePublicKeyBase64,
    string? EnrollmentCertB64,
    string? ApproverPublicKeyBase64,
    string Label,
    long EnrollmentExpiryUnix,
    bool IsPrimary);
