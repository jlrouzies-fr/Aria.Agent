# Server-side code highlighting plan

## Goal
Render markdown code blocks with syntax highlighting and a **COPY** button across the Blazor UI:
- Chat messages
- Hive Overmind drawer
- Hive Drone task result panel
- Skills preview in the agents menu

## Chosen approach
Use `Markdown.ColorCode` (NuGet) to colorize code on the server during markdown → HTML conversion.

Why server-side:
- No extra CDN files to update.
- No client-side JS race conditions with streaming Blazor content.
- Colorization is deterministic and available immediately in the initial HTML.

## Files to check / change

### 1. Package reference
`src/AriaAgent/Aria.Web/Aria.Web.csproj`
```xml
<PackageReference Include="Markdown.ColorCode" Version="3.0.1" />
```

### 2. Shared pipeline
`src/AriaAgent/Aria.Web/Helpers/MarkdownHelper.cs`
- Builds a single `MarkdownPipeline` with `UseAdvancedExtensions()` + `UseColorCode()`.
- All markdown rendering goes through `MarkdownHelper.ToHtml(...)`.
- Because `Markdown.ColorCode` adds a top-level `Markdown` namespace, call `global::Markdig.Markdown.ToHtml(...)` to avoid ambiguity.

### 3. Render locations
- `src/AriaAgent/Aria.Web/Components/Pages/Chat.razor` / `Chat.Rendering.razor.cs`  
  Assistant/user messages rendered via `RenderMarkdown` / `RenderUserMarkdown`.
- `src/AriaAgent/Aria.Web/Components/Pages/HiveOvermindDrawer.razor`  
  Overmind result rendered as `@((MarkupString)MarkdownHelper.ToHtml(Collective.ResultSummary))`.
- `src/AriaAgent/Aria.Web/Components/Pages/HiveDroneDrawer.razor`  
  Drone task results rendered as `@((MarkupString)MarkdownHelper.ToHtml(t.Result))`.
- `src/AriaAgent/Aria.Web/Components/Layout/NavMenu.Agents.razor.cs`  
  Skills preview rendered via `MarkdownHelper.ToHtml(md)`.

### 4. Remove old client-side highlighting
`src/AriaAgent/Aria.Web/Components/App.razor`
- Remove `highlight.js` CSS and JS CDN links.

`src/AriaAgent/Aria.Web/wwwroot/aria-interop.js`
- Remove any `highlightCode` / `hljs.highlightElement` calls.
- Keep only the `MutationObserver` that adds COPY buttons to `<pre>` blocks.

### 5. CSS for server-generated blocks
`src/AriaAgent/Aria.Web/wwwroot/app.css`

`Markdown.ColorCode` emits two shapes:
- **With language:** `<div style="color:#DADADA;background-color:#1E1E1E;"><pre><span style="color:#...">...</span></pre></div>`
- **Without language:** `<pre><code>...</code></pre>`

Rules must:
1. Make the `<pre>` background **transparent** so the ColorCode `div` background shows through.
2. Set `white-space: pre-wrap` so long lines wrap instead of overflowing.
3. Use a smaller monospace font inside code blocks.
4. Keep `code` (inline) styling distinct from highlighted blocks.

Example selectors:
```css
.msg-content.markdown pre,
.hv-drawer-markdown pre,
.hv-task-markdown pre {
    background: transparent;
    white-space: pre-wrap;
    font-size: 11px;
    line-height: 1.5;
}
```

Do **not** add color rules for `pre span` — ColorCode uses inline `style` attributes on each token span; CSS color would override them.

### 6. COPY button JavaScript
`src/AriaAgent/Aria.Web/wwwroot/aria-interop.js`

The enhancer must support both shapes:
- `<pre><code>...</code></pre>` → copy `code.textContent`.
- `<pre><span>...</span></pre>` (ColorCode) → copy `pre.textContent`.

```js
window.ariaInterop._enhancePre = function (pre) {
    if (pre.dataset.enhanced === 'true') return;
    var code = pre.querySelector('code');
    if (!code && pre.childElementCount === 0 && !pre.textContent.trim()) return;
    pre.dataset.enhanced = 'true';
    // ... add COPY button, copy code ? code.textContent : pre.textContent
};
```

## Verification
1. `dotnet build -clp:ErrorsOnly` succeeds.
2. Start `Aria.Bridge` then `Aria.Web`.
3. Health checks:
   ```bash
   curl -s http://localhost:5741/health
   curl -s http://localhost:5129/api/debug/mcp-bridge/health
   ```
4. In the UI, send a message containing a fenced code block with a language tag, e.g.:
   ````markdown
   ```csharp
   var x = 1;
   Console.WriteLine(x);
   ```
   ````
5. Inspect the rendered HTML — expect a `<div style="color:#DADADA;background-color:#1E1E1E;">` wrapping a `<pre>` with `<span style="color:#...">` tokens, and a COPY button on hover.
6. Also test a plain fenced block (no language) — expect `<pre><code>` with the standard dark block styling.

## Common pitfalls
- Forgetting `global::Markdig.Markdown.ToHtml` → ambiguous `Markdown` reference compile error.
- CSS `pre` background is opaque → hides the ColorCode `div` background and makes all tokens look flat.
- CSS targets `pre code` only → ColorCode blocks (no `<code>` child) get no styling.
- COPY button enhancer requires `<code>` → ColorCode blocks never get a button.
- `white-space: pre` (default) → horizontal scroll instead of wrapping.
