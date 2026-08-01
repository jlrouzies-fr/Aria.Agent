# // IDEA — Edit diff feedback (prospective diff in approvals, diff back to the model)

**Status: shipped.** Two gaps around the existing unified-diff machinery: (1) approval cards show a
~160-char args-JSON preview, so a human approving a `multi_edit` cannot see what it actually does;
(2) after a mutation the model only receives `"Applied N edit(s)"`, so it cannot self-verify its own
edit without burning more read calls. Both fixes reuse `DiffTools.ComputeUnifiedDiff`, which already
runs post-mutation to feed the UI diff cards.

## Current state

- `edit_file` / `multi_edit` apply edits to an in-memory copy and write only when every edit succeeds
  (atomic per file) — `Aria.Bridge/BuiltinTools/BuiltinTools.File.cs` (~:204-244).
- After every mutation, `BuildFileMutationMetadata` (`BuiltinTools.File.cs` ~:420-487) computes a
  unified diff (`Aria.Bridge/BuiltinTools/DiffTools.cs`, Wagner–Fischer) and ships it as
  `metadataJson`; the web UI renders it in `Aria.Web/Components/Shared/DiffCard.razor`.
  Pre-images over a 512 KB cap skip the diff (but still record undo).
- The model-facing result text is a one-line confirmation — the diff never enters the model's context.
- Approval flow: `GovernedTool` (`Aria.Harness/Governance/GovernedTool.cs`) pauses mutation-class
  calls per `ToolClassifier`; the approval request carries only an args-JSON preview
  (~:210-211) and waits on a TCS (`Aria.Web/Services/Agent/CogitationRun.cs` ~:229-247).
- Harness reaches the bridge through `IHarnessRuntime.BridgePostAsync` →
  `WebHarnessRuntime` (`Aria.Web/Services/Llm/WebHarnessRuntime.cs`) → SignalR `BridgeRequest` →
  bridge `POST /tools/call` (`Aria.Bridge/Endpoints/ToolEndpoints.cs`).

## Design

### Part A — diff back to the model (bridge-side)

- After a successful `edit_file` / `multi_edit` / `write_file` mutation, append the unified diff to
  the tool result **text**, after the one-line confirmation. Truncate head-biased at a configurable
  cap (default 4,000 chars): keep the first N diff lines, then `… diff truncated (M more lines)`.
- Skip when no diff was computed (pre-image cap, binary, non-file tools) or the diff is empty.
- The diff is already computed for the UI metadata in the success path, so this is nearly free —
  no second diff pass.
- Bridge config knob `AgentTools:DiffFeedback` (`on`/cap), defaulted on; surfaced on the bridge
  status page next to the Projects toggle. When off, behaviour is exactly today's.
- Token cost is bounded by the cap and replaces the re-reads the model would otherwise do to
  confirm the edit landed correctly.

### Part B — prospective diff in approval cards (preview endpoint + harness + UI)

- New bridge endpoint `POST /tools/preview` with body `{name, arguments}`:
  - For `edit_file` / `multi_edit` / `write_file`: runs the *same* apply logic against an in-memory
    copy (factor the pure apply out of the mutation handlers — see steps) and returns
    `{ok, diff, truncated}` **without writing anything**. `write_file` on a new path diffs against
    empty (all-added); on an existing path, old vs new.
  - Enforces exactly the same checks as the real call: Projects toggle, Allowed Paths / session
    scope, per-tool arg validation. A preview of an out-of-scope path fails the same way the real
    call would. Audit-logged as a preview (read-only) event.
  - Other tools: `{ok: false, reason: "no-preview"}` — caller falls back.
- `GovernedTool`, when a call pauses for approval **and** the tool is in the file-mutation set,
  fetches the preview via the runtime (short timeout, ~2 s, fail-open) and attaches the diff to the
  approval-request payload.
- The Blazor approval card renders the prospective diff using the existing `DiffCard.razor`
  (read-only mode: no revert button), labelled as *proposed*. Falls back to the current args-JSON
  preview when preview is unavailable, times out, or the tool is not a file mutation.
- `delete_file` / `delete_dir` and non-file mutations keep the args preview (a future pass can add
  size/first-lines info for deletes). Seal-gated ops are untouched.

### Governance / security notes

- No new capabilities are granted — approvals remain the decision point; we only make the decision
  informed. The preview endpoint is read-only and scope-enforced, and returns content only to a
  session that could read that file anyway.
- Diff text returned to the model may duplicate content already in context; the 4 KB cap keeps
  compaction impact negligible.

## Implementation steps

1. **Bridge, refactor:** extract the pure "apply edits to content, produce new content" logic from
   the `edit_file`/`multi_edit`/`write_file` handlers into a shared internal helper (no behaviour
   change; existing tests must pass).
2. **Bridge, Part A:** after a successful mutation, append the truncated diff to the result text;
   add the `AgentTools:DiffFeedback` knob; unit tests in `Aria.Tests` (diff present, truncation
   marker correct, knob off → old text).
3. **Bridge, Part B:** `POST /tools/preview` in `ToolEndpoints.cs` reusing the step-1 helper +
   `DiffTools`; scope enforcement identical to `/tools/call`; tests (edit preview, new-file preview,
   out-of-scope preview refused, no write occurs).
4. **Harness:** `GovernedTool` fetches the preview on approval-pause for the file-mutation set
   (timeout, fail-open); extend the approval-request payload with an optional `diff` field.
5. **Web:** approval card renders the diff via `DiffCard.razor` read-only; fallback path preserved.
6. **Docs:** README "Tools & Integrations" bullet + `docs/readme/architecture.md` agent-tools list.

## Open questions

- Per-mode default: on everywhere (chosen), or off in Balanced to save tokens? Revisit with usage
  data from `context_status`.
- Preview for `install_software` (resolved package + manager + version) — noted, out of scope.
- Should `undo_file` also preview (diff of the revert)? Cheap once Part B exists; v2.
