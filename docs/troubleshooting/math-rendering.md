# Chat math rendering (KaTeX)

## What we ship today

Agent replies often include LaTeX (`\(…\)`, `\[…\]`, or `$…$`). Chat markdown goes through
`MarkdownHelper.ToHtml` (Markdig) into a Blazor `MarkupString`.

1. **Server (Markdig)** — `.UseMathematics()` plus a delimiter normalize step so `\(` / `\[`
   become `$` / `$$` before parse. That fences formulas so markdown does not mangle them
   (underscores as emphasis, `{a}` as generic attributes, stripped backslashes). Output is
   `<span class="math">\(…\)</span>` / `<div class="math">\[…\]</div>`.
2. **Client (KaTeX)** — `ariaInterop.typesetMath` calls `katex.render` on those `.math` nodes.
   Assets live under `wwwroot/lib/katex/` (CSS + fonts + `katex.min.js`). Chat only typesets
   when **not streaming**, so per-token re-renders do not fight KaTeX.

## If the Blazor circuit freezes again

Same failure mode as the old code-block copy button: JS mutated Blazor-owned `MarkupString`
DOM, then the next render batch desynced (see `markdown-rendering-freezes.md`). KaTeX
replaces the interior of `.math` nodes — that is intentional mutation.

Mitigations in order:

1. Confirm typeset still runs only when `!_isStreaming` (and not via a body-wide
   `MutationObserver`).
2. Typeset only once per finished message (e.g. mark `data-katex-done` and skip until the
   MarkupString is replaced wholesale on history reload).
3. **Fallback plan: Jint + KaTeX server-side** (no browser DOM mutation).

### Jint + KaTeX fallback (not implemented)

Goal: emit already-typeset HTML inside the `MarkupString` so the client only needs KaTeX
**CSS/fonts**, not `katex.render`.

Sketch:

- Add the `Jint` NuGet package; embed `katex.min.js` as a resource.
- Pool several `Engine` instances (Jint engines are **not** thread-safe — one engine must not
  serve concurrent `renderToString` calls without a lock or checkout pool).
- Custom Markdig `HtmlObjectRenderer` for `MathInline` / `MathBlock` that calls
  `katex.renderToString(tex, { throwOnError: false, displayMode })` and writes the HTML.
- Cache by formula string; fail soft to escaped source on error.
- Keep delimiter normalize + `.UseMathematics()` for parsing; replace only the HTML renderers.
- Remove or no-op `ariaInterop.typesetMath` once server HTML already contains `.katex`.

Do **not** take a dependency on `AppSoftware.KatexSharpRunner` unless you accept its age
(Jint 2.x) and license terms — a thin in-repo wrapper around modern Jint is enough.

Trade-offs vs client KaTeX: more server CPU / memory, larger SignalR payloads for math-heavy
replies, but zero client DOM mutation and no streaming freeze class of bugs.
