using NTokenizers.Extensions.Spectre.Console.Styles;
using Spectre.Console;

namespace Aria.Console;

/// <summary>
/// NTokenizers MarkdownStyles theme for Aria — warm reddish tint matching the 40K aesthetic.
/// All colors are defined as hex so they're easy to tweak centrally.
/// </summary>
public static class AriaMarkdownTheme
{
    // ── Retro CRT Palette ─────────────────────────────────────────────────────
    private static readonly Color TitleRed   = new(0xFF, 0x50, 0x30); // #ff5030
    private static readonly Color BrightRed  = new(0xFF, 0xA0, 0x80); // #ffa080
    private static readonly Color NormalRed  = new(0xFF, 0x80, 0x60); // #ff8060
    private static readonly Color MutedRed   = new(0xCC, 0x70, 0x50); // #cc7050
    private static readonly Color DeadRed    = new(0x99, 0x50, 0x40); // #995040
    private static readonly Color BodyText   = new(0xFF, 0xA0, 0x80); // #ffa080
    private static readonly Color GoldBright = new(0xFF, 0x8C, 0x00); // #ff8c00
    private static readonly Color GoldNormal = new(0xD4, 0xA0, 0x20); // #d4a020
    private static readonly Color BorderGlow = new(0xFF, 0x50, 0x30); // #ff5030
    private static readonly Color BorderDim  = new(0x8B, 0x20, 0x10); // #8b2010
    private static readonly Color SurfaceBg  = new(0x1A, 0x08, 0x08); // #1a0808

    public static readonly MarkdownStyles Styles = BuildStyles();

    private static MarkdownStyles BuildStyles()
    {
        var s = new MarkdownStyles
        {
            DefaultStyle   = new Style(BodyText),
            Bold           = new Style(BrightRed,  decoration: Decoration.Bold),
            Italic         = new Style(NormalRed,  decoration: Decoration.Italic),
            Emphasis       = new Style(NormalRed,  decoration: Decoration.Italic),
            HorizontalRule = new Style(BorderDim),
            Blockquote     = new Style(DeadRed,    decoration: Decoration.Italic),
            Link           = new Style(GoldBright, decoration: Decoration.Underline),
            Image          = new Style(GoldNormal),
            CodeInline     = new Style(GoldBright, SurfaceBg),
            CodeBlock      = new Style(GoldBright, SurfaceBg),
            TableCell      = new Style(MutedRed),
            Heading        = new Style(TitleRed,   decoration: Decoration.Bold),
        };

        // Sub-style objects are read-only references — mutate their properties in place.
        s.MarkdownHeadingStyles.Level1         = new Style(TitleRed,  decoration: Decoration.Bold);
        s.MarkdownHeadingStyles.Level2To4      = new Style(NormalRed, decoration: Decoration.Bold);
        s.MarkdownHeadingStyles.Level5AndAbove = new Style(MutedRed);

        s.MarkdownListItemStyles.Marker        = new Style(BorderGlow);
        s.MarkdownOrderedListItemStyles.Number = new Style(BorderGlow);

        return s;
    }
}
