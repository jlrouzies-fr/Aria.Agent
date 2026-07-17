using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aria.Console;

/// <summary>
/// Retro CRT chrome for Aria.Console — red phosphor palette, double-line borders,
/// and helpers for drawing the header, message cards, and status bar.
/// </summary>
public static class AriaRetroChrome
{
    // ── CRT Palette ───────────────────────────────────────────────────────────
    public static readonly Color BloodRed    = new(0xCC, 0x3D, 0x00); // #cc3d00
    public static readonly Color PhosphorRed = new(0xFF, 0x50, 0x30); // #ff5030
    public static readonly Color Amber       = new(0xFF, 0x8C, 0x00); // #ff8c00
    public static readonly Color DimRed      = new(0x8B, 0x20, 0x10); // #8b2010
    public static readonly Color DarkSurface = new(0x1A, 0x08, 0x08); // #1a0808
    public static readonly Color BodyText    = new(0xFF, 0xA0, 0x80); // #ffa080
    public static readonly Color MutedText   = new(0xCC, 0x70, 0x50); // #cc7050
    public static readonly Color Gold        = new(0xD4, 0xA0, 0x20); // #d4a020

    /// <summary>Creates a retro double-lined card around the supplied content.</summary>
    public static Panel Card(IRenderable content, string? header = null, bool expand = true)
    {
        var panel = new Panel(content)
            .DoubleBorder()
            .BorderColor(PhosphorRed)
            .Padding(1, 0);

        if (expand)
            panel.Expand();

        if (!string.IsNullOrWhiteSpace(header))
            panel.Header(new PanelHeader(
                $"[bold {PhosphorRed.ToMarkup()}]{Markup.Escape(header)}[/]",
                Justify.Left));

        return panel;
    }

    /// <summary>The retro Aria header panel.</summary>
    public static IRenderable Header()
    {
        var logo = @"
░█████╗░██████╗░██╗░█████╗░  ░█████╗░░██████╗░███████╗███╗░░██╗████████╗
██╔══██╗██╔══██╗██║██╔══██╗  ██╔══██╗██╔════╝░██╔════╝████╗░██║╚══██╔══╝
███████║██████╔╝██║███████║  ███████║██║░░██╗░█████╗░░██╔██╗██║░░░██║░░░
██╔══██║██╔══██╗██║██╔══██║  ██╔══██║██║░░╚██╗██╔══╝░░██║╚████║░░░██║░░░
██║░░██║██║░░██║██║██║░░██║  ██║░░██║╚██████╔╝███████╗██║░╚███║░░░██║░░░
╚═╝░░╚═╝╚═╝░░╚═╝╚═╝╚═╝░░╚═╝  ╚═╝░░╚═╝░╚═════╝░╚══════╝╚═╝░░╚══╝░░░╚═╝░░░";

        var grid = new Grid();
        grid.AddColumn();
        grid.AddRow(Align.Center(new Markup($"[{PhosphorRed.ToMarkup()}]{logo}[/]")));
        grid.AddRow(Align.Center(new Markup($"[dim {MutedText.ToMarkup()}]Bridge-mandatory console edition[/]")));

        return Card(grid, header: null, expand: true);
    }

    /// <summary>Renders a section divider with a retro label.</summary>
    public static Rule Rule(string? label = null)
    {
        var rule = string.IsNullOrWhiteSpace(label)
            ? new Rule()
            : new Rule($"[bold {PhosphorRed.ToMarkup()}]{Markup.Escape(label)}[/]");

        rule.Style = new Style(DimRed);
        rule.Justification = Justify.Left;
        return rule;
    }

    /// <summary>Renders the bottom key-hint/status line.</summary>
    public static void StatusBar()
    {
        var rule = new Rule($"[dim {MutedText.ToMarkup()}]{Markup.Escape("[Enter] Send  [R] Reset  [/exit] Quit  :: Aria Agent Retro Console")}[/]")
        {
            Style = new Style(DimRed),
            Justification = Justify.Left,
        };
        AnsiConsole.Write(rule);
    }

    /// <summary>Renders a colored prompt prefix.</summary>
    public static void Prompt(string label = "User")
    {
        AnsiConsole.Markup($"[bold {PhosphorRed.ToMarkup()}]{label}:[/] ");
    }

    /// <summary>Renders a user message as a retro card.</summary>
    public static IRenderable UserCard(string text)
        => Card(new Markup($"[{Amber.ToMarkup()}]{Markup.Escape(text)}[/]"), "You", expand: true);

    // ── Status helpers ────────────────────────────────────────────────────────
    public static void Success(string text) => AnsiConsole.MarkupLine($"[{Amber.ToMarkup()}]✓ {Markup.Escape(text)}[/]");
    public static void Warning(string text) => AnsiConsole.MarkupLine($"[{Gold.ToMarkup()}]! {Markup.Escape(text)}[/]");
    public static void Error(string text)   => AnsiConsole.MarkupLine($"[{BloodRed.ToMarkup()}]✗ {Markup.Escape(text)}[/]");
    public static void Info(string text)    => AnsiConsole.MarkupLine($"[{MutedText.ToMarkup()}]> {Markup.Escape(text)}[/]");
}
