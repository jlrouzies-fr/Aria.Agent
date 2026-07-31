# // ONE SOUL, MANY COGITATORS — Multi-node routing

[← Back to the cogitator terminal](../../README.md) · [Architecture](architecture.md)

How channels, keys, format detection, terminal projects, and diagnostics behave when one soul has
bridges on several machines (e.g. a Mac and a Windows PC). This is the map of "what executes
where" — most multi-node confusion comes from one fact:

> **The browser you are typing in has no routing significance.** Every action goes browser →
> server (Blazor circuit) → SignalR tunnel → *some* bridge. Which bridge is chosen by explicit
> binding rules, never by which machine your browser is on.

- [Joining a second node](#joining-a-second-node)
- [Routing rules](#routing-rules)
- [Channels and node binding](#channels-and-node-binding)
- [Provider keys: per-node vaults + encrypted mesh sync](#provider-keys-per-node-vaults--encrypted-mesh-sync)
- [Thinking/tool-call format detection](#thinkingtool-call-format-detection)
- [Terminal projects on multiple machines](#terminal-projects-on-multiple-machines)
- [Error surfacing in the chat](#error-surfacing-in-the-chat)
- [Access gate and knocks](#access-gate-and-knocks)
- [Diagnostics](#diagnostics)

---

## Joining a second node

Adding a machine is one join flow with two human checks — approve the device, then confirm the
soul's master-key fingerprint on the new machine. The fingerprint is **not** a separate "security
chore"; without it the new node refuses grants signed elsewhere (so seals/approvals will not
replicate to it). That is intentional: every candidate key reaches a joined node through the
hosted server, so the anchor has to come from the primary bridge out of band. See
[Security](security.md) and the Layer B trust-anchor note in
[`docs/security/phase2-context-grants-remaining.md`](../security/phase2-context-grants-remaining.md).

1. On the **new** machine, open the bridge status page ([http://localhost:5741](http://localhost:5741)) → **Soul**.
2. Under **Join an existing soul**, paste the **Server Soul ID** from Aria.Web → **Devices** (not a local bridge ID), set a label, and click **JOIN**.
3. **JOIN · STEP 1 — APPROVE THIS DEVICE** — note the pairing code on the new machine. In Aria.Web → **Devices** (from a browser that already trusts your primary), enter the code and **APPROVE**.
4. On the **primary** bridge → **Soul**, click **▶ SHOW FINGERPRINT**
   (`abcd-efgh-ijkl-mnop`). Read it from that machine's own bridge — never from Aria.Web.
5. Back on the **new** machine, the Soul panel shows **JOIN · CONFIRM MASTER KEY**. Paste the
   fingerprint and click **CONFIRM & FINISH JOIN**.

Until step 5, Aria.Web → Devices may pulse a **JOIN NOT FINISHED** warning on that device. After a
successful confirm, sibling grants and seal replication work on the new node. Reconnect after
approval can take up to a few minutes — leave the bridge running.

If the fingerprint keeps failing to match, stop and investigate: the server may be presenting a
key that is not your primary's. Do not pin a value you cannot read from the primary bridge itself.

---

## Routing rules

| Traffic | Routed to | Code |
|---|---|---|
| Chat completions | The channel's **bound node** (`UserLocalSource.BridgeNodeId`) | `Chat.razor.cs → ResolveNodeId` |
| Format probes (thinking + tool-call) | The channel's bound node | `AgentService.DetectThinkingFormatAsync → ResolveSourceNodeId` |
| Channel editor probe / key save | The channel's bound node (refuses if that bridge is offline) | `NavMenu.Channels → ValidateLocalSourceAsync`, `SaveLocalSourceAsync` |
| Cloud-provider key save (modal) | The **default node** (most recently connected), then mesh-synced everywhere | `NavMenu.Channels → SaveKeyAsync` |
| Terminal tools (`bash_exec`, `read_file`, …) | The node whose **project path matches the call's path argument** | `PathRoutedTerminalTool` |
| Browser attestation / soul discovery | The browser machine's own `localhost:5741` bridge (the one place locality matters) | `aria-interop.js → attestViaLocalBridge` |
| Config snapshot sync | **Every** connected node | `BridgeSyncService.PushSnapshotAsync` |

“Default node” = the most recently connected bridge. It is only used where no better binding
exists; anything channel-scoped must use the channel's binding.

## Channels and node binding

A bridged channel's URL (`http://127.0.0.1:1234/v1`) is relative to **one machine's localhost** —
the same string means a different server on every node. Therefore:

- Saving a bridged channel **binds it to a node**: auto-bound when exactly one bridge is online;
  with several online the editor refuses to save until a bridge is selected.
- Everything channel-scoped (chat, probes, key custody checks) follows that binding.
- The channels list shows the bound node under each channel (`⤷ MyWorkstation`) and warns when
  the bound bridge is offline.

Historic failure mode (fixed): with no binding, traffic fell through to the default node — a chat
on the Mac's Gemma channel could be answered by whatever model the *Windows* server had loaded,
and a key entered "on the Windows browser" landed in the Mac's vault.

## Provider keys: per-node vaults + encrypted mesh sync

Keys live in each bridge's local SQLite vault (`LlmKeys`), never on the server. With several
nodes, the executing bridge needs the key **in its own vault** — so keys replicate:

- `GET /keys/sync-export` (bridge) — the whole key set encrypted with the soul's **sync data key**
  (the §11 DEK every enrolled node holds). `POST /keys/sync-import` decrypts and **merges**
  (upsert per provider, never deletes). The server relays ciphertext it cannot read.
- The mesh (every node's export → every other node) runs automatically after a key save and when
  a node connects, and manually via `POST /api/maintenance/replicate-keys?userId=…`.
- After a key save the UI states exactly what happened:
  `⚿ Key 'Window' stored on WindowsELFI2 · synced to 1 other bridge(s)` — or an explicit warning
  when storage or sync failed (e.g. the other bridge predates 0.9.0).
- The key icon in the channels panel means "**some** bridge of this soul holds a key under this
  channel's name" (union across nodes, `RefreshConfiguredProvidersAsync`).

The bridge vault itself lives in per-user app data (`%APPDATA%\aria-bridge\`,
`~/Library/Application Support/aria-bridge/`) since 0.8.0 — reinstalling the bridge no longer
wipes the soul; a legacy vault next to the executable is migrated on first run.

## Thinking/tool-call format detection

Detection (`FormatProber`, run **on the channel's bound bridge** via `/llm/detect-format`, key
injected from that node's vault) returns:

- `ReasoningContent` / `ThinkTags` / `StartsInThinkMode` / tool-call formats — positive detections,
  cached in `ModelFormatCaches` (server DB + memory).
- `None` — probe **succeeded** and saw no thinking markers. Cached.
- `Unknown` — probe **failed** (endpoint down, auth rejected, …). Says nothing about the model;
  **never cached**, never feeds assumptions; the next session re-probes.

There is deliberately **no model-name heuristic** anymore (there used to be one forcing
`StartsInThinkMode` for qwen3/gemma-4/etc. when the probe returned None). A wrong think-mode
verdict is the worst failure available — the entire answer streams into the thinking block and
the reply is empty — and any probe failure used to trigger it. `reasoning_content` deltas are
handled dynamically regardless of the cached verdict, so conditional thinkers still render
correctly under `None`.

Stale/poisoned verdicts are recoverable: **Channels panel → // MAINTENANCE → CLEAR MODEL FORMAT
CACHE**, or `DELETE /api/maintenance/format-cache?model=<fragment>`.

## Terminal projects on multiple machines

Each terminal project is bound to a node, but every node exposes the same tool names. Instead of
registering colliding duplicates, `PathRoutedTerminalTool` merges them: one function per name,
and each **call** is dispatched to the node whose project path prefixes the call's path argument
(`path`, `working_dir`, `base_dir`, `pattern`) — comparison is case- and separator-insensitive,
so `c:/users/x` matches `C:\Users\X`. Calls with no path argument go to the LLM channel's node.

The system-prompt addendum tags each project with its OS when projects span machines and
instructs the model to copy path prefixes verbatim (no restyling `C:\…` into `/c/…`).

## Error surfacing in the chat

The tunnel relays upstream responses as `200 text/event-stream` even when the model endpoint
failed (the transport cannot change status mid-stream). A raw error body used to parse as an
empty SSE stream → **silent empty reply**. Now `UniversalReasoningHandler` peeks the first bytes
of every chat stream: a JSON `{"error": …}` body becomes a thrown fault, rendered in the chat as

```
// COGITATOR FAULT: The model endpoint rejected the request: An LM Studio API token is required… //
```

The full raw exchange is retained in the bridge's egress log (below). Note: LM Studio logs its
auth rejections as `Unexpected endpoint or method … Returning 200 anyway` — that message means
"request intercepted before routing", typically missing/invalid API token, not a URL problem.

## Access gate and knocks

Every connected bridge knocks its public IP once a minute; the gate admits requests from knocked
IPs (plus trusted cookies / invite codes). Knocks are stored **per IP with a per-user cap** — an
earlier version kept only the latest knock per user, so two machines (or IPv4 vs IPv6 from one
LAN) evicted each other every ~40 s and the gate flip-flopped between them.

## Diagnostics

Production-safe endpoints (behind the access gate) in `Endpoints/MaintenanceEndpoints.cs`:

```
GET    /api/maintenance/nodes?userId=…                        connected nodes
GET    /api/maintenance/node-keys?userId=…                    per-node provider key NAMES
GET    /api/maintenance/node-llm-log?userId=…&nodeId=…        a node's recent LLM egress (bridge ≥0.9.1)
POST   /api/maintenance/test-channel?userId=…&source=…        fire a minimal completion through the
                                                              channel's bound bridge, return raw outcome
POST   /api/maintenance/replicate-keys?userId=…               run the key mesh now
DELETE /api/maintenance/format-cache?model=…                  purge cached format verdicts
```

On each bridge: `GET /debug/llm-log` — ring buffer of the last 25 outbound LLM calls with URL,
auth-header presence, status, content-type, and the first 600 bytes of the response (this is how
"chat shows nothing" resolves into "LM Studio said invalid_api_key" without touching the machine).
