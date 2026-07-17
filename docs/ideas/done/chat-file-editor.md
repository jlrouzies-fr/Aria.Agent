# // IDEA — Editable file viewer (EDIT button in the explorer viewer)

**Status: planned.** The explorer's read-only viewer gains an `[EDIT]` button in its header bar.
Click → editable buffer, save writes back through the bridge. Phase 1 is a styled `<textarea>`;
phase 2 swaps in a locally-bundled CodeMirror 6.

## Current state

- Viewer state lives in `Chat.Explorer.razor.cs` (`_viewerContent`, `_viewerTruncated`,
  `_viewerModalMode`, docked/modal toggle, localStorage persistence at lines 53-64). Content is
  **read-only** and size-capped on the bridge (`_viewerTruncated` when clipped).
- Reads go through `ProjectFilesClient.ReadFileAsync` → bridge `POST /project-files/read`
  (path validated against `allowedPaths`). There is **no write endpoint** on the project-files
  surface — the agent writes via the `write_file` builtin, but the UI has no path to it.

## Design

### 1. Bridge: `POST /project-files/write`

`{ path, content, baseHash, allowedPaths }` →

- path validation identical to `/read`;
- **optimistic concurrency**: `baseHash` = SHA-256 of the content the editor loaded; if the file's
  current hash differs, return `409` with the fresh content + hash — the UI offers
  "reload (discard my edits)" or "overwrite anyway";
- on success, capture a pre-image undo row exactly like agent edits do (`FileUndo` table from
  `chat-diff-cards.md`) so user edits are revertible through the same mechanism;
- return the new hash.

Add `hash` to the `/read` response while there (needed for `baseHash`).

### 2. Web: edit mode

- Header bar: `path · [EDIT] · [◫ dock/modal] · [✕]`. `[EDIT]` swaps the `<pre>` for the editor
  and becomes `[SAVE] [CANCEL]`; dirty state marks the header (`● path`).
- **Truncated files cannot enter edit mode** — saving a clipped buffer would destroy the tail.
  Button disabled with a tooltip ("file exceeds viewer cap").
- Ctrl/Cmd+S saves; Esc cancels (with confirm if dirty); navigating the tree or closing the panel
  while dirty prompts.
- After save: refresh viewer content/hash, flash a `SAVED` tick, refresh the CHANGES badge if that
  tab exists (`chat-changes-tab.md`).
- Guard against the *agent* editing the same file mid-edit: on save conflict (409) show a mini-diff
  of theirs-vs-mine using the diff renderer from `chat-diff-cards.md`.

### 3. Phase 2: CodeMirror 6, bundled locally

- Vendored into `wwwroot/vendor/codemirror/` (single ESM bundle built once, checked in) — no CDN,
  consistent with the no-external-JS stance and the app's CSP posture.
- Init via `aria-interop.js` (`ariaInterop.initEditor(el, content, lang, theme)`), language from
  extension (start: cs, razor→html, js/ts, json, md, css), theme follows the app's terminal theme.
- Blazor↔JS contract kept minimal: `getValue()` on save, `setValue()` on reload, dirty event via
  `DotNetObjectReference` callback. The textarea path stays as fallback when JS init fails.
- Later (cheap once CM6 is in): gutter markers on lines the agent changed this session (from diff
  card hunks), and "open at line" targets for `path:line` links.

## Implementation steps

1. Bridge: `hash` on read, `/project-files/write` with 409 flow + undo row (minor version bump).
2. `ProjectFilesClient.WriteFileAsync`.
3. Textarea edit mode: EDIT/SAVE/CANCEL, dirty guards, truncation lock, conflict dialog.
4. CM6 vendoring + interop + language/theme wiring.
5. Gutter markers / open-at-line (stretch, after diff cards land).

## Open questions

- Binary/huge files: `/read` already refuses or truncates; edit mode simply follows its lead.
- Multi-node: writes route with the project's `NodeId` like reads — no new routing logic.
- Undo depth for user saves shares the agent's `FileUndo` pruning (last 200) — acceptable, revisit
  if users treat this as a real editor and want deeper history.
