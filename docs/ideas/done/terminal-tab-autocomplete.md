# // IDEA — Terminal Tab autocomplete (shell-style completion against the node)

**Status: implemented.** Follow-up to [chat-shared-terminal.md](chat-shared-terminal.md) (shipped through
phase 1). Make Tab in the terminal prompt behave like a real shell: complete command names on the
first token, files/directories elsewhere, longest-common-prefix insertion, and a candidate list on
ambiguity. Candidates come from the **cogitator node** — the filesystem the commands actually run
against — via a new bridge endpoint.

## Current state

- Tab is already intercepted: `ariaInterop.terminalInput.init` (aria-interop.js) binds keydown on
  `#terminalInput`, `preventDefault()`s Tab and inserts a literal `\t`. That handler is the natural
  hook point — replace its body, keep the binding.
- The prompt input is a `DebouncedInput` — **the DOM owns the text** (no `value` attribute; .NET is
  only notified after a debounce). Consequence: a Tab handler must read `element.value` +
  `selectionStart` from the DOM at keypress time, not from `_terminalInput` (which can be ~150 ms
  stale), and must write completions back through `ariaInterop.debouncedInput.setValue` so the JS
  state stays coherent.
- Per-session cwd already tracked: `_terminalCwd` on the web side, `_sessionCwd` on the bridge
  (`TerminalEndpoints.cs`) — completion must resolve relative paths against it.
- `SecurityPolicy` (bridge) has `EnforcePath` for allowed-path checks and the web side already
  passes `SessionState.AllowedProjectPaths` on every exec call — reuse both.
- Node routing (`ResolveTerminalNodeId`) and the tunnel REST path (`TerminalClient` →
  `SendLocalRestAsync`) exist; completion is just another local REST call.

## Design

### Bridge — `POST /terminal/complete`

Request: `{ line, cursor, cwd, sessionId, allowedPaths }`. Response:
`{ replaceStart, replaceEnd, commonPrefix, candidates: [{ text, isDir }], truncated }` where
`replaceStart/End` are offsets of the token being completed in `line`, so the client never
re-tokenizes.

Implementation: **native C#, not `compgen`.** Shelling out to `bash -c "compgen …"` gives real
shell semantics but requires embedding remote input into a shell string — a quoting/injection
hazard for zero real benefit at this fidelity level. Native logic:

1. **Tokenize** the line up to `cursor`: scan backward for the token start, honouring `\ `
   (escaped space), `"…"` and `'…'` quoting. Deliberately minimal — no `$()`, no globs.
2. **Classify** the token:
   - first token, no `/` or `.` prefix → **command completion**: shell builtins (`cd`, `export`,
     `source`, …) + executables enumerated from `PATH` (cached index, ~60 s TTL, built once per
     process on first Tab);
   - first token starting `./`, `~/`, `/` → path completion filtered to executables + dirs;
   - argument after a bare `cd` → **directories only**;
   - anything else → **path completion**: expand `~`, resolve against session cwd,
     `Directory.EnumerateFileSystemEntries(dir, prefix + "*")`, dirs get a trailing `/`.
