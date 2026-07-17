using Aria.Harness.Governance;
using Aria.Shared;
using Aria.Web.Services.Node;

namespace Aria.Web.Services.ModelBridge;

/// <summary>
/// Mints node-signed <see cref="SignedGrant"/>s (defense-in-depth plan §5) by driving the Seal
/// ceremony over canonical grant bytes. The node signs the grant with the soul key — the server can
/// relay the resulting grant but cannot forge one. Both Layer A (device trust, server-verified) and
/// Layer B (context grants, bridge-verified) obtain grants through this one service.
/// </summary>
public sealed class GrantService(SealService seal, ILogger<GrantService> logger)
{
    public const string DeviceGrant  = "trust-device";
    public const string ContextGrant = "context";

    /// <summary>
    /// Asks the user's node (via the local Seal approval page) to sign a grant for
    /// <paramref name="subjectId"/> in <paramref name="contextId"/>, valid for <paramref name="ttl"/>.
    /// Returns the signed grant on approval, or null if the human refused, it timed out, or no node was
    /// reachable — fail-closed at every branch. The returned grant is self-verifying via
    /// <see cref="GrantVerifier"/> and safe to persist or mesh-replicate.
    /// </summary>
    public async Task<SignedGrant?> RequestGrantAsync(
        string userId, string grantType, string subjectId, string contextId,
        TimeSpan ttl, ActionDescriptor desc, CancellationToken ct)
    {
        var expiryUnix = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();
        var payload    = NodeCrypto.GrantPayload(grantType, subjectId, contextId, expiryUnix);

        var sig = await seal.RequestSignatureAsync(userId, desc, payload, ct);
        if (sig == null)
        {
            logger.LogInformation("Grant '{Type}' for subject {Subject} not signed (refused/expired/unreachable) for user {User}",
                grantType, subjectId, userId);
            return null;
        }

        return new SignedGrant(grantType, subjectId, contextId, expiryUnix, sig);
    }
}
