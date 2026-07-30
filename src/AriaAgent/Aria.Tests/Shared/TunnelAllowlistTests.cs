using Aria.Shared;
using Xunit;

namespace Aria.Tests.Shared;

/// <summary>
/// Locks the tunnel local-REST allowlist (hardening-plan.md F-2). Only enumerated paths may be
/// relayed from the hosted server to the bridge; everything else is refused at the proxy boundary.
/// </summary>
public class TunnelAllowlistTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/metrics")]
    [InlineData("/llm/proxy")]
    [InlineData("/llm/probe")]
    [InlineData("/llm/discover-models")]
    [InlineData("/sync/apply")]
    [InlineData("/keys")]
    [InlineData("/channels")]
    [InlineData("/context/grants/export")]
    [InlineData("/context/grants/import")]
    [InlineData("/context/status")]
    [InlineData("/seal/request")]
    [InlineData("/seal/poll")]
    [InlineData("/node/session-code")]
    [InlineData("/node/sign-enrollment")]
    [InlineData("/node/sign-revocation")]
    [InlineData("/contacts")]
    [InlineData("/cogitations/init")]
    [InlineData("/memory/inscribe")]
    [InlineData("/debug/llm-log")]
    public void ExactAllowedPaths_AreAllowed(string path)
        => Assert.True(TunnelAllowlist.IsAllowed(path));

    [Theory]
    [InlineData("/oauth/google/token")]
    [InlineData("/oauth/google/status")]
    [InlineData("/cogitations/abc-123")]
    [InlineData("/cogitations/abc-123/messages")]
    [InlineData("/contacts/abc-123")]
    [InlineData("/memory/search")]
    [InlineData("/hive/rooms")]
    [InlineData("/project-files/list")]
    [InlineData("/project-files/write")]
    [InlineData("/project-git/run")]
    [InlineData("/terminal/exec")]
    [InlineData("/terminal/complete")]
    [InlineData("/terminal/pty-enabled")]
    [InlineData("/terminal/pty-enable")]
    [InlineData("/terminal/pty")]
    [InlineData("/terminal/enabled")]
    public void PrefixAllowedPaths_AreAllowed(string path)
        => Assert.True(TunnelAllowlist.IsAllowed(path));

    [Theory]
    [InlineData("/SOUL/EXPORT")]
    [InlineData("/soul/export")]
    [InlineData("/soul/import")]
    [InlineData("/soul/rotate-key")]
    [InlineData("/soul/keypair")]
    [InlineData("/soul/unlink")]
    [InlineData("/soul/switch-server")]
    [InlineData("/soul/link-server")]
    [InlineData("/soul/sign")]
    [InlineData("/db/soul")]
    [InlineData("/db/cogitations")]
    [InlineData("/db/messages")]
    [InlineData("/db/noosphere")]
    [InlineData("/unknown")]
    [InlineData("/soul")]
    [InlineData("/debug/secrets")]
    // Keys and channels are authored ONLY on the bridge (local origin), never relayed from the server.
    [InlineData("/keys/openai")]
    [InlineData("/keys/sync-export")]
    [InlineData("/keys/sync-import")]
    [InlineData("/channels/openai")]
    [InlineData("/channels/my-local")]
    // The soul-key pinning ceremony is the joined node's trust anchor. If the server could drive it,
    // it could pin a key of its own choosing and forge context grants — the anchor must stay local.
    [InlineData("/soul/pin")]
    [InlineData("/soul/pin-key")]
    [InlineData("/soul/unpin-key")]
    [InlineData("/soul/pin-status")]
    public void LocalHumanOnlyPaths_AreBlocked(string path)
        => Assert.False(TunnelAllowlist.IsAllowed(path));

    [Theory]
    [InlineData("/LLM/Proxy")]
    [InlineData("/Llm/Probe")]
    [InlineData("/Terminal/Exec")]
    [InlineData("/KEYS")]
    [InlineData("/terminal/exec/")]
    [InlineData("/cogitations/ABC?q=1")]
    public void Normalisation_IsTolerant(string path)
        => Assert.True(TunnelAllowlist.IsAllowed(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrNullPaths_AreBlocked(string? path)
        => Assert.False(TunnelAllowlist.IsAllowed(path));

    [Fact]
    public void TerminalExecIsAllowed_ContextGrantStillGovernsIt()
    {
        // The allowlist permits the path through the tunnel; the separate Layer B gate still
        // classifies /terminal/exec as Sensitive and requires a context grant under enforcement.
        Assert.True(TunnelAllowlist.IsAllowed("/terminal/exec"));
        Assert.True(RequestClassifier.IsSensitive("POST", "/terminal/exec"));
    }
}
