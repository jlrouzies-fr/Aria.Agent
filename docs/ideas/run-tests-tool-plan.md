# // IDEA — run_tests tool (structured build/test feedback)

**Status: shipped.** Today "run the tests" means raw `bash_exec` guided by cheat-sheet text;
failures come back as an unbounded stdout dump the model must re-read and grep itself. A dedicated
builtin runs the project's own build/test/lint command and returns *structured* failures — counts,
failing test names with file:line — capped for context. This is the core of the edit → verify →
fix loop, and it operationalises the `/test` entry already listed Planned in `ChatCatalog.cs`.

## Current state

- `project_info` (`Aria.Bridge/BuiltinTools/BuiltinTools.ProjectInfo.cs`) sniffs manifests and
  infers install/run/build/test commands per ecosystem (.NET, Python, Node, Rust, Go…). Read-only.
- `commands_index` (`BuiltinTools.CommandsIndex.cs`) provides static per-topic cheat-sheets.
- `bash_exec` (`BuiltinTools.Shell.cs`) runs anything through `/bin/sh -c` with SecurityPolicy
  inspection; per-call timeout auto-converts to a tracked background job; stdout/stderr return
  effectively unbounded.
- Tool registration: manifest + dispatch in `BuiltinTools.cs` (:19-32, :50-89); governance
  classification in `Aria.Harness/Governance/ToolClassifier.cs` + `ToolCategories.cs`.
- `/test` is listed Planned in `Aria.Web/Services/Chat/ChatCatalog.cs` (~:53).

## Design

New builtin `run_tests` in `BuiltinTools.RunTests.cs`:

```json
{ "cwd": "...", "kind": "test|build|lint|run", "command": "optional override",
  "filter": "optional test filter", "timeoutSec": 120, "maxOutput": 4000 }
```

- **Command resolution:** explicit `command` → `project_info` inference for `kind` at `cwd` →
  error with guidance ("couldn't infer a test command here; pass `command` or run `project_info`").
- **Execution:** reuse the bash execution path (SecurityPolicy inspection, timeout, background
  conversion for long suites). `filter` maps to the ecosystem's native filter flag (`--filter`,
  `-k`, `--testNamePattern`, …) only for inferred commands; for a custom `command`, a non-empty
  `filter` is rejected with guidance (append it yourself).
- **Output parsing** per ecosystem, v1: dotnet (VSTest console: failed test names + `Failed: N`
  summary), pytest (`FAILED path::test` + short summary), jest/vitest (✕ / FAIL blocks),
  cargo test (`failures:` list), go test (`--- FAIL:`), generic fallback = exit code + tail.
- **Model-facing result** (capped at `maxOutput`, failures list capped at top 20):

  ```
  ◈ TEST RUN [dotnet test] — FAILED (exit 1, 42.3s)
  passed: 181  failed: 2  skipped: 0
  ✗ CartServiceTests.Checkout_EmptyCart_Throws — CartService.cs:88
    Expected CheckoutException, got null
  — output tail (last 1,500 chars) —
  ```

  Success path returns counts + a couple of lines only.
- **Governance:** classify exactly like `bash_exec` (command-execution category) so Balanced/Coding
  budgets and Strict/Paranoid approvals apply unchanged; `cwd` is scope-checked like other path
  tools.
- **UI:** reuse the standard inline tool-activity line; a dedicated pass/fail card is later polish.
- **Prompt addendum** (only when Projects tools are registered, next to the existing terminal
  addendum in `Harness.cs`): prefer `run_tests` over `bash_exec` for build/test/lint.

## Implementation steps

1. Parser module `Aria.Bridge/BuiltinTools/TestOutputParsers.cs` — one parser per ecosystem +
   fallback; unit tests with captured fixture outputs (green and red) in `Aria.Tests`.
2. Tool handler + manifest/dispatch registration in `BuiltinTools.cs`; reuse the bash exec +
   SecurityPolicy path.
3. `ToolClassifier`/`ToolCategories` mapping (same class as `bash_exec`); prompt addendum string.
4. Filter-flag mapping per inferred ecosystem.
5. Docs: README "Tools & Integrations" bullet + `docs/readme/architecture.md` agent-tools list.
6. Flip `/test` in `ChatCatalog.cs` from Planned to wired (chat command that asks the agent to run
   `run_tests`) — or split to a follow-up if command plumbing is non-trivial; note it in the PR.

## Open questions

- TRX / JUnit XML ingest for exact counts (v2: run with a logger to a temp file, parse XML —
  far more robust than console scraping for dotnet).
- Streaming progress to the UI for long suites (the background-job plumbing already exists).
- `kind: "lint"` inference is weak in `project_info` today — acceptable; explicit `command` covers it.
