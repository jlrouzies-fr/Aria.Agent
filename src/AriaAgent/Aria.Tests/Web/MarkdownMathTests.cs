using Aria.Web.Helpers;
using Xunit;

namespace Aria.Tests.Web;

/// <summary>
/// Markdig math fencing + delimiter normalize — client KaTeX typesets the resulting .math nodes.
/// </summary>
public class MarkdownMathTests
{
    [Fact]
    public void ToHtml_ParenDelimitedInline_EmitsMathSpanWithIntactLatex()
    {
        var html = MarkdownHelper.ToHtml(@"manifold \((M, g_{\mu\nu})\)");

        Assert.Contains("class=\"math\"", html);
        Assert.Contains(@"g_{\mu\nu}", html);
        Assert.DoesNotContain("<p a=", html); // UseGenericAttributes must not eat {a} / similar
    }

    [Fact]
    public void ToHtml_ParenDelimitedDisplay_EmitsMathBlock()
    {
        var html = MarkdownHelper.ToHtml(@"
\(G_{\mu\nu} + \Lambda g_{\mu\nu} = 8\pi G \, T_{\mu\nu}/c^4\)
");

        Assert.Contains("class=\"math\"", html);
        Assert.Contains(@"G_{\mu\nu}", html);
        Assert.Contains(@"T_{\mu\nu}", html);
    }

    [Fact]
    public void ToHtml_DollarDelimited_StillFenced()
    {
        var html = MarkdownHelper.ToHtml(@"Inline $H(t)=\dot{a}(t)/a(t)$ here.");

        Assert.Contains("class=\"math\"", html);
        Assert.Contains(@"\dot{a}(t)", html);
    }

    [Fact]
    public void ToHtml_DoesNotRewriteMathInsideCodeFence()
    {
        var html = MarkdownHelper.ToHtml("```\n\\(x^2\\)\n```");

        Assert.DoesNotContain("class=\"math\"", html);
        Assert.Contains("\\(x^2\\)", html);
    }

    [Fact]
    public void NormalizeMathDelimiters_ConvertsParenAndBracketForms()
    {
        var n = MarkdownHelper.NormalizeMathDelimiters(@"a \((x)\) b \[y\] c");
        Assert.Equal("a $(x)$ b $$y$$ c", n);
    }
}
