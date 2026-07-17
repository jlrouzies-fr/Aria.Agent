using Aria.Bridge.Infrastructure;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Locks the Layer B context-id format (defense-in-depth §4). The signed grant payload is derived
/// from this string, so a soul-wide grant and a session-scoped grant must produce distinct, stable
/// context ids — otherwise a session approval could be mistaken for a soul-wide one (or vice-versa).
/// </summary>
public class ContextGrantScopeTests
{
    [Fact]
    public void NoSession_IsSoulWide()
    {
        Assert.Equal("soul-1", ContextGrantStore.ContextId("soul-1", null));
        Assert.Equal("soul-1", ContextGrantStore.ContextId("soul-1", ""));
    }

    [Fact]
    public void WithSession_IsScopedAndDistinct()
    {
        var soulWide = ContextGrantStore.ContextId("soul-1", null);
        var sessionA = ContextGrantStore.ContextId("soul-1", "sessA");
        var sessionB = ContextGrantStore.ContextId("soul-1", "sessB");

        Assert.Equal("soul-1|sessA", sessionA);
        Assert.NotEqual(soulWide, sessionA);   // a session grant is not the soul-wide grant
        Assert.NotEqual(sessionA, sessionB);   // one session's grant does not cover another
    }

    [Fact]
    public void DifferentSouls_NeverCollide()
    {
        Assert.NotEqual(
            ContextGrantStore.ContextId("soul-1", "sess"),
            ContextGrantStore.ContextId("soul-2", "sess"));
    }
}
