# Markdown rendering freezes the Blazor circuit

## Symptom

Clicking a button that reveals markdown content (e.g., **OPEN** on a completed Hive Overmind
result, or a streaming chat message containing a code block) does nothing, and the whole
Blazor Server circuit becomes unresponsive. Telltale signs:

- The browser console shows **no JavaScript error**.
- The `/_blazor` WebSocket is still `101 Switching Protocols` (connection is alive).
- Server logs show the click handler ran and `StateHasChanged()` completed.
- Only a **full page reload** restores the UI.

## Root cause

**External JavaScript mutating DOM that Blazor owns.** This is the real cause — see the red
herring section below for what it is *not*.

Markdown is injected as a `MarkupString` (`@((MarkupString)MarkdownHelper.ToHtml(...))`), so
the `<pre>`/`<code>`/`<span>` nodes it produces are **owned and tracked by the Blazor Server
renderer**. If any JS inserts or removes child nodes inside that subtree, Blazor's logical
DOM tree no longer matches the real DOM. On the next re-render of that region (drawer open
animation, `StateHasChanged`, or each streaming token) Blazor's sibling / `removeChild`
navigation walks into the foreign node, throws **inside the render-batch apply**, and the
client renderer dies silently — hence the WebSocket stays open and the server side looks
healthy, but the UI is frozen.

The specific offender was a global `MutationObserver` in `wwwroot/aria-interop.js` that
watched all of `document.body` and did `pre.appendChild(copyButton)` on every code block —
inserting a foreign `<button>` child into Blazor-owned `<pre>` elements.

### Red herring: it is NOT ColorCode / DOM size

The earlier version of this doc (and the bug report) blamed `ColorCode` for "exploding the
DOM" with thousands of `<span>`s. **That was wrong.** Proof:

- The freeze reproduced with the **plain** pipeline (a single `<pre>`, no ColorCode) and with
  rendered HTML of only ~2–3 KB.
- Removing ColorCode did not fix it; removing the **JS DOM mutation** did.

ColorCode only made the crash *more reliable* (more nodes → higher chance the mutated one is
re-rendered). It is a perf consideration for very large blocks, not the freeze cause. Don't
waste time profiling "Recalculate Style" — that's the wrong trail.

## How to investigate (quick triage)

Work from the cheapest check to the most specific:

1. **Confirm the circuit is alive, not the page.** DevTools → Network → `_blazor` WS is still
   open (101), and there is **no** red console error. A truly crashed page or a JS exception
   points elsewhere; this signature (alive socket, no error, dead UI) is the Blazor-renderer
   desync.
2. **Search for JS that mutates rendered content.** The fingerprint is JS touching nodes that
   Blazor produced:
   ```bash
   grep -nE "appendChild|insertBefore|removeChild|\.remove\(\)|innerHTML *=" \
     src/AriaAgent/Aria.Web/wwwroot/aria-interop.js
   grep -n "MutationObserver" src/AriaAgent/Aria.Web/wwwroot/aria-interop.js
   ```
   Any `MutationObserver` on `document.body` with `subtree: true` that *writes* to the DOM is
   the prime suspect — especially one enhancing `<pre>`, tables, or links inside markdown.
3. **Confirm the markdown subtree is the target.** If the mutating code keys off elements that
   only exist inside `MarkupString` output (`pre`, `code`, `table`, `a`), and the freeze
   happens exactly when such content renders, you've found it.
4. **Bisect with a non-mutating render.** Temporarily make the JS enhancement a no-op (don't
   touch markdown rendering). If the UI stays responsive, the markdown *pipeline* is innocent;
   the JS mutation is guilty. (This is the opposite conclusion the old doc jumped to — verify,
   don't assume ColorCode.)

## Fix

**Never insert or remove DOM nodes inside Blazor-rendered content from JS.** Two safe patterns:

1. **Render the enhancement server-side** so it's part of the `MarkupString` and owned by
   Blazor. The COPY button is baked into the HTML in
   `Aria.Web/Helpers/MarkdownHelper.cs` (`AddCopyButtons` injects it into each `<pre>`).
2. **Read the DOM, never mutate its structure.** Replace mutating observers with a single
   **delegated event listener** that only reads. In `aria-interop.js` the copy handler is:
   ```js
   document.addEventListener('click', function (e) {
       var btn = e.target.closest && e.target.closest('.code-copy-btn');
       if (!btn) return;
       var pre = btn.closest('pre');
       // clone OFF the live DOM so Blazor is untouched, then read the text
       var clone = pre.cloneNode(true);
       var cloneBtn = clone.querySelector('.code-copy-btn');
       if (cloneBtn) cloneBtn.remove();
       var code = clone.querySelector('code');
       var text = (code ? code.textContent : clone.textContent).replace(/^\n/, '');
       navigator.clipboard.writeText(text || '');
       // mutating the button's OWN text (no node add/remove) is safe
   });
   ```
   Reading is safe; cloning happens off-DOM; mutating an existing node's `textContent` does
   not change sibling structure, so Blazor's reconciliation is unaffected.

With the mutation removed, `ColorCode` syntax highlighting is safe and is the default pipeline
again (`HighlightedPipeline` in `MarkdownHelper.cs`). The only remaining guard is a
source-size limit (`HighlightSourceLimit`, 20 KB) that drops highlighting for very large
markdown to keep the SignalR render payload reasonable — a perf precaution, not a freeze fix.

## Related files

- `Aria.Web/Helpers/MarkdownHelper.cs` — pipelines + server-side copy-button injection
- `Aria.Web/wwwroot/aria-interop.js` — delegated (non-mutating) copy handler
- `docs/Bugs/markdown-colorcode-freezes-blazor-circuit.md` — original bug report + resolution

## Verification

1. Build and restart `Aria.Web`.
2. Open a Hive Overmind conclusion (or a chat message) containing a fenced code block.
3. The drawer/message renders with syntax highlighting and a working hover **COPY** button.
4. The UI stays responsive and closable; copying yields the code **without** the "COPY" label.
5. Stream a long response with multiple code blocks — no freeze during or after streaming.
