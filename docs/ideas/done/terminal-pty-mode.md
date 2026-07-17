# // IDEA — Terminal PTY mode (dual-mode: quick exec + full shell)

**Status: implemented; server-side validated (browser render pending manual test).** Bridge
`v0.20.0-beta`. Phase 2 of [chat-shared-terminal.md](chat-shared-terminal.md), reshaped as a
**second mode** rather than a replacement. The panel keeps two personalities:

| | QUICK EXEC (default) | PTY |
|---|---|---|
| Interactive programs (vim, claude, top, ssh) | ✗ — no TTY, no live stdin | ✓ — real shell on a pseudo-terminal |
| Security | Blocklist + allowed-path policy enforced per command | **None beyond tunnel auth** — raw keystrokes to a live shell; command filtering is not honestly enforceable |
| Tab autocomplete | Bridge `/terminal/complete` (shipped) | The real zsh/bash does it, including args/flags |
| cwd tracking | Emulated (`cd` interception) | Native |
| Enable gate | **Requires bridge-side Terminal Capability** (0.25.0+) | **Requires Terminal Capability + a node-local Seal approval** |

Mode switch lives in the terminal header (`QUICK EXEC ▾ / PTY`). Everything else about the panel —
chrome, resize, node/project picker, localStorage state, accent theming, agent-visibility toggle,
`[→ CHAT]` quoting — is shared between modes.

## Why both modes

- Quick exec is the safe default and the only mode where the `SecurityPolicy` story is real: each
  command is a string the bridge can inspect before running. It stays useful for one-shot commands
  on a node you're being careful with.
- PTY is "you have a shell." Filtering keystrokes to a live zsh is security theater (line editing,
  history expansion, aliases, `$()` all route around any inspection at Enter). So don't pretend:
  PTY mode disables the blocklist and allowed-paths entirely and *says so* in the UI —
  `⚠ PTY MODE: full shell, node policy not enforced`.

## The enable gate — Seal, not sudo

Requiring `sudo` on the bridge was the first instinct, but sudo proves *OS admin rights*, which is
the wrong property (a PTY doesn't need root; the risk is remote-shell exposure) and is painful
cross-platform (a daemon can't prompt sudo; macOS would need an osascript admin dialog, Windows a
UAC helper exe). What we actually want to prove is **a human at the node consented** — and the
bridge already has that primitive: the **Seal** (`SealEndpoints.cs`): server posts a request, the
node opens a localhost-only approval page in the local browser, the human clicks, the node signs a
nonce with the soul key. Localhost-only page = local presence; soul signature = verifiable by the
server; works identically on macOS/Windows/Linux with zero new machinery.

Flow:

1. User flips the mode switch to PTY in the web panel.
2. Server → bridge `POST /seal/request` with `ToolName: "terminal_pty"`, reason text explaining
   exactly what is being enabled ("full interactive shell over the tunnel; node blocklist and
   allowed paths will NOT apply").
3. Human approves on the node → bridge persists `PtyEnabled = true` in the local vault (SQLite),
   signed verdict returned to the server as usual.
4. From then on PTY sessions open without re-approval. A **revoke** control on the bridge status
   page (`localhost:5741`) flips it back off — revocation is node-local only, the server can't
   re-enable without a fresh seal.

Open sub-question: should the grant expire (e.g. 30 days) or persist until revoked? Lean persist —
the seal ceremony is deliberate enough, and expiry would train users to click through it.

## PTY implementation

1. **Pseudo-terminal on the bridge**: `Pty.Net` (the VS Code terminal host library) — `forkpty` on
   macOS/Linux, ConPTY on Windows. One shell process per terminal session (user's login shell:
   `$SHELL` / `%COMSPEC%`-or-pwsh), managed like MCP processes in `SessionStore.cs`: 10-min idle
   eviction, kill on tunnel drop, explicit `exit` closes the session.
2. **Streaming over the existing tunnel**, both directions — new hub methods:
   - bridge → server: `TerminalChunk(sessionId, bytesBase64)`, `TerminalClosed(sessionId, exitCode)`
     (same pattern as `SendChunk`/`CompleteRequest` for LLM SSE);
   - server → bridge: `TerminalInput(sessionId, bytesBase64)`, `TerminalResize(sessionId, cols, rows)`.
3. **Renderer**: self-hosted **xterm.js** in the panel (single bundled JS+CSS in `wwwroot/lib/`,
   no CDN). Do not hand-roll an ANSI/VT renderer. Keystrokes go from xterm's `onData` straight
   into `TerminalInput` — the `DebouncedInput` prompt line is not rendered in PTY mode.
4. **Agent visibility unchanged in contract**: `BuildTerminalContextForAgent` sources lines from
   xterm's buffer serialization (plain-text, last N lines) instead of `_terminalLines`. The toggle,
   caps, and per-turn injection logic are shared. Same for `[→ CHAT]` block quoting (select-based
   in PTY mode, since "command blocks" don't exist in a raw stream).

## What survives from phase 1 / what retires

- **Survives**: panel chrome + state, node routing, tunnel infra, agent-visibility contract,
  quoting, and the whole quick-exec path (`/terminal/exec`, `/terminal/complete`, blocklist,
  cwd emulation) — as the default mode.
- **Retires in PTY mode only**: the prompt-line `DebouncedInput`, the line-list scrollback,
  bridge-side completion, `cd` interception. None of it is deleted; it's the other mode.
- **New**: Pty.Net dependency, xterm.js asset, `SessionStore` PTY entries, 4 hub methods,
  seal-gated `PtyEnabled` vault flag, mode switch UI.

## Quick-exec niceties that fall out of this

- **Fail helpfully now** (independent of PTY work, ~10 lines): known-interactive first tokens
  (`vim`, `nano`, `less`, `top`, `htop`, `claude`, `ssh` with no command, …) short-circuit in
  quick exec with `// interactive program — needs PTY mode` (or, pre-PTY, a hint like
  `try: claude -p "…"`).
- Optionally run quick-exec commands under `script -q /dev/null` on macOS/Linux so TTY-*checking*
  programs (colors, progress bars) render properly — doesn't help truly interactive ones, but
  upgrades the "works but ugly" class for free.

## Implementation steps

1. Fail-helpfully guard in `/terminal/exec` (ship immediately; bridge fix bump).
2. Seal gate: `terminal_pty` seal reason + `PtyEnabled` vault flag + revoke on status page
   (bridge minor bump).
3. PTY sessions: Pty.Net + `SessionStore` lifecycle + hub methods + server-side routing in
   `ModelBridgeRegistry` (bridge major-ish minor bump; protocol addition is backward-compatible).
4. Web: xterm.js panel mode, mode switch, warning banner, agent-visibility re-sourcing.
5. README security note: PTY mode = remote shell gated by soul-verified tunnel + node-local seal;
   quick exec remains the policy-enforced mode.

## Open questions

- One PTY session per panel, or allow multiple tabs per node? Start with one.
- Scrollback persistence across page refresh in PTY mode — xterm buffer lives in the browser;
  reconnect to a live session should replay recent output (bridge keeps a small ring buffer,
  e.g. 256 KB, per session).
- Seal grant lifetime: persist-until-revoked (leaning yes) vs expiring.
- Windows shell choice: pwsh if present, else PowerShell 5, else cmd — or make it a node setting.
