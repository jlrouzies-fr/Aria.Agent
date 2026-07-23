# // IDEA — Coding-agent capability plan (Claude Code / Kimi Code parity without dropping the safeties)

**Status: implemented (2026-07-22).** All five waves landed; suite green at 488 passed / 4 skipped
(from a 319 baseline). Review of README + `docs/readme/*` + `docs/security/*` against the actual
enforcement code in `src/AriaAgent/`. Conclusion: Aria's limits as a coding agent were
**capability gaps, not the security layers**. The Server↔Bridge trust model (fail-closed path
allowlist, node-signed grants, tunnel allowlist, "the node decides; requests never grant") is the
correct answer to the A1 (compromised server) threat and survived every change below.
Everything was added *inside* that model.

## Current state

### The security layers (keep all of them)

- **Fail-closed path allowlist** — `Aria.Bridge/Security/SecurityPolicy.cs:41-62`: empty node
  allowed-paths = every path blocked; the server may only narrow, never widen
  (`SecurityPolicy.cs:46-50`). This is the core guarantee against a compromised server.
- **Node-authoritative capability toggles** — three independent off-by-default flags, not the
  single "TerminalEnabled" the docs describe: `ProjectsEnabled` (agent file/git/`bash_exec`
  inside declared projects), `QuickExecEnabled`, PTY seal (`BridgeDbContext.cs:347-359`).
- **Layer B context grants** — sensitive requests (`/terminal/exec`, mutating `/tools/call`,
  `/project-files`, `/project-git`, `/llm/proxy`, all MCP) need a live 8h node-signed session
  grant (`RequestClassifier.cs:31-49`, `ContextEndpoints.cs:18`); read-only builtins
  (`read_file`/`list_dir`/`glob`/`commands_index`) are Benign (`RequestClassifier.cs:46-49`).
  Enforcement defaults ON (`ContextGrantStore.cs:24-28`).
- **Governance modes** — per-turn budgets in `Aria.Harness/Governance/GovernancePolicy.cs:32-39`:
  Balanced 30 tool-calls/18 reads (default), Strict 12/6, Paranoid 8/4; scope-lock to project
  paths + `#`-refs (`ToolClassifier.cs:90-105`); synthetic refusals let the model self-correct.
- **Inquisitorial Seal** — single-use, capability-bound, ECDSA node-signed, 5-min pending TTL
  (`SealEndpoints.cs:38`). Hardening findings F-1–F-14 all fixed; `docs/security/`
  phase 2 context grants landed (one checkbox blocked by a pre-existing unrelated
  `Souls.ProjectsEnabled` schema-drift test failure — fix and close).

### What actually limits it as a coding agent

1. **No grep/content-search tool.** Searching code requires `bash_exec grep` — classified
   *Sensitive* under Layer B — while whole-file `read_file` is Benign. The safest action needs
   the strongest grant, and the fallback burns the 18-read/turn budget. Biggest single gap.
2. **No agent git tools.** `GitEndpoints` (diff/status/log/stage/commit/discard,
   `Aria.Bridge/Endpoints/GitEndpoints.cs:45-54`) serves only the Explorer UI; the agent must
   `bash_exec git …` (Sensitive).
3. **Headless agents can't code.** `AgentBackgroundExecutor.cs:25` (`NoBridgeTools`) strips
   terminal/file tools from every vigil and Hive drone. There is also no spawn-subagent tool —
   the main agent cannot delegate.