3. **Policy**: before listing a directory, run `policy.EnforcePath(dir)`; on violation return an
   empty candidate list (silently — a shell shows nothing, it doesn't lecture). Completion is
   remote FS enumeration, so it gets exactly the exec call's trust level, no more.
4. **Caps**: max 200 candidates (`truncated: true` beyond), case-insensitive prefix match on
   macOS/Windows, hidden files only when the prefix starts with `.`.
5. Compute `commonPrefix` (longest common prefix of all candidates, minus what's already typed)
   server-side so one round trip covers the bash-style "extend then list" behaviour.

Minor bridge version bump (new endpoint, backward-compatible).

### Web plumbing

- `TerminalClient.CompleteAsync(userId, line, cursor, cwd, sessionId, allowedPaths, nodeId)` →
  `SendLocalRestAsync("POST", "/terminal/complete", …)` with a short timeout (~5 s). Null result =
  no-op (Tab silently does nothing when the bridge is away — like a dead shell).

### Interaction (bash-style, one round trip per Tab)

1. JS Tab handler: `preventDefault()`, read `element.value` + `selectionStart`, invoke a
   `[JSInvokable]` on the Chat circuit (`OnTerminalTabAsync(text, cursor)`) via a
   `DotNetObjectReference` passed at panel init. Ignore Tab while a request is in flight
   (single-flight; show a subtle busy tick in the prompt if >150 ms).
2. .NET calls the bridge, then:
   - **0 candidates** → nothing (optionally a brief flash on the prompt).
   - **1 candidate** → replace `[replaceStart, replaceEnd)` with it; append `/` for a dir, ` `
     otherwise; write back via a new `ariaInterop.debouncedInput.setValueAndCursor(el, text, pos)`
     (setValue + `setSelectionRange` — the missing primitive today) and re-focus.
   - **many, commonPrefix non-empty** → insert the extended prefix only (classic bash first-Tab).
   - **many, no extension** → render a **transient candidates strip** above the prompt line: a
     wrapped monospace grid, dirs tinted with `--terminal-accent-dim`, `… +N more` when truncated.
     Not part of `_terminalLines` (never scrollback, never agent-visible, gone on next keystroke /
     Esc / command run).
3. Second Tab with the strip open → cycle selection through candidates (zsh `menu-complete`),
   Shift+Tab cycles backward, Enter/→ accepts. This is the only stateful part; keep it a small
   `_completionState` record on the Chat partial (`candidates`, `index`, `replaceStart/End`)
   cleared by the same events that dismiss the strip.

### Latency notes

Every Tab is a browser → circuit → tunnel → node round trip; realistically 30–150 ms on a LAN
bridge — fine for an explicit keypress, and why there is **no** as-you-type completion here.
Two cheap mitigations if it grates: bridge-side PATH index cache (already in the design) and a
per-`(dir)` listing cache on the bridge with a ~3 s TTL so Tab-Tab-Tab cycling costs one listing.

## Implementation steps

1. **Bridge**: `TerminalCompleteRequest/Response` records + tokenizer + completer in
   `TerminalEndpoints.cs` (or a sibling `TerminalCompletion.cs`), PATH index cache, policy check,
   unit-testable pure function `Complete(line, cursor, cwd, policy)`. Version bump (minor).
2. **Web client**: `TerminalClient.CompleteAsync`.
3. **JS**: rework `ariaInterop.terminalInput.init` to take a `DotNetObjectReference`; add
   `debouncedInput.setValueAndCursor`.
4. **Chat.Terminal.razor.cs**: `OnTerminalTabAsync`, `_completionState`, apply/cycle/dismiss logic;
   candidates strip markup in `Chat.razor` + styles in `css/chat/terminal.css`.
5. Manual pass: `cd s<Tab>`, `git ch<Tab>` (no candidates — arg completion is out of scope),
   `dotn<Tab>`, `ls ~/Dev<Tab>`, quoted paths with spaces, blocked path silence.

## Out of scope (deliberately)

- **Argument/flag completion** (`git ch<Tab>` → `checkout`) — that's the shell's programmable
  completion machinery; revisit only with the phase-2 PTY, where the real shell does it for free.
- Glob expansion, `$VAR` completion, command history search (Ctrl+R) — separate ideas.
- As-you-type suggestions (fish-style autosuggest) — per-keystroke tunnel traffic; explicitly not
  now.

## Open questions

- Escaping on insert: complete `My Documents` as `My\ Documents` (bash-like) or quote the whole
  token? Leaning backslash-escape, matching what users type back into the same prompt.
- Should the candidates strip be keyboard-navigable with arrows too, or is Tab-cycling enough?
  Start with Tab-cycling only; arrows currently do nothing in the prompt so they're free to claim
  later (history will want ↑/↓ first).
- Windows nodes: PATH completion needs `PATHEXT` handling and `\` separators — worth doing in
  step 1 or gated to a follow-up? (The exec endpoint already runs on Windows, so probably step 1.)
