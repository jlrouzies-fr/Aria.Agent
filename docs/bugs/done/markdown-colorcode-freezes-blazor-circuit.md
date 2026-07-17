# Markdown / ColorCode output freezes the Blazor circuit

## Status

**Fixed** (2026-06-30) — root cause identified and resolved. ColorCode was a red herring.

## Root cause

A Blazor Server renderer desync caused by **external JS mutating Blazor-owned DOM**.

The auto-copy `MutationObserver` in `aria-interop.js` watched the entire `document.body`
(`subtree: true`). Whenever Blazor injected markdown via `MarkupString` (chat stream,
drone drawer, overmind drawer), the observer fired `_enhancePre`, which did
`pre.appendChild(btn)` — inserting a `<button>` **child** into a `<pre>` that Blazor
rendered and owns.

On the next re-render of that region (drawer open animation, `StateHasChanged`, or each
streaming token), Blazor's logical DOM tree no longer matched the real DOM. The foreign
`<button>` broke Blazor's sibling / `removeChild` navigation, so the client renderer threw
*inside* the render-batch apply and the circuit's renderer died silently — hence: WebSocket
still open (101), server handler + `StateHasChanged` both completed, no JS console error, UI
frozen until full reload. ColorCode only made it more reliable by emitting many more nodes
to mutate; plain markdown with a single `<pre>` triggered the same desync (matches
"possible cause" #2 below).

## Fix

Stop mutating Blazor-owned DOM:

1. `MarkdownHelper.ToHtml` now bakes the COPY button into the server-rendered HTML, so it
   lives inside the `MarkupString` and is owned by the Blazor renderer.
2. `aria-interop.js` removed the mutating `MutationObserver` + `_enhancePre.appendChild` and
   replaced them with a single delegated `click` listener that only **reads** the DOM and
   mutates the clicked button's own text (no structural node add/remove) — which is safe.

ColorCode syntax highlighting has been **re-enabled** as the default pipeline now that the
freeze is fixed. The only remaining precaution is a source-size guard (`HighlightSourceLimit`,
20 KB): markdown larger than that renders without highlighting to keep the SignalR render
payload reasonable. ColorCode emits a bare `<pre>` (highlighted blocks wrapped in a styled
`<div>`, no `<code>` element), so the server-side copy-button injection and the JS clone-based
copy both account for that.

## Original report (for reference)

## Symptom

Opening a Hive Overmind result drawer (or any component that renders markdown via `MarkdownHelper.ToHtml`) causes the UI to stop responding. Clicks are still captured by the browser DOM, the `/_blazor` WebSocket stays open (`101 Switching Protocols`), and the server log shows the click handler and `StateHasChanged()` both completed. No JavaScript error appears in the browser console.

## Affected content

Example result that triggers the freeze:

```text
The user wants me to create a small Python script that generates random values...
{
  "decision": "COMPLETE",
  "summary": "Python random script generated directly:\n\n```python\nimport random\n...
```

The freeze happens whether the code block is highlighted by ColorCode or not; even plain markdown rendering of this result can leave the circuit unresponsive.

## What has been tried

1. Removed `ColorCode` entirely and used a plain Markdig pipeline with `DisableHtml()`.
   - UI stays responsive, but code blocks lose syntax highlighting.
2. Re-introduced `ColorCode` only for small inputs (< 5 KB) with a 50 KB output guard.
   - Freeze returned for the same result.
3. Confirmed the rendered HTML is small (~2–3 KB) and contains no obvious malformed tags.

## Current workaround

`MarkdownHelper.ToHtml` uses the safe pipeline without `ColorCode`. Code blocks render as plain `<pre>` text inside the markdown output.

## Possible causes to investigate

- `UseAdvancedExtensions` combined with `DisableHtml` may still emit HTML that the Blazor diff algorithm cannot reconcile after external JS (e.g., the auto-copy MutationObserver) mutates the DOM.
- The auto-copy MutationObserver in `wwwroot/aria-interop.js` adds `<button>` children to `<pre>` elements rendered by Blazor via `MarkupString`. Re-renders may conflict with those mutations.
- Markdig `MarkupString` content may be interacting badly with the scoped CSS or drawer animation in `HiveOvermindDrawer`.
- A Blazor Server bug where a `MarkupString` re-render during an event dispatch leaves the client-side renderer in a bad state.

## Reproduction steps

1. Run a Hive that produces a result containing a markdown code block (e.g., the Python random script result above).
2. Wait until the collective status is `Completed` and the Overmind result banner appears.
3. Click **OPEN**.
4. UI becomes unresponsive; only a full page reload restores it.

## Files involved

- `src/AriaAgent/Aria.Web/Helpers/MarkdownHelper.cs`
- `src/AriaAgent/Aria.Web/Components/Pages/HiveOvermindDrawer.razor`
- `src/AriaAgent/Aria.Web/wwwroot/aria-interop.js` (auto-copy MutationObserver)
- `src/AriaAgent/Aria.Web/Components/Pages/Hive.razor.cs` (canvas init, re-renders)