4. **No persistent cwd for agent `bash_exec`** (each call is a fresh process; Quick Exec has
   `_sessionCwd` at `TerminalEndpoints.cs:19`, the agent doesn't).
5. **No auto-compaction** — manual `/compact` only (`Chat.Messaging.razor.cs:690-758`); long
   refactors overflow silently. No plan mode.
6. **Governance budgets are anti-coding by design** — Strict/Paranoid caps die in one real
   refactor turn; Balanced is the only survivable governed mode.
7. **Honest-limit gap:** `bash_exec` only enforces paths on an explicitly-passed `working_dir`
   (`BuiltinTools.Shell.cs:40-44`) — the shell can `cat` anywhere the OS user can. The real
   filesystem boundary exists only for the file tools.
8. **Diff/undo blind spot** — files over 512KB mutate without diff/undo (`DiffTools.cs:13`).

## Design

### Track A — capability inside the existing gates (zero security relaxation)

1. **`grep` builtin** (bridge, read-only, Benign classification). Wrap a search over
   `EnforcePath`-validated roots with result caps (like `glob`'s 500-cap). Classify Benign in
   `RequestClassifier.cs:46-49` alongside `read_file`. Pure win: more capable *and* safer than
   shell-grep.
2. **Agent git tools** from the existing endpoint set: `git_status`/`git_diff`/`git_log` Benign;
   `git_stage`/`git_commit`/`git_discard` Sensitive + mutating under governance
   (`ToolCategories.cs:10-27`). Same `NodeTerminalPolicy.ResolveAsync` path checks.
3. **Persistent agent cwd** — session-scoped cwd for `bash_exec`, copying the Quick Exec
   `_sessionCwd` interception pattern (`TerminalEndpoints.cs:220-247`).
4. **Auto-compaction** — token-threshold trigger that runs the existing `/compact` summarisation
   path automatically before overflow.
5. **Plan mode as a governance preset**, not a new system: read-only scope + mutations blocked +
   "present plan" exit. Reuses per-turn scope-lock and synthetic refusals — configuration, not
   new trust.
6. **`spawn_agent` tool + opt-in terminal access for headless runs** via the *existing*
   pre-authorisation pattern: scoped, time-boxed, auto-revoked grants (`hive:{id}` 8h /
   `vigil:{id}` 2h, `ContextApprovalService.cs:30,35`). Extend that pattern per-run with node
   approval; do **not** route around `NoBridgeTools`.

### Track B — tune restrictions, don't remove them

- **Configurable governance budgets** — per-session/per-project overrides of the
  `GovernancePolicy` constants instead of hardcoded values; add a "Coding" preset
  (e.g. 60 calls / 40 reads, scope-lock = Approve). Enforcement mechanism stays identical.
- **Ergonomic scope widening** — multi-root project groups, and an in-chat "add this path to
  scope" flow that requires *node-side approval* (a signed scope expansion). Widening becomes
  one consent ceremony instead of a trip to `localhost:5741` or a disabled check. The allowlist
  stays fail-closed forever.
- **Fix the asymmetric classification** — read-only inspection is over-gated relative to file
  reads. Track A items 1–2 cover the legitimate cases; no blanket bash relaxation.

### Track C — honest-limit fixes

- `bash_exec` cwd: default the spawn cwd to the project root (and document that paths outside
  the allowlist remain reachable via shell builtins, or confine harder). Update `security.md` §4.
- 512KB diff cap: either raise, or refuse mutation without undo above the cap instead of
  silently mutating (`DiffTools.cs:13`).
- Fix the pre-existing `Souls.ProjectsEnabled` schema-drift test failure blocking the phase-2
  close-out (`docs/security/phase2-context-grants-remaining.md:162-163`).

### Never relax (load-bearing against A1)

- Empty-node-paths = block everything; request may only narrow (`SecurityPolicy.cs:46-50`).
- Node-signed grants/seals; tunnel allowlist (`TunnelAllowlist.cs:13-38`); PTY seal ceremony.
- The 8h Layer B session grant is the right anti-prompt-fatigue tradeoff — keep it. Claude
  Code's own model is per-action prompts in a local process; this is architecturally stronger.

## Doc/README accuracy fixes (independent of the above)

- **README conflates seal and grant** (README "Inquisitorial Seal" paragraph): claims the seal
  "is valid for 8 hours, binds to the current browser session" — that's the *context grant*
  (8h, `ContextEndpoints.cs:18`). The seal is single-use, capability-bound, 5-min pending TTL
  (`SealEndpoints.cs:38`). Reality is better than the docs say.
- **Toggle drift** — README/security.md describe one master `TerminalEnabled`; code has
  `ProjectsEnabled` / `QuickExecEnabled` / PTY (`BridgeDbContext.cs:347-359`).
  `ProjectsEnabled` is precisely the coding-agent knob and is undocumented.
- **Vigils oversold** — README says a vigil "runs the prompt against the chosen sub-agent";
  headless runs have no file/shell tools, so "wake up and fix the build" silently can't. One
  honest line until Track A item 6 lands.

## Implementation outcome (as landed)

1. ✅ **`grep` builtin** (`BuiltinTools.Grep.cs`) — managed regex/substring search, 200-match/20-per-file
   caps, binary + `.git`/`node_modules`/`bin`/`obj` skip (overridable), Benign-classified, counts as
   FileReads. Resolved the open question in favour of managed (no `rg` dependency).
2. ✅ **Agent git builtins** (`BuiltinTools.Git.cs`) — `git_status`/`git_diff`/`git_log` Benign;
   `git_stage`/`git_commit` mutating; `git_discard` mutating + HighStakes, explicit-paths-only
   (whole-repo targets rejected). Reuses `GitEndpoints.RunGitAsync`.
3. ✅ **`bash_exec` cwd** — default cwd = first allowed project root; persistent session cwd with
   `cd` interception validated by `EnforcePath` (explicit `working_dir` always wins).
4. ✅ **Diff-cap honesty** — above 512KB the mutation result warns "no diff preview" (undo via
   revert still works — the `FileUndo` row is always written).
5. ✅ **Governance presets** — `Coding` (60 calls/40 reads, scope Approve, loop 4) and `Plan`
   (40/40, mutations blocked with a present-the-plan refusal), plus `/governance` chat command
   with per-session `budget tools=<n> reads=<n>` overrides. Default stays Balanced.
6. ✅ **Auto-compaction** — `AutoCompaction.ShouldCompact` (Harness-side, host-agnostic): real
   prompt-token count when reported, chars/4 fallback; fires between turns via the existing
   `/compact` flow; `/compact auto [<tokens>|off]`, default 100k (no context-window metadata
   exists to do 80%-of-window).
7. ✅ **`spawn_agent` / `agent_result`** (`SpawnAgentTools.cs` + `SubAgentSpawnService`) —
   background child runs of existing personas; inherit parent session grant + governance mode;
   one level deep (children never get a spawner); 4 concurrent/session; `wait_seconds` ≤ 120.
8. ✅ **Scoped terminal opt-in for vigils/Hive** — `AllowProjectTools` flags (default off) on
   `AgentCronJob`/`AgentCollective`; opted-in runs keep bridge tools under the existing
   time-boxed `vigil:{id}`/`hive:{id}` grants; node-side gates unchanged.
9. ✅ **Node-approved scope expansion** — `/scope [add|remove] <path>`; path grants are
   node-signed `GrantType="path"` rows (`path:{soul}|{sessionId}|{fullPath}`, 8h), unioned into
   enforcement session-side only; request-may-only-narrow untouched; sibling-replicated.
10. ✅ **Doc fixes** — seal-vs-grant conflation corrected (seal = single-use, 5-min pending TTL;
    grant = 8h session), three-toggle drift documented (`ProjectsEnabled`/`QuickExecEnabled`/PTY),
    governance/vigils/sub-agent README sections updated, phase-2 closeout checkbox checked
    (schema-drift test was already fixed in `VaultEncryptionTests.cs`).

## Follow-ups (closed 2026-07-22)

- ✅ **Builtin-tools policy path gap** — closed, and it was a real hole: `/tools/call` previously
  enforced the server-supplied policy directly (null policy = unrestricted; the server could
  widen builtins scope with its own `AllowedPaths`). Builtins now resolve through
  `NodeTerminalPolicy.ResolveBuiltinPolicyAsync` — the same node-authoritative seam as the
  project-file/git endpoints: node declared paths (empty when `ProjectsEnabled` off →
  fail-closed) ∪ node-signed session grants ∩ request narrowing. One merge, no drift.
- ✅ **Revocation replication** — tombstones ride the existing grant export/import channel as
  signed `GrantType="revoke"` entries (distinct canonical prefix; old siblings fail closed).
  Covers path grants, context grants, and hive one-shot seals. Out-of-order tombstones win;
  a later-expiry re-approval counts as a fresh human decision. Known limit: re-approving the
  *same* context id with expiry ≤ the tombstoned one won't replicate to siblings (deliberate,
  tamper-proofing).
- ✅ **EF Core alignment** — Bridge/Web/Tests all on EF Core **10.0.8**; the two load-and-save
  workarounds reverted to `ExecuteUpdateAsync`; zero 9→10 breakage.
- ✅ `docs/commands-and-references-plan.md` — status note + palette table synced with
  `ChatCatalog.cs` (`/governance`, `/scope`, `/compact auto`).

## Remaining ideas

- Hive Servitor edge node (separate plan, `hive-servitor-edge-node.md`) pairs naturally with the
  `AllowProjectTools` collective flag now available.

## Tool expansion (2026-07-22, second pass)

- ✅ **`install_software`** — allowlisted managers (brew/npm/pip/pipx/dotnet/cargo/go),
  injection-safe argv (no shell anywhere, strict charset validation), pip always `--user`,
  rendered command still passes `EnforceCommand`. New `RequiresApproval` governance category:
  asks in Balanced/Coding/Strict, blocked in Plan, seal in Paranoid, free only in Off.
  Motivation: installs write outside any Allowed Path, so the path allowlist can never cover
  them — approval-gating is the honest gate.
- ✅ **`system_info`** — Benign environment recon (OS/arch, runtimes, managers on PATH, disk).
- ✅ **`process_list`/`process_output`/`process_kill`** — registry-tracked management of
  `bash_exec background:true` jobs (kill refuses unregistered pids; POSIX exit-code sidecar).
- ✅ **`multi_edit`** — atomic multi-hunk edits in one call (one undo entry per batch).
- ✅ **`undo_file`** — agent-side revert over the `FileUndo` store (stack semantics, undoable).
- Known limits: process registry is in-memory (bridge restart loses job tracking); Windows
  background jobs report no exit code (liveness only).

## Tool expansion (2026-07-22, third pass — agent self-management)

- ✅ **`ask_user`** (server-side) — structured mid-run questions (≤4 options + free-text, 2h
  timeout, skip/timeout → "proceed with your best judgment", never fails the run). Pause/resume
  rides the approval-bar machinery end-to-end (sink → `CogitationRun.PendingAskUser` TCS → ask
  bar in chat). Allowed in Plan mode (it's how the agent presents choices).
- ✅ **`context_status`** (server-side) — reported/estimated tokens vs. the effective
  auto-compact threshold (% headroom), invariant-culture output. Benign everywhere.
- ✅ **`http_request`** (bridge) — full verbs/headers/body, raw status + response, no
  auto-redirect, ≤60s. Sensitive (runs on the node: localhost/LAN reach, exfiltration path).
  Registered per-toolId like the memory tools, not via the bridge manifest (a manifest copy
  would race the vision/wiring dedup).
- ✅ **`read_image`** (bridge) — EnforcePath'd, magic-byte sniffed (png/jpeg/gif/webp), ≤10MB,
  delivered via TakeScreenshot's exact multimodal path (`ToolCallResponse` base64 →
  `MultimodalToolResult` → `DataContent` only when the vision probe passes). Benign/FileReads.
- Test hardening: `BuiltinHttpRequestTests` serialized (`[Collection]`) + non-fatal listener
  cleanup — `HttpListener`'s endpoint manager races ephemeral-port binds under full-suite
  parallelism (5 consecutive green runs after the fix).

## Tool expansion (2026-07-22, fourth pass — long-running processes / web servers)

- ✅ **`bash_exec` timeout conversion** — when a foreground command hits `timeout_seconds`, it is
  no longer killed. The running process is moved into the background-job registry, stdout/stderr
  keeps draining to `.aria-bg/`, an exit-code sidecar is written on exit, and the tool returns
  `converted_to_background: true` + `pid` + partial output. Healthy dev servers survive; the agent
  gets guidance to call `process_output`/`process_kill`/`wait_for`. On Windows the registered pid
  is `cmd.exe`, so cleanup works; on POSIX the registered pid is the shell wrapper, so SIGTERM may
  miss grandchildren (same honest limit as existing `background:true`).
- ✅ **`run_background`** — first-class "start a long-running process" tool. Models reliably use a
  named tool. Shares the existing background launch path; gated by `ProjectsEnabled` and
  `EnforceCommand`; Mutating + HighStakes like `bash_exec`.
- ✅ **`wait_for`** — readiness probe: TCP port, URL response, or background-job log pattern.
  Default 30s / max 120s; 500ms poll interval; probe-level timeouts prevent stalls. Benign/FileReads.
- Test hardening: serialized `BuiltinProcessTests` and `BuiltinWaitForTests` via a shared
  `[Collection("BuiltinBackgroundJobs")]` because both mutate the static `BackgroundJobs`
  registry and reset it; `BuiltinHttpRequestTests` already had its own collection.
- Also fixed a latent flake in `RunAsync` `ExitCode` reads: fast-exiting processes on macOS could
  throw "No process is associated with this object" after `WaitForExitAsync`; caught and defaulted
  to -1, mirroring the existing pattern.
