# // IDEA — Truly persistent agent shell

**Status: planned.** The README advertises a "persistent bash shell"; the code starts a fresh
`/bin/sh -c` per call and only the cwd survives (intercepted bare `cd`, process-wide
`_sessionCwd`). Environment exports, `source .venv/bin/activate`, shell functions — all gone
between calls, which breaks real dev workflows. Make the shell actually persistent: one long-lived
shell process per agent session on the bridge.

## Current state

- `bash_exec` (`Aria.Bridge/BuiltinTools/BuiltinTools.Shell.cs`): fresh shell per call; only cwd
  persists; timeout auto-converts to a tracked background job; every command inspected by
  `SecurityPolicy` before running.
- Long jobs are covered separately: `run_background`, `wait_for`, `process_list/output/kill`
  (`BuiltinTools.Background.cs`, `.Process.cs`; registry is in-memory).
- A real PTY exists for the *user-facing* interactive terminal — seal-gated, disables the
  quick-exec policy because keystrokes can't be honestly filtered. That trade-off is correct for
  humans and must not leak into the agent path.
- README "Tools & Integrations" claims "persistent bash with background jobs" — currently an
  overclaim (the coding-capability plan's known-limits section acknowledges pieces of this).

## Design

### One shell per agent session

- The bridge keeps a small pool of long-lived shell processes, keyed by agent session/cogitation
  id. First `bash_exec` of a session spawns it lazily; idle reaper kills it after 15 min
  (configurable); bridge shutdown kills all. Spawned sub-agent runs get their own shell keyed by
  run id (children share the parent's session id today — keying by run avoids cross-talk).
- **Plain pipes, not a PTY.** Line-mode stdin/stdout; no echo, no control characters, no TTY
  games. Interactive programs that demand a TTY still route to `run_background` or the (human,
  seal-gated) PTY — documented limitation.
- **Command protocol:** for each call, write `command ; echo __ARIA_END_<nonce>_$?` and drain
  stdout/stderr until the sentinel line. Exit code comes from the sentinel; output is everything
  before it. A nonce per call makes sentinel spoofing by command output detectable.
- **Timeout semantics:** on timeout, send SIGINT to the shell's foreground process group first
  (graceful interrupt, shell survives), drain to a fresh sentinel; if the command ignores SIGINT,
  offer the existing background-conversion path. The shell itself only dies on reaper/shutdown or
  unrecoverable pipe errors (then lazily respawned on next call, with a one-line notice that env
  was reset).
- **Environment persistence is the feature:** `export`, `cd`, `source`, `venv`, `nvm use`,
  shell functions — all survive between calls because the process survives.
- **Security unchanged:** every command is still inspected by `SecurityPolicy` before it touches
  stdin (the agent shell is NOT the PTY; keystroke filtering stays meaningful because commands
  arrive whole). cwd/Allowed-Paths enforcement works as today; the persistent `cd` state replaces
  `_sessionCwd` bookkeeping (keep them in sync or drop the latter).
- `process_list` shows the agent shell as `agent-shell (session …)`; `process_kill` can end it
  (next call respawns, env reset notice).
- **Windows:** same design over `pwsh`/`powershell` (fallback `cmd`) with an equivalent sentinel
  pattern; kept behind the same abstraction so POSIX and Windows share the session manager.
- Config: `AgentTools:PersistentShell` (default on once baked; flag off restores today's
  fresh-shell behaviour for rollback).

## Implementation steps

1. `AgentShellSession` manager on the bridge: spawn, sentinel write/drain, SIGINT-on-timeout,
   idle reaper, respawn-on-death, session/run keying.
2. Rewire `bash_exec` through it (flag-guarded); keep background conversion and SecurityPolicy
   untouched.
3. Windows shell selection + sentinel variant.
4. Tests in `Aria.Tests`: env persistence (`export X=1` then `echo $X`), venv-style `cd`+`source`
   simulation, SIGINT-on-timeout keeps the shell alive, concurrent sessions isolated, dead-shell
   respawn notice.
5. Docs: README bullet becomes true (adjust wording to per-session shell), troubleshooting note
   about env resets after idle reap, architecture.md terminal-limits paragraph.

## Open questions

- Expose `shell_reset` (deliberate env wipe) as a tool? Cheap; useful when the agent poisons its
  own env. Probably yes in the same PR.
- Should vigils/Hive drones (headless runs) get persistent shells? They run one-shot; lazy spawn
  handles it naturally — no special casing.
- Merge `_sessionCwd` into the shell's real cwd tracking — verify no other feature reads
  `_sessionCwd` before removing it.
