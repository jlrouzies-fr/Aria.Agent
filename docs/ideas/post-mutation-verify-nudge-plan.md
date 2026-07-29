# // IDEA — Post-mutation verify nudge

**Status: shipped.** After the agent edits files, nothing prompts it to check its work — the turn
just continues (or ends) on faith. A tiny harness behaviour: when a turn accumulates file mutations
and no verification has run, append a one-line nudge to the mutation tool's own result. Zero new
plumbing — it rides on the per-turn `GovernanceContext` counters and the existing tool-result path.

## Current state

- `GovernanceContext` (`Aria.Harness/Governance/GovernanceContext.cs`) holds per-turn counters
  (tool calls, reads) and a 16-entry ring buffer for loop detection; `BeginTurn` resets state each
  turn (`Aria.Harness/Core/Harness.cs` ~:455).
- Every tool call passes through `GovernedTool` (`Aria.Harness/Governance/GovernedTool.cs`), which
  knows the tool's category via `ToolClassifier` / `ToolCategories` and sees the result.
- `project_info` can infer the verify command; the `run_tests` tool (see
  [run-tests-tool-plan.md](run-tests-tool-plan.md)) gives the nudge a concrete target. The nudge
  text composes with the diff feedback appended by
  [edit-diff-feedback-plan.md](edit-diff-feedback-plan.md) (both live in the same result text).

## Design

- Add two pieces of per-turn state to `GovernanceContext`:
  - `MutationCount` — incremented in `GovernedTool` when a **file-mutation** tool
    (`write_file`, `edit_file`, `multi_edit`, `delete_*`, `move_path`, `create_dir`) completes
    successfully. Deliberately not bash (can't tell reliably) and not git ops.
  - `VerificationRan` — set when `run_tests` succeeds, or a `bash_exec` command matches a
    build/test pattern (`dotnet test|build`, `pytest`, `npm test|run build`, `cargo test`,
    `go test`, `make test`…). The pattern list lives next to `ToolCategories`.
- After a successful file-mutation result, when `MutationCount` is 1 or a multiple of 5 **and**
  `!VerificationRan`, append to that tool's result text:

  `◈ N file(s) mutated this turn, no build/test run yet — consider verifying (run_tests, or project_info to infer the command).`

- Modes: meaningful in Off/Balanced/Coding; pointless in Plan (mutations blocked); in
  Strict/Paranoid humans already approve each mutation — still append (approvals come in batches,
  the reminder helps the human too). Toggle `Governance:VerifyNudge`, default on.
- Hard rules: at most one nudge per tool result; never blocks, never fails a call, never counts
  against budgets.

## Implementation steps

1. `MutationCount` / `VerificationRan` + the verification command-pattern list in
   `GovernanceContext` / `ToolCategories`; reset both in `BeginTurn`.
2. Nudge append in `GovernedTool`'s post-invocation path (file-mutation set + thresholds).
3. Tests in `Aria.Tests`: nudge at 1st and 6th mutation, suppressed once verification ran,
   suppressed when the toggle is off.
4. Docs: one bullet in the README governance section and `docs/readme/architecture.md`.

## Open questions

- Should a turn that *ends* with unverified mutations raise a UI hint (amber marker on the turn)?
  Nice, but needs turn-summary plumbing — v2.
- Sub-agents get fresh counters (existing behaviour for all governance state) — acceptable; the
  parent sees their aggregate report.
