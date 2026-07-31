# // IDEAS — planned feature designs

Plans for accepted feature ideas, grounded in the current code. Each doc states current state
(with file references), design, implementation steps, and open questions.

## Hive — programmable edges

| Plan | One-liner |
|---|---|
| [Servitor edge node](hive-servitor-edge-node.md) | Deterministic tool step on an edge (`bash_exec`, MCP…) — build/test/validate drone output mechanically, plus a `servitor` condition mode |
| [Lexmechanic edge node](hive-lexmechanic-edge-node.md) | Return-path distillation: cheap-model digest of drone replies so Overmind context stops ballooning |

Suggested order: Servitor introduces the `EdgePhase` (Dispatch/Return) column that Lexmechanic
also needs — build Servitor first, or land the column with whichever goes first.

## Coding capabilities

| Plan | One-liner |
|---|---|
| [Edit diff feedback](edit-diff-feedback-plan.md) | Prospective diff inside approval cards + the diff returned to the model after each mutation — reuses the existing `DiffTools` |
| [run_tests tool](run-tests-tool-plan.md) | Structured build/test runner builtin: inferred commands, parsed failures (counts + file:line), capped output — wires up Planned `/test` |
| [Post-mutation verify nudge](post-mutation-verify-nudge-plan.md) | One-line "N files mutated, no build/test yet" reminder in mutation results — rides `GovernanceContext` counters |
| [Context-window discovery](context-window-discovery-plan.md) | Learn each model's context size (override → provider probe → fallback) via the format-cache; per-model compaction thresholds + `read_file` guard |
| [Tool-output distillation](tool-output-distillation-plan.md) | Small local model on the bridge digests huge tool outputs before they hit the main model's context — "Lexmechanic for the main loop" |
| [Symbol index](symbol-index-plan.md) | Bridge-local ctags-style index in SQLite powering `find_symbol` / `find_references` / `#sym:` — no LSP needed for v1 |
| [Turn checkpoints & /rewind](turn-checkpoints-rewind-plan.md) | ✅ Tag `FileUndo` rows with the turn's run id; revert a whole agent turn atomically behind the existing hash guard |
| [Worktree sub-agents & fleet placement](worktree-subagents-fleet-plan.md) | Git-worktree isolation for parallel coding children + `node` argument to place a sub-agent on a specific Fleet machine |
| [Persistent shell](persistent-shell-plan.md) | One long-lived shell per agent session (sentinel protocol, SIGINT-on-timeout) — makes the README's "persistent bash" claim true |
| [Mid-turn steering](mid-turn-steering-plan.md) | Inject user redirects mid-turn via MS `MessageInjectingChatClient` (queue STEER merges all + Ctrl+Up); Ctrl+Enter stays post-turn FIFO |

Suggested order: edit diff feedback first (smallest diff, reuses existing code), then run_tests +
verify nudge (the edit → verify → fix loop), then context-window discovery and turn checkpoints
(safety/observability), then the strategic bets: distillation, symbol index, worktree swarms.
Persistent shell and mid-turn steering are independent and can land any time.
