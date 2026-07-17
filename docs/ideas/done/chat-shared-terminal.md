# // IDEA — Shared terminal panel (user-driven shell on the node, agent can read it)

**Status: phase 1 shipped** (panel, `/terminal/exec`, cwd tracking, agent visibility, `[→ CHAT]`
quoting). Tab autocomplete shipped separately: [terminal-tab-autocomplete.md](terminal-tab-autocomplete.md).
Phase 2 (PTY) redesigned as a dual-mode plan: [terminal-pty-mode.md](terminal-pty-mode.md). A collapsible bottom panel in the chat: a terminal running commands **on the
cogitator node** (the same machine the agent's `bash_exec` targets). Opt-in toggle lets the agent
see the scrollback, so "look at the failing test above" just works. Phase 1 is command/response;
phase 2 is a true persistent PTY.

> **0.25.0+ update:** both the panel's Quick Exec and PTY modes now require the **Terminal Capability**
> toggle to be enabled on the bridge first (`http://localhost:5741`, Telemetry tab). Enabling the
> Terminal tool in the web UI is no longer sufficient. PTY still needs its own Seal grant on top of
> the master toggle.

## Current state

- The bridge already executes shell commands: `bash_exec` in `BuiltinTools.Shell.cs`, dispatched
  through `BuiltinTools.cs` under a `SecurityPolicy`, reachable from the server via
  `SendLocalRestAsync("POST", "/tools/call", …)`.
- The tunnel already streams chunked data server-ward (LLM SSE via `SendChunk`/`CompleteRequest`
  in `DirectTunnel.cs` / `ModelBridgeRegistry.Routing.cs`) — the pattern a live PTY needs exists.
- Nothing user-facing: the user can only run commands by asking the agent to.

## Design

### Phase 1 — command/response panel

- UI: bottom panel in `Chat.razor` (toggle in the input bar, `▤ TERMINAL`), monospace scrollback +
  prompt line. Height draggable; state (open/height/cwd) in localStorage like the explorer.
- Execution: prompt input → `SendLocalRestAsync` → **a dedicated endpoint** `POST /terminal/exec`
  `{ command, cwd, sessionId }` rather than reusing `/tools/call`, because semantics differ:
  - runs under the same `SecurityPolicy` blocklist as `bash_exec` (it's still remote input arriving
    over the tunnel — user-initiated, but the node shouldn't trust the server more for it);
  - **no agent governance** — governance wraps *agent* tools; this is the human;
  - per-`sessionId` working directory persistence (`cd` updates it server-side of the shell call,
    reported back in the response) so the panel feels like a shell even before a real PTY;
  - output capped (~200 KB) + wall-clock timeout (default 120 s) with a clear `⏱ TIMED OUT` marker.
- Scrollback lives in circuit memory per cogitation + a rolling cap (~2,000 lines). Not persisted
  to the DB in phase 1.
- Node routing: a node picker in the panel header when the soul has multiple nodes (default: the
  active project's `NodeId`, else primary) — path-routed terminal tools already set the precedent.

### Agent visibility (the actual point)

- Toggle in the panel header: `AGENT SEES TERMINAL: ON/OFF` (default OFF).
- When ON, the harness appends a system-message block to the next turn: the last N lines
  (default 80, capped by chars) of scrollback, labelled
  `◈ VOX-TERMINAL (user's shell on node <name>, most recent last):`.
- Injected fresh each turn while ON — never stored into cogitation history, so it can't bloat
  long chats (same spirit as SynapseMemory injection in the Hive).
- Also the reverse: a small `[→ CHAT]` button per command block quotes that command + output into
  the message input, for pointing the agent at one specific failure.

### Phase 2 — persistent PTY

- Bridge keeps a real shell per terminal session (spawn `zsh`/`bash` with a pty wrapper), managed
  like MCP processes in `SessionStore.cs` (10-min idle eviction, kill on tunnel drop).
- Streamed over the tunnel with the `SendChunk`-style pattern (new hub methods
  `TerminalChunk`/`TerminalClosed`); interactive programs, colours (xterm-ish subset → HTML), Ctrl+C.
- The agent-visibility contract is unchanged — it reads rendered scrollback, not the pty stream.

## Implementation steps

1. Bridge: `/terminal/exec` with policy check, cwd tracking, caps (minor version bump).
2. Web: panel UI + scrollback + node picker + localStorage state.
3. Agent visibility toggle + per-turn injection in the session prompt assembly + `[→ CHAT]` quote.
4. (Phase 2) PTY sessions in `SessionStore`, streaming hub methods, ANSI rendering.

## Open questions

- Should scrollback persist across page refresh? Phase 1: no (matches "terminal" mental model);
  reconsider if it grates.
- Injection size vs. context budget: 80 lines is a guess — make it a setting next to the toggle.
- Security note for the README when this ships: the panel is remote command execution on the node,
  gated by soul-verified tunnel auth + the node's own blocklist. Worth stating plainly.
