using Aria.Shared;
using Xunit;

namespace Aria.Tests.Shared;

/// <summary>
/// Locks the egress-pinning rule behind the key-custody guarantee: a keyed LLM call is only ever sent to
/// the host the node declared for that channel, never to a URL a compromised server supplies.
/// </summary>
public class ProviderPinningTests
{
    [Fact]
    public void PinToHost_KeepsMatchingBase()
    {
        var pinned = PublicProviderCatalog.PinToHost("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions");
        Assert.Equal("https://api.openai.com/v1/chat/completions", pinned);
    }

    [Fact]
    public void PinToHost_RedirectsTamperedHostBackToAuthoritative()
    {
        // A compromised server points the key at its own host — pinning forces it back to OpenAI.
        var pinned = PublicProviderCatalog.PinToHost("https://api.openai.com/v1", "https://attacker.example/v1/chat/completions");
        Assert.StartsWith("https://api.openai.com/", pinned);
        Assert.DoesNotContain("attacker", pinned);
        Assert.EndsWith("/v1/chat/completions", pinned);
    }

    [Fact]
    public void PinToHost_PrependsBasePathWhenRequestOmittedIt()
    {
        // Server tampered the base to a bare host, so the requested path lost the "/v1" prefix.
        var pinned = PublicProviderCatalog.PinToHost("https://api.openai.com/v1", "http://127.0.0.1:9/chat/completions");
        Assert.Equal("https://api.openai.com/v1/chat/completions", pinned);
    }

    [Fact]
    public void PinToHost_PreservesQuery()
    {
        var pinned = PublicProviderCatalog.PinToHost("https://generativelanguage.googleapis.com/v1beta/openai/",
            "https://attacker.example/v1beta/openai/chat/completions?alt=sse");
        Assert.StartsWith("https://generativelanguage.googleapis.com/", pinned);
        Assert.DoesNotContain("attacker", pinned);
        Assert.EndsWith("?alt=sse", pinned);
    }

    [Fact]
    public void PinToHost_MalformedRequestFallsBackToAuthoritativeBase()
    {
        var pinned = PublicProviderCatalog.PinToHost("https://api.openai.com/v1", "not a url");
        Assert.Equal("https://api.openai.com/v1", pinned);
    }

    [Theory]
    [InlineData("OpenAI", "https://api.openai.com/v1")]
    [InlineData("openai", "https://api.openai.com/v1")]
    [InlineData("Groq",   "https://api.groq.com/openai/v1")]
    public void CanonicalUrl_ResolvesPublicProviders(string name, string url)
        => Assert.Equal(url, PublicProviderCatalog.CanonicalUrlFor(name));

    [Fact]
    public void CanonicalUrl_UnknownIsNull() => Assert.Null(PublicProviderCatalog.CanonicalUrlFor("My Local LLM"));
}
