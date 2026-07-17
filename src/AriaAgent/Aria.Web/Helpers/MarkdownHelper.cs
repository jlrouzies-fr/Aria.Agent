using System.Net;
using Markdig;

namespace Aria.Web.Helpers;

public static class MarkdownHelper
{
    // Source longer than this skips ColorCode and renders without syntax highlighting.
    // ColorCode wraps every token in a <span>, so highlighting a very large block inflates
    // the HTML (and the SignalR render payload) heavily for little benefit. This is now a
    // pure perf/payload guard — it is NOT the circuit-freeze fix (that was the JS DOM
    // mutation; see ToHtml below and docs/Bugs/markdown-colorcode-freezes-blazor-circuit.md).
    private const int HighlightSourceLimit = 20_000;

    // File viewer content gets a much higher limit than chat messages: it's a one-shot,
    // user-initiated render (not a per-token streaming broadcast that repeats the payload
    // cost on every chunk), and whole source files routinely exceed 20 KB. Matches
    // ProjectFileEndpoints.MaxReadBytes (2 MB) so any file the bridge lets through renders highlighted.
    private const int FileHighlightSourceLimit = 2 * 1024 * 1024;

    // Plain pipeline: advanced Markdown features, no raw HTML passthrough, no ColorCode.
    // Used as the fallback when the source is too large to highlight or ColorCode throws.
    public static readonly MarkdownPipeline SafePipeline =
        new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();

    // Default pipeline: ColorCode syntax highlighting on fenced code blocks.
    public static readonly MarkdownPipeline HighlightedPipeline =
        Markdown.ColorCode.MarkdownPipelineBuilderExtensions.UseColorCode(
            new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .DisableHtml())
            .Build();

    // Copy button is baked into the server-rendered HTML so it lives inside Blazor's
    // MarkupString and is owned by the Blazor renderer. The previous approach — a JS
    // MutationObserver that appended this button into Blazor-rendered <pre> elements —
    // inserted foreign child nodes that desynced Blazor Server's logical DOM tree and
    // froze the circuit on the next re-render. See
    // docs/Bugs/markdown-colorcode-freezes-blazor-circuit.md. The matching JS now only
    // reads the DOM (delegated click handler); it never inserts or removes nodes.
    private const string CopyButton =
        "<button type=\"button\" class=\"code-copy-btn\" aria-label=\"Copy code to clipboard\">COPY</button>";

    // Maps a file extension to the language tag ColorCode expects on a fenced code block.
    // Extensions not listed render as an untagged (plain) code block.
    private static readonly Dictionary<string, string> ExtensionLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp", [".razor"] = "html", [".cshtml"] = "html", [".fs"] = "fsharp",
        [".ts"] = "typescript", [".tsx"] = "typescript", [".js"] = "javascript", [".jsx"] = "javascript",
        [".json"] = "json", [".xml"] = "xml", [".csproj"] = "xml",
        [".html"] = "html", [".css"] = "css", [".scss"] = "css",
        [".py"] = "python", [".sql"] = "sql", [".sh"] = "bash", [".bash"] = "bash",
        [".ps1"] = "powershell", [".yml"] = "yaml", [".yaml"] = "yaml", [".md"] = "markdown",
        [".java"] = "java", [".cpp"] = "cpp", [".c"] = "cpp", [".h"] = "cpp",
        [".go"] = "go", [".rs"] = "rust", [".rb"] = "ruby", [".php"] = "php",
        [".toml"] = "toml", [".ini"] = "ini", [".cfg"] = "ini",
    };

    /// <summary>Renders a file's raw text as a syntax-highlighted code block, language-tagged from
    /// its extension. Used by the project Explorer's file viewer (plain text, not chat markdown).</summary>
    public static string ToHtmlForFile(string relPath, string? content)
    {
        if (string.IsNullOrEmpty(content)) return "";

        var lang = ExtensionLanguages.GetValueOrDefault(Path.GetExtension(relPath), "");

        // The content becomes the body of a fenced code block, so its fence must use more
        // backticks than any backtick run already inside it (Markdown fence-nesting rule).
        var fenceLen = 3;
        var run = 0;
        foreach (var c in content)
        {
            if (c == '`') { run++; fenceLen = Math.Max(fenceLen, run + 1); }
            else run = 0;
        }
        var fence = new string('`', fenceLen);

        return Render($"{fence}{lang}\n{content}\n{fence}", FileHighlightSourceLimit);
    }

    public static string ToHtml(string? text) => Render(text, HighlightSourceLimit);

    private static string Render(string? text, int highlightLimit)
    {
        if (string.IsNullOrEmpty(text)) return "";

        try
        {
            var pipeline = text.Length > highlightLimit ? SafePipeline : HighlightedPipeline;
            return AddCopyButtons(global::Markdig.Markdown.ToHtml(text, pipeline));
        }
        catch
        {
            // ColorCode failed on this input — degrade gracefully to plain markdown,
            // then to escaped raw text as a last resort.
            try { return AddCopyButtons(global::Markdig.Markdown.ToHtml(text, SafePipeline)); }
            catch { return $"<pre>{WebUtility.HtmlEncode(text)}</pre>"; }
        }
    }

    // Both Markdig (DisableHtml) and ColorCode emit a bare "<pre>" for code blocks
    // (ColorCode also wraps it in a styled <div>, but the <pre> itself has no attributes),
    // so this targets code blocks exactly without touching inline <code>.
    private static string AddCopyButtons(string html) =>
        html.Replace("<pre>", $"<pre class=\"code-block-wrapper\" style=\"position:relative\">{CopyButton}");
}
