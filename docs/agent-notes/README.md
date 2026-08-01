# // AGENT NOTES — operational lessons for future coding agents

This folder is a scratchpad of things a coding agent working on this repo had to learn the hard
way: local-dev-environment quirks, sharp edges in the codebase that look correct but aren't, and
process/tooling gotchas that cost real time to diagnose. It is **not** end-user documentation
(that's `docs/readme/`) and not a bug tracker (that's `docs/bugs/`) — it's context so the next
agent (or human) doesn't have to re-derive the same debugging session.

Add a new file here whenever you burn significant time on something non-obvious that will
recur — a flaky local setup, a footgun in a shared helper, a "the logs say X but the real cause is
Y" trail. Keep entries short, dated implicitly by git blame, and link to the exact files/lines
involved.

- [local-dev-environment.md](local-dev-environment.md) — running `Aria.Bridge` + `Aria.Web`
  locally side by side: process backgrounding gotchas, the stale soul-link failure mode, local LM
  Studio quirks, and why `Aria.Console` is not a useful tool for exercising real chat sessions.
- [db-migration-ordering.md](db-migration-ordering.md) — the `BridgeDatabaseInitializer` footgun
  where `CREATE TABLE IF NOT EXISTS` silently no-ops on pre-existing databases, and how that broke
  a real local bridge database when a new column's index was created too early.
