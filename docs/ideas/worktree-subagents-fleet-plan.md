# // IDEA — Worktree-isolated sub-agents & fleet placement (parallel coding that doesn't collide)

**Status: planned.** `spawn_agent` can already run up to 4 children concurrently — but they all
share the same working tree, so parallel *coding* agents would stomp on each other's files. Two
upgrades, shippable independently: (1) optional git-worktree isolation per child, with a clean
merge path; (2) optional node placement, so a child runs against a specific Fleet machine (the one
with 64 GB RAM and the fast disk) — something no single-machine agent can offer, on top of the
cross-node approval gate that already exists.

## Current state

- `spawn_agent` / `agent_result`: `Aria.Harness/Tools/SpawnAgentTools.cs` →
  `Aria.Web/Services/Agent/SubAgentSpawnService.cs` (max 4 concurrent, depth 1, children get no
  spawner) → `AgentBackgroundExecutor.SpawnChildRunAsync` (`allowBridgeTools: true`, inherits
  parent's governance mode and session context grant).
- All children operate on the same filesystem view as the parent; no isolation primitive exists.
- Git tools are `git_status/diff/log/stage/commit/discard` (`BuiltinTools.Git.cs`) — no worktree
  support.
- Fleet: `fleet_status` (`Aria.Harness/Tools/FleetStatusTools.cs`) hands the agent per-node
  hardware/free RAM/models; multi-node terminal tools merge into `PathRoutedTerminalTool`
  (routes by path prefix); cross-node calls pause for approval
  (`GovernedTool.cs` ~:62-73, `ApproveCrossNodeCalls`).

## Design

### Phase 1 — worktree isolation (same node)

- `spawn_agent` gains `isolation: "none" | "worktree"` (default `none`) and `base_ref`
  (default `HEAD`).
- New bridge builtin `git_worktree {action: add|list|remove, repo, name, ref?}` — explicit tool
  rather than bash so it stays allowlisted, scope-checked, and auditable. Worktrees live under the
  bridge's data dir (`worktrees/<sanitised-name>/`), NOT inside the repo, so the child's tree never
  pollutes the parent's `git status`.
- When isolation is requested, the spawn service: creates the worktree via the owning bridge →
  runs the child with its file/bash tools scoped to the worktree path (automatic, run-scoped scope
  extension — never widens the node's Allowed Paths permanently) → on completion, `agent_result`
  returns the child's report **plus** `{worktree, branch, diffStat}` metadata.
- Merge: the parent decides, using existing git tools (the worktree is a normal branch:
  `git merge` / cherry-pick from the repo root). A `git_worktree {action: remove}` after merge
  cleans up. Conflicts are reported honestly and left to the parent agent/user — no auto-resolution.
- Orphan sweep on bridge start: worktrees older than 7 days with no active run are listed in the
  bridge log (not auto-deleted v1 — deletion is a human/`remove` decision).

### Phase 2 — fleet placement (`node` argument)

- `spawn_agent` gains `node: "<fleet node name>"`. The spawn service binds the child's *entire*
  bridge-tool dispatch to that node (run-level node affinity, replacing per-call path routing for
  the child), so its files, bash, and tests all execute there.
- The existing cross-node approval gate fires **once at spawn** ("agent requests a sub-agent on
  NODE-2"), not per tool call — reusing the `ApproveCrossNodeCalls` UX.
- Precondition: the target repo path must resolve on that node (check via `project_info` there;
  clear error otherwise). `fleet_status` already tells the agent what each machine can host, so
  placement reasoning ("run the test suite on the desktop") is prompt-level, no new planner.
- Phase 2 combines with Phase 1: worktree isolation on the *target* node.
- Explicitly deferred (v2+): cross-node repo sync (bundle/fetch between bridges), Hive-drone
  placement, placing vigils by capability instead of pinning by hand.

## Implementation steps

1. `git_worktree` builtin (add/list/remove) + tests (scope enforcement, repo detection, removal
   safety).
2. Spawn plumbing: `isolation`/`base_ref` args → worktree create → run-scoped scope extension →
   `agent_result` metadata (worktree, branch, diffStat).
3. `node` arg → run-level node affinity in `SubAgentSpawnService`/`AgentBackgroundExecutor` +
   single spawn-time cross-node approval; precondition error path.
4. UI: child-run chip shows `worktree`/`node` badges; timeline event for cross-node spawn.
5. Tests in `Aria.Tests`: two isolated children editing the same file converge via merge;
   non-git directory + `worktree` → clean error; cross-node spawn denied → child not started.
6. Docs: README sub-agents + fleet sections; multi-node.md routing rules paragraph.

## Open questions

- Should children inherit `/scope` expansions of the parent session (today: session grant yes,
  path grants? verify) — decide and document.
- Budget sharing: parent's tool budget vs per-child budgets (children have fresh counters today) —
  a global per-session cap may be needed once children do real parallel work.
- Depth stays 1 — with worktrees, nested parallelism stays banned; revisit only with evidence.
