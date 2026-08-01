# // IDEA — Turn checkpoints & `/rewind` (atomic revert of a whole agent turn)

**Status: shipped.** `undo_file` reverts one file at a time off a 200-row snapshot stack; a coding
turn touches many files, so "take me back to before this turn" means N manual undos in the right
order. Tag every mutation with the turn that caused it, and `/rewind` reverts the whole turn
atomically behind the existing hash guard. This is the trust feature that makes users willing to
let the agent write code aggressively — and `/rewind` is Available in `ChatCatalog.cs`.

## Current state (as shipped)

- Every mutating file tool writes a `FileUndo` row (path, pre-content, post-hash, tool name,
  optional `Checkpoint`) to the bridge SQLite, pruned to 200 rows —
  `BuiltinTools.File.cs` (`BuildFileMutationMetadata`).
- `undo_file` walks the not-yet-reverted stack, refuses if the file changed since the snapshot
  (post-hash guard), and restores via `Aria.Bridge/Infrastructure/FileReverter.cs`; the revert is
  itself recorded as a mutation (undo-of-undo works). The Explorer UI exposes the same reverter at
  `/project-files/revert`.
- Each cogitation turn (and headless/spawned child run) mints a checkpoint id on
  `HarnessContext.CurrentTurnCheckpoint`; `BridgeMcpTool` stamps it onto `/tools/call` →
  `ToolsCallRequest.Checkpoint` → `FileUndo.Checkpoint`.
- `/rewind` / `/rewind <n>` intercepts in chat (never reaches the model), discovers recent
  checkpoints from transcript mutation metadata, and calls
  `POST /project-files/revert-checkpoint`. Diff cards expose one **REVERT TURN** button per
  checkpoint.

## Design

### Checkpoint tagging

- New nullable `checkpoint` column on `FileUndo` (migration; old rows = null, excluded from rewind).
- The harness stamps each turn with a checkpoint id and sends it as metadata on every `/tools/call`
  request (`ToolsCallRequest.Checkpoint`, threaded from `HarnessContext.CurrentTurnCheckpoint`).
- The bridge copies it onto the `FileUndo` row for every mutation in that call. Calls without the
  field behave exactly as today (manual Quick Exec, MCP, older servers).

### `/rewind`

- Chat command `/rewind` (no args = most recent turn that mutated files in this cogitation;
  `/rewind <n>` = nth mutating turn back, small cap like 5).
- Resolves to `POST /project-files/revert-checkpoint {checkpoint}`:
  - Fetches that checkpoint's unreverted `FileUndo` rows, newest first.
  - Applies `FileReverter` per file with the existing post-hash guard.
  - Returns a per-file report: `reverted` / `skipped (changed since)` / `missing`.
  - Records the revert batch as a new mutation set under a fresh checkpoint — rewind-of-rewind works.
- User-initiated `/rewind` and REVERT TURN go through the same Layer B path as Explorer revert
  (not the agent Strict/Paranoid approval dialog).
- UI: one REVERT TURN button per checkpoint on the turn's DiffCards; `/rewind` is the
  discoverable path.

### Failure honesty

- Partial conflicts never abort the batch silently: each file reports its own outcome, and the
  summary lists what was NOT reverted and why. The agent (or user) can then decide per file.

## Implementation steps

1. `checkpoint` column + migration in the bridge DB initializer. ✅
2. Checkpoint plumbing: turn AsyncLocal → `BridgeMcpTool` body → `ToolsCallRequest` →
   `BuildFileMutationMetadata`. ✅
3. `POST /project-files/revert-checkpoint` on the bridge reusing `FileReverter`; per-file report. ✅
   (Uses the existing `/project-files/` tunnel prefix rather than a new `/files/` path.)
4. `/rewind` chat command in `Aria.Web` (catalog flip to Available + handler). ✅
5. Diff-card REVERT TURN button (one per checkpoint in the message). ✅
6. Tests in `Aria.Tests`: multi-file revert order, hash-guard skip reporting, rewind-of-rewind,
   null-checkpoint rows untouched. Docs: README + `docs/readme/security.md` + bridge-features. ✅

## Open questions

- Background runs (vigils, Hive drones, spawned agents): checkpoint = their run id works
  unchanged; `/rewind` in the parent chat only sees checkpoints *it* can name — child-run reverts
  may need the run id quoted explicitly. Acceptable v1.
- Cross-node turns (mutations on two bridges in one turn): revert is per-bridge; the command
  reports per node. Fine, but the UI copy must say so.
- Interaction with `git_discard`: users with clean git trees may prefer it — complementary, not a
  conflict; `undo_file`/`rewind` also covers non-git directories.
