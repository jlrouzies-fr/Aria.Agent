# // IDEA — Diff cards for agent file edits (with per-file revert)

**Status: planned. Build first on the chat-IDE track** — the single feature that makes "chat as a
programming environment" feel real, and every dependency already exists.

When the agent calls `write_file` / `edit_file` / `delete_file` / `move_path`, the inline tool
card becomes a proper **diff card**: colorized hunks, add/remove counts, a button to open the file
in the explorer viewer, and a **REVERT** button backed by a pre-image stored on the node.

## Current state

- Tool invocations render inline as generic cards: `MessageSection.SectionType.ToolActivity`
  sections built in `Chat.Rendering.razor.cs:204` and rendered in `Chat.razor:276`
  (name + args preview + result, expandable).
- The mutating builtins live on the bridge (`BuiltinTools.File.cs`, dispatched in
  `BuiltinTools.cs:36-55`); their results come back through the normal tool loop, so the web side
  sees tool name + args + result JSON already.
- The explorer viewer can open any file (`Chat.Explorer.razor.cs`, `OpenViewer`-style path at
  line 291) and markdown/code already renders server-side via Markdig + ColorCode.

## Design

### 1. Bridge: return diff + undo token from mutating file tools

In `BuiltinTools.File.cs`, before mutating:

- capture the pre-image (file content, or "absent" for a create; capped at ~512 KB — beyond that,
  store on disk regardless but skip inline diff);
- persist it to a new SQLite table in the local vault:
  `FileUndo(Id, Path, PreContent, PostHash, ToolName, CreatedAt)` — the node owns history, in line
  with "the server holds nothing of yours";
- compute a **unified diff** (plain LCS line diff, no dependency needed, ~100 lines of C#) between
  pre and post;
- add to the tool result JSON: `"diff": "...unified...", "undoToken": "<id>", "adds": n, "dels": m,
  "path": "...", "created"/"deleted": bool`.

Retention: keep the last N=200 undo rows per node, pruned on insert.

New endpoint `POST /project-files/revert` `{ undoToken }`:
- refuses if the file's current hash ≠ `PostHash` (something else touched it since) unless
  `force: true` — surfaced in the UI as a confirm step;
- restores pre-image (or deletes the created file), returns the reverse diff so the chat can show
  "reverted".

### 2. Web: render the diff card

- In the tool-callback path (`CogitationStreamRouter` → `Chat.Rendering.razor.cs`), when the
  completed tool result parses as a file-mutation payload (`diff` + `path` present), tag the
  section (e.g. `ToolCall.Kind = FileEdit`) instead of relying on tool-name matching — MCP
  file tools can opt in later by emitting the same shape.
- New `DiffCard.razor` component: header `path · +12 −3 · [OPEN] [REVERT]`, body = per-hunk
  colorized lines (green/red gutter, existing terminal aesthetic; reuse code-block CSS). Collapsed
  by default beyond ~40 lines with an expand toggle, matching current tool-card behaviour.
- `[OPEN]` calls the existing explorer viewer open path with the file's abs path.
- `[REVERT]` → `ProjectFilesClient` gains `RevertAsync(userId, undoToken, nodeId)` →
  `SendLocalRestAsync("POST", "/project-files/revert", …)`. On success the card shows a struck
  `REVERTED` state (persisted in the message payload so it survives reload).

### 3. Persistence

Tool sections are already persisted with messages (bridge-owned cogitation content via
`BridgeCogitationClient`). The diff/undoToken ride along in the same stored tool payload — revert
therefore works after a page refresh or from an old cogitation, as long as the undo row survives
pruning.

## Implementation steps

1. Unified-diff helper + pre-image capture + `FileUndo` table + result enrichment (bridge; bump
   bridge minor version).
2. `/project-files/revert` endpoint + `ProjectFilesClient.RevertAsync`.
3. `DiffCard.razor` + section tagging in `Chat.Rendering.razor.cs` + `Chat.razor` branch.
4. Persisted `REVERTED` state on the section payload.
5. CSS (diff gutter colours for both themes).

## Open questions

- `bash_exec` can also mutate files invisibly (e.g. `sed -i`) — out of scope here; the Changes tab
  (`chat-changes-tab.md`) is the safety net for those.
- Multi-file batches: revert-per-card is fine to start; a "revert whole turn" needs turn-level
  grouping of undo tokens — natural follow-up once cards exist (group by message id).
