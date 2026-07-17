# Chat `/` Commands & `#` References — Design Plan

> **Goal:** bring Aria's chat input to parity with modern agentic CLIs (Claude Code,
> Kimi CLI): a rich `/` command palette and typed `#` context references — plus a
> `/hive` flow that routes a message to a Hive collective and waits for its output.
>
> This doc is the implementation reference. It cites real symbols/paths so work can
> start without re-deriving the architecture.

Supersedes [`referenceProjectFiles.md`](./referenceProjectFiles.md) — that original
`#` file-picker idea is now **implemented** (`Aria.Bridge/Endpoints/ProjectFileEndpoints.cs`
+ `Chat.FilePicker.razor.cs`). This doc extends it to a full catalog.

> **Status update (2026-07-06):** `ChatCatalog.cs`
> (`src/AriaAgent/Aria.Web/Services/Chat/ChatCatalog.cs`) is now the single source of
> truth for every `/` command and `#` reference, tagging each `Available` or `Planned`.
> Phase 1 of the rollout below (§4) has shipped — see **"Implemented so far"** just
> below the current-state table for exactly what landed and where. The per-item
> tables further down still describe the target design; treat their `Effort`/phase
> labels as historical, and check `ChatCatalog.cs` for live status.

## Current state

| Surface | Today | Backing |
|---|---|---|
| `/` palette | `/clear`, `/compact`, `/project`, `/tools`, `/mcp`, `/agents`, `/skills`, `/soul`, `/devices`, `/hive`, `/vigil`, `/vox`, `/wargame`, `/help`, `/index` | `ChatCatalog.Commands`, dispatched in `Chat.FilePicker.razor.cs` |
| `#` reference | `#<path>`, `#folder:<dir>`/`#dir:`, `#git:diff`/`#git:status`/`#git:log` | `ChatCatalog.References` → `ProjectFileEndpoints` / `GitEndpoints` |
| agent awareness | agent can call `list_chat_capabilities` to see the `Available` list above on demand | `ChatCapabilitiesTools.cs` (Aria.Tools) + `ChatCatalog.BuildAgentCapabilitiesText()` |

The two-stage picker pattern already exists — `OpenCommandPalette` / `OpenProjectPicker`
/ `OpenFilePickerAsync` in `Chat.FilePicker.razor.cs`. Everything below mirrors it.

### Implemented so far

- **Agent awareness tool** (not originally in this doc, but the gap it fixes — the
  agent had no way to answer "how do I do X" about the chat UI — was raised alongside
  this rollout). New always-on, Web-only tool `list_chat_capabilities`
  (`Aria.Tools/ChatCapabilitiesTools.cs`), registered in `Harness.cs` whenever
  `HarnessOptions.ChatCapabilitiesText` is set (`AgentService.CreateSessionAsync` sets
  it from `ChatCatalog.BuildAgentCapabilitiesText()`). Mirrors the `TodoTools` "always-
  on, in-process, host-supplied" pattern — absent from Console, since it's Web-only.
- **`#folder:<dir>` / `#dir:<dir>`** — resolves via the existing
  `ProjectFilesClient.ListTreeAsync` → `/project-files/tree` (no new Bridge endpoint
  needed), formatted as an indented tree in `BuildReferenceNote`
  (`Chat.FilePicker.razor.cs`). No live fuzzy picker — the user types the path
  directly, matching the doc's original "Low effort" call.
- **`#git:diff` / `#git:status` / `#git:log`** — new Bridge endpoint
  `POST /project-git/run` (`Aria.Bridge/Endpoints/GitEndpoints.cs`, gated by
  `SecurityPolicy.EnforcePath` exactly like `ProjectFileEndpoints`), surfaced through
  `ProjectFilesClient.RunGitAsync`. The `#git:` prefix opens a small fixed-choice
  picker (`diff`/`status`/`log`) in `Chat.FilePicker.razor.cs` instead of the fuzzy
  file list. `#git:@<sha>` / `#pr:N` remain `Planned`.
- **`/compact`** — now a two-step flow: typing `/compact` opens a confirmation modal
  (`Chat.razor`, reusing the generic `.modal-backdrop`/`.modal-panel` CSS) warning
  that history will be replaced and is unrecoverable; confirming runs
  `CompactAsync()` (`Chat.Messaging.razor.cs`), which streams a summary through the
  existing `AgentService.StreamAsync` pipeline, replaces the persisted transcript
  (bridge-owned cogitations via a new `POST /cogitations/{id}/compact` Bridge
  endpoint + `BridgeCogitationClient.CompactAsync`; legacy/server-stored ones via
  `CogitationService.CompactAsync`), and — to actually reclaim the live context
  window immediately rather than on some future session recreation — swaps in a
  fresh SDK thread via `_session = await _agent.CreateSessionAsync()`. A "⟲
  COMPACTED" divider marks the cut point in the rendered chat.
- **`/clear`** was already `Available` before this round (pre-existing "start fresh
  session" flow) — not part of this implementation pass, listed here only because
  the original doc had it down as a gap.

Everything else in the tables below (`#url`, `#mem`, `#sym`, `#diag`, `#mcp`,
`#agent:`/`#skill:`, `/model`, `/commit`, `/review`, `/diff`, `/test`, `/hive` armed-
input, etc.) is still `Planned` / unimplemented.

---

## 1. `#` references — typed context injection

**Design shift:** `#` becomes a **namespaced** token, `#type:arg`. The dispatcher in
`UpdatePickersAsync` / `_refTokenRx` (`Chat.FilePicker.razor.cs:29,60`) inspects the
prefix before the first `:` and routes to the right resolver. Every new source is a
bridge endpoint reached the same way `ProjectFilesClient` reaches `/project-files/*`,
gated by `SecurityPolicy.EnforcePath` + `AllowedPaths`.

**Back-compat:** a token with **no colon** (`#src/foo.cs`) stays a file path, so the
current behaviour is unchanged.

| Reference | Resolves to | Backing service | Effort |
|---|---|---|---|
| `#path/to/file` *(have)* | file content | `ProjectFileEndpoints /read` | — |
| `#folder:src/` · `#dir:` ✓ *(done)* | recursive tree + key file heads | `/project-files/tree` (`ProjectFilesClient.ListTreeAsync`) | **Low** |
| `#git:diff` · `#git:status` · `#git:log` ✓ *(done)* | working diff / staged state / recent log | `GitEndpoints` (`git` via `ProcessStartInfo`, gated by `AllowedPaths`) | Low |
| `#git:@<sha>` · `#pr:123` | a commit or PR diff | `git` / `gh` | Med |
| `#sym:Name` | a symbol's definition body | needs an index (LSP or ctags) | Med-High |
| `#diag` · `#problems` | compiler / LSP diagnostics | parse `dotnet build` or LSP | Med |
| `#url:https://…` | fetched & cleaned page text | existing web-fetch tooling | Low |
| `#mem:<query>` | a Hindsight memory hit | `HindsightTools` (already bridge-routed) | Low |
| `#mcp:<server>/<resource>` | an MCP resource | `McpTools` / `SessionStore` | Med |
| `#term` · `#out` | last terminal / tool output | session buffer | Low |
| `#agent:<name>` · `#skill:<name>` | inject a persona / skill snippet | `SubAgent` / `Skill` tables | Low |

**Phase-1 picks:** `#folder`, `#git:diff`, `#url`, `#mem` — all reuse existing bridge
plumbing and give the biggest "it knows my repo/context" jump.

**Structural work:** extend `_refTokenRx` to capture an optional `type:` prefix and
dispatch on it instead of always calling `OpenFilePickerAsync`. Each resolved
reference becomes a chip in `_referencedFiles` (generalise to `_referencedRefs`) and
expands to content on send.

---

## 2. `/` commands — palette catalog

Populate the `AllCommands` array (`Chat.FilePicker.razor.cs:34`). ✓ = exists,
◷ = planned in this doc.

### Session / context
- `/clear` ✓ — wipe the conversation (purge the cogitation)
- `/compact` ✓ *(done)* — summarise history to reclaim context window, behind a
  confirmation modal (see "Implemented so far" above)
- `/resume` · `/rewind` — reload or checkpoint-restore a cogitation (`CogitationEndpoints`; the `commitRestore` branch is already here)
- `/export` — dump transcript to markdown / file
- `/cost` · `/tokens` — token + spend for the session

### Memory / project
- `/remember <text>` — write a Hindsight memory (the write-sibling of `#mem:`)
- `/init` — scan project, generate a CLAUDE.md-style brief
- `/project` ✓ — choose which project the `#` picker searches

### Config / capabilities (palette entry points to existing flyout panels)
- `/model` — switch `ModelSource`
- `/agents` — sub-agent persona manager
- `/skills` — skill snippet manager
- `/mcp` — MCP server connect / list (`SessionStore`)
- `/tools` — toggle available tools
- `/soul` — soul link / unlink / verify status

### Dev workflow (biggest parity gap)
- `/review` — review working diff
- `/commit` — draft a commit message from the diff
- `/diff` — show working changes inline
- `/test` · `/build` — run and feed results back

### Aria-native (surface existing engines in the palette)
- `/hive` ◷ — route the next message to a Hive collective (see §3)
- `/wargame` ✓
- `/vigil` — schedule a cron slot (`CronSlotService`)
- `/vox` — voice input (`VoxService`)
- `/exchange` — soul-to-soul session (`ExchangeService`)
- `/help` — list all commands

**Phase-1 picks:** `/clear`, `/compact`, `/model`, `/commit`, `/review`, `/help`.
First two are table-stakes (both now ✓ *done*); `/commit` + `/review` are the leap
from chat client to agentic CLI and remain `Planned`.

**Structural work:** the `SlashCommand` record (`Chat.FilePicker.razor.cs:31`) is
`(Name, Description)` and the palette filters on `Name.StartsWith` only (line 78).
Add description/keyword matching and an **argument hint** (e.g. `/model <source>`),
since several commands take args — that's what makes the palette feel like Claude
Code's rather than a static menu.

---

## 3. `/hive` — armed-input deep dive

The headline interaction: select a Hive collective, type a directive, and the active
agent **delegates to the hive and waits for its synthesis**.

### Why this is ~80% wired already
`CollectiveOrchestrator.RunCogitationAsync(collectiveId, userPrompt, onMessageAdded, ct)`
(`CollectiveOrchestrator/CollectiveOrchestrator.Cogitation.cs:18`) already:
- creates a cogitation and writes the user prompt,
- fires background orchestration that **streams each drone/Overmind message** via the
  `onMessageAdded` callback,
- returns a `HiveCogitationResult(CogitationId, Success, Error)`.

And `Chat.HiveGate.razor.cs` already consumes the stream and the gates:
- `OnHiveCogitationUpdated(cogId)` (line 57) appends new hive messages into the chat,
  filtered by `cogId == _cogitationId`,
- `ApproveGate` / `ApproveMemberGate` wire the human-in-the-loop approvals
  (`OnHiveGatePending`, `OnHiveMemberGatePending`).

### The armed flow
1. **`/hive`** does not execute — it opens a **second-level picker** of the user's
   collectives (mirror `OpenProjectPicker()`), sourced from
   `CollectiveService.GetListAsync(userId)`.
2. Pick a collective → the input enters an **armed** state. A chip sits above the
   textbox; the active agent is benched.
3. Type the directive and send → instead of the normal agent dispatch, the text goes
   to `RunCogitationAsync(armedHiveId, text, OnHiveCogitationUpdated)`.
4. Drone messages stream inline; gate approvals surface inline (already built). The
   active agent shows a "delegating to ⬡ Hive…" waiting state.
5. Hive finishes → its final Overmind synthesis is handed back, and the active agent
   resumes.

```
┌─ chat ─────────────────────────────────────────────┐
│  ◈ you: …                                           │
│  ◈ Aria: …                                          │
│                                                     │
│  ╭─ ⬡ AUSPEX COHORT armed ───────────────  [✕] ─╮   │
│  │ next directive routes to the Overmind        │   │
│  ╰──────────────────────────────────────────────╯   │
│  ┌──────────────────────────────────────────────┐   │
│  │ > scout three approaches to the cache bug_    │   │
│  └──────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────┘
```

### Two models — pick the mental model first

**Model A — Takeover.** `/hive X` switches the conversation *into* the hive
cogitation. The active agent steps aside; the thread becomes the hive run. Simplest:
point `_cogitationId` at the new cog `RunCogitationAsync` creates, and the existing
`OnHiveCogitationUpdated` filter matches automatically.

**Model B — Delegation (active agent waits).** ⭐ *Recommended.* The active agent
stays in charge. The hive runs as an **inline collapsible "⬡ deliberation" block** —
structurally the same as the existing inline tool-call activity. On completion the
hive's synthesis is injected back into the agent's context and *the agent* composes
the final reply. Visually: agent delegated → waited → answered. This frames a hive as
a delegate-able heavyweight **tool**, consistent with the rest of the UX, and is what
"the active agent waits for the hive output" actually means.

### Wiring points
- **Palette → collective list:** new `_hivePickerOpen` stage mirroring
  `OpenProjectPicker()`; list from `CollectiveService.GetListAsync(userId)`.
- **Arm state:** add `int? _armedHiveId` to `Chat`; render the chip in `Chat.razor`;
  clear on send or `✕`.
- **Send branch:** in `Chat.Messaging.razor.cs`, `if (_armedHiveId is { } hid)` →
  call `RunCogitationAsync(hid, text, OnHiveCogitationUpdated)` instead of the normal
  agent dispatch.
- **Streaming / gates:** already done. Model A — switch `_cogitationId` to the new
  cog. Model B — keep a separate hive-block buffer keyed on the hive cogId so it
  renders as one collapsible block (don't rely on the `cogId == _cogitationId`
  filter).

### The one real gap
There is **no completion event**. The background run signals only via `onMessageAdded`;
nothing emits "hive done, here is the synthesis." Model B's hand-back requires adding:

```csharp
// CollectiveOrchestrator.cs — alongside OnHiveGatePending etc. (lines 42–60)
public event Action<int, string>? OnHiveCogitationComplete;  // (cogId, finalSynthesis)
```

fired at the end of `RunCogitationBackgroundAsync`
(`CollectiveOrchestrator.Cogitation.cs:71`). Chat subscribes, then feeds the synthesis
to the active agent as the next turn's context.

---

## 4. Phased rollout

| Phase | `/` commands | `#` references | Hive |
|---|---|---|---|
| **1** ✓ *done* | `/clear`, `/compact`, `/help` | `#folder`, `#git:diff/status/log` | — |
| **2** | `/model`, `/commit`, `/review` | `#url`, `#mem` | — |
| **3** | — | — | `/hive` Model B: armed input + deliberation block + `OnHiveCogitationComplete` |
| **4** | `/exchange`, `/vigil`, `/agents`, `/skills` | `#sym`, `#diag`, `#mcp` | gate/streaming polish |

Phase 1 shipped 2026-07-06, plus one unplanned addition: the `list_chat_capabilities`
agent-awareness tool (see "Implemented so far" above), which wasn't in the original
scope but was needed alongside it since the agent had no way to answer questions
about these very features. Phase 1 also delivered the **structural** prerequisites:
namespaced `#type:` dispatch (`BuildReferenceNote` in `Chat.FilePicker.razor.cs`),
and the `ChatCatalog` status model that both the palette and this doc's "done" marks
now key off of. Note `/help`'s actual implementation is `/help` + `/index`, both
opening the same reference panel — no argument-hint work was needed for the Phase-1
set since none of `/clear`/`/compact`/`/help` take arguments; that part of the
structural work (keyword-match + arg hints for commands like `/model <source>`)
remains open for Phase 2.

---

## 5. Open questions / gaps

- **Completion event** — add `OnHiveCogitationComplete` (above); decide whether the
  hand-back is automatic or user-confirmed.
- **Palette argument model** — how `/model <source>` and `/remember <text>` pass args:
  inline (rest of the line) vs. a follow-up step. Affects the `SlashCommand` shape.
- **`#` namespacing back-compat** — ✓ resolved and implemented: tokens **without** a
  colon remain file paths; only `prefix:` tokens (`folder:`, `dir:`, `git:`) route to
  new resolvers in `BuildReferenceNote`.
- **Indexing** — `#sym` / `#diag` need a symbol index; defer until an LSP/ctags
  source exists (Phase 4).
