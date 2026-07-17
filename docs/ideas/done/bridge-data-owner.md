# Plan — Move conversation data off the server, onto the cogitator node

## Context

**The problem.** The project's thesis is "the server holds nothing of yours," but today the
**server DB (`AppDbContext` SQLite) is the source of truth** for all conversation content —
chat messages, hive transcripts, vigil results. The bridge (`BridgeDbContext`) keeps only a
best-effort secondary *mirror* written fire-and-forget by `BridgeCogitationClient`. So the most
sensitive data lives in plaintext on the server, exactly opposite to the product promise.

**The change.** Invert ownership: the **local cogitator node (bridge) becomes the source of
truth** for conversation *content*; the server keeps only an **index** (IDs, ownership, title,
avatar, timestamps, status, scheduling config, and a new `OriginNodeId`). When the node that
holds a conversation is offline, the list still renders from the index but the conversation body
shows a **warning** instead of content.

**Replication model (decided with user).**
- **Default = Pinned to origin node.** Content lives *only* on the bridge that created it.
  The server stores none of it — not even ciphertext.
- **Optional "Sync" (per-soul toggle) = live encrypted relay.** When enabled, the origin bridge
  encrypts content with its **DEK** (`DataKeyBase64`, the AES-256 key every one of the user's own
  bridges already holds; the server never sees it) and pushes it *through* the server to the
  user's **other currently-connected bridges**, which decrypt and store a local copy.
  **The server only relays the blob — it never persists it** (this is the key difference from the
  existing `SyncRecord.CipherBlob`, which the server *does* store).
- **Inherent trade-off (accepted):** a pure relay only reaches nodes online at the time. A node
  that was offline during the conversation won't receive that copy (store-and-forward would force
  the server to hold the encrypted blob, violating "never store"). Sync is therefore best-effort
  fan-out; reads degrade gracefully.

**Scope.** Plan covers all three data types (chats, vigils, hives). **Phase 1 (chats) is the
focus** — it's lowest-risk, reuses infrastructure that already exists, and validates the
offline-warning UX. Vigils and hives follow the same pattern.

---

## What already exists (reuse, don't rebuild)

- **REST tunnel with per-node targeting.** `ModelBridgeRegistry.SendLocalRestAsync(userId, method,
  path, body, nodeId, timeout)` (`Aria.Web/Services/ModelBridgeRegistry.Routing.cs:90`) already
  pushes a `LocalRestRequest` (`Aria.Shared/BridgeRequest.cs:19`) to a **specific node** and
  returns `null` when that node isn't connected. Round-trips via
  `Aria.Bridge/DirectTunnel.cs:276` (`HandleLocalRestAsync`) → `CompleteLocalRest`.
- **Bridge local store + read endpoints.** `Aria.Bridge/Endpoints/CogitationEndpoints.cs` against
  `BridgeDbContext` already exposes `GET /cogitations`, `GET /cogitations/{id}`,
  **`GET /cogitations/{id}/messages`**, plus `POST /init`, `POST .../messages`, `PUT`, `DELETE`.
- **Deterministic IDs.** Bridge cogitation id = `sv-{serverCogitationId}`
  (`BridgeCogitationClient.BridgeId`, `:19`) — no mapping table needed.
- **Write mirroring + a read example.** `BridgeCogitationClient` (`Aria.Web/Services/BridgeCogitationClient.cs`)
  already writes init/title/messages to the bridge and **reads** contacts via
  `GetContactsAsync` (`:71`) — the exact deserialize-the-body pattern to copy for messages.
- **DEK + node crypto.** Bridge `DataKeyBase64` is provisioned by `EnsureDataKeyAsync`
  (`DirectTunnel.cs:161`); `NodeCrypto` and the `SoulNodeKey` allow-list already exist.
- **Node identity for `OriginNodeId`.** The connecting node's `NodeId` is known at
  `ModelBridgeHub.RegisterDirectBridge` / `_registry.RegisterNode`.
- **Offline surfaces.** `HasBridge` / `IsSoulVerified` (`ModelBridgeRegistry.cs:58-187`), the
  `BridgeGatewayModal` `Offline` state, and `SoulVerified` chat gating already exist to build on.
- **Exchange is already aligned** — `ExchangeSessionService` keeps nothing server-side and pushes
  transcripts to the bridge via `BridgeCogitationClient.PushExchangeTranscriptAsync`. Use as the
  reference precedent; no change needed.

---

## Phase 1 — Chats (the focus)

### Server index (metadata only)
- `Aria.Web/Data/Cogitation.cs` — add `string? OriginNodeId`. **Semantics:**
  `null` = legacy/server-stored (read content from server DB, back-compat); non-null = content
  lives on the bridge with that node id. Keep `Title`, `AriaAvatarKey`, timestamps, `UserId`,
  `SubAgentId` as-is.
- `Aria.Web/Data/CogitationMessage.cs` — **stop persisting `Content` / `ThinkingContent` for
  new (bridge-owned) cogitations.** Simplest: stop creating server `CogitationMessage` rows once
  `OriginNodeId` is set; the bridge holds messages. Keep the table for legacy rows.

### Read path → bridge
- `Aria.Web/Services/BridgeCogitationClient.cs` — add **read** methods mirroring `GetContactsAsync`:
  `GetMessagesAsync(userId, serverCogId, originNodeId)` → `GET /cogitations/{sv-id}/messages`, and
  `GetCogitationAsync(...)`. Route with `nodeId: originNodeId`, and on `null`/404 **fall back**:
  iterate the user's other connected nodes (for synced conversations) and take the first `200`.
- `Aria.Web/Components/Pages/Chat.Session.razor.cs:223` — replace
  `CogitationService.GetMessagesAsync(...)` with the bridge read (keep server read only when
  `OriginNodeId == null`). Map rows into `MessageEntry` as today (`:224-240`).
- `Aria.Web/Services/CogitationService.cs` — `GetMessagesAsync`/`AddMessageAsync` become thin
  routers: bridge-owned → bridge; legacy → server DB.

### Write path → bridge only
- `Aria.Web/Components/Pages/Chat.Messaging.razor.cs` (`:168,287` etc.) — on new cogitation,
  stamp `OriginNodeId` = the currently-active node, call `EnsureCogitationAsync`, then append
  messages **only** via `BridgeCogitationClient` (drop the server `AddMessageAsync` for
  bridge-owned cogitations). `BridgeCogitationClient` writes should no longer swallow errors
  silently for the source-of-truth path — surface failures so the UI can warn.

### Offline UX
- **List** (`NavMenu` cogitation list): renders from server index; show a greyed/"⚠ offline" badge
  when `OriginNodeId` is set and neither it nor any other node currently serves it
  (cheap check via `HasBridge` + node presence).
- **Opening a conversation** whose node is offline: render a banner in `Chat.razor` instead of
  messages — e.g. "⚠ This cogitation is held on cogitator node «{label}», which is offline.
  Connect that node to view its contents." Reuse `BridgeGatewayModal` `Offline` styling.
- **Continuing** a chat requires prior messages for context → if the holder is offline, block the
  composer with the same warning (can't append to a conversation you can't read).

### Optional Sync (encrypted relay, server never stores)
- **Toggle:** add a per-soul `SyncEnabled` flag (User/soul settings + a switch in the souls/settings
  panel). Default off (pinned).
- **Relay hub method:** `Aria.Web/Hubs/ModelBridgeHub.cs` — add `BroadcastSync(blob)` that pushes
  `HandleSyncBlob(blob)` to the caller's **other** connected node connections for that user. The
  server forwards the opaque blob and **does not persist it**.
- **Origin side:** after a local write, if `SyncEnabled`, the bridge encrypts the cogitation+message
  (AES-GCM with `DataKeyBase64`) and calls `BroadcastSync`.
- **Receiver side:** `Aria.Bridge/DirectTunnel.cs` — register a `HandleSyncBlob` handler that
  decrypts with the DEK and upserts into the local `BridgeDbContext` (idempotent on the `sv-{id}`
  key). Reads then naturally succeed from any node that received the copy via the read-fallback.

---

## Phase 2 — Vigils (cheap; mostly follows Phase 1)

`Aria.Web/Data/AgentCronJob.cs` already stores **only `ResultSummary` + a `CogitationId` pointer**
(`CronSlotService.MarkCompletedAsync`, `:155-162`); the full transcript is the linked Cogitation,
which becomes bridge-owned in Phase 1. So:
- The result transcript follows automatically through `CogitationId` once chats are bridge-side.
- Keep `TaskPrompt` and scheduling config (`SourceName`/`ModelId`/`ScheduledDate`/`ScheduledHour`)
  **server-side** — the `CronSchedulerHostedService` needs them to dispatch the job. (Pragmatic:
  the job can only run when the bridge is online anyway, since the LLM call proxies through it.)
- `ResultSummary` is a short teaser; keep a trimmed copy server-side for list display, or blank it
  and show "open on node". Recommend: keep the short teaser as metadata.
- `CronVigilsView.razor` "VIEW ▶" (`:57-59`) already opens the linked cogitation → inherits the
  Phase 1 offline banner with no extra work.

---

## Phase 3 — Hives (highest risk; same pattern, more content fields)

Move to the bridge (content): `CollectiveTask.Instruction` / `EffectiveInstruction` / `Result`
(`Data/CollectiveTask.cs`), `CollectiveEvent.Message` (`Data/CollectiveEvent.cs`),
`AgentCollective.Objective` / `ResultSummary` / `LastFeedback` / `SynapseMemory`
(`Data/AgentCollective.cs`). Each drone task already links a `CogitationId` → reuse Phase 1 for
the per-drone transcript. Keep structural metadata server-side: members, edges, canvas
(`CanvasZoom/PanX/PanY`), `Status`, `Round`, approval flags, `OvermindSubAgentId`, ownership.

**Risk to mitigate:** `CollectiveOrchestrator` (`Aria.Web/Services/CollectiveOrchestrator/`) reads
and writes content *during a live run*. Naively routing every read/write through
`SendLocalRestAsync` (15s timeout) mid-orchestration is slow and fragile.
**Mitigation:** the orchestrator already operates on in-memory state during a run — keep content
in memory for the duration, and **persist to the bridge at checkpoints** (round boundaries,
task completion, run end). Read from the bridge only for display and for resuming a run.
Add `OriginNodeId` to `AgentCollective`; the offline banner applies to the whole hive view when
its node is down.

---

## Cross-cutting

- **No big-bang migration.** `OriginNodeId == null` means "legacy, read from server DB," so old
  conversations keep working untouched. New ones are bridge-owned. Optionally add a one-shot
  backfill (on a node's first connect, push its user's legacy content down to that bridge, stamp
  `OriginNodeId`, then clear server content) — defer until Phase 1 is proven.
- **Error visibility.** Today `BridgeCogitationClient` swallows all errors (it's a mirror). For a
  source-of-truth write, failures must surface (so the user sees "couldn't save to your node")
  rather than silently lose data.
- **Build/restart after every change** per `CLAUDE.md`: rebuild and restart both `Aria.Web` and
  `Aria.Bridge`.

---

## Verification (end-to-end, Phase 1)

1. Rebuild + restart both apps (`dotnet build`; run Bridge then Web; check `/health` and
   `/api/debug/mcp-bridge/health`).
2. With one node (A) connected, create a chat and send a few messages. **Confirm content split:**
   - Server: `Cogitation` row exists with `OriginNodeId = A`, **no `CogitationMessage` content
     rows** (inspect via `/api/debug` or the SQLite file).
   - Bridge: `GET http://127.0.0.1:5741/cogitations/sv-{id}/messages` returns the messages.
3. Stop node A's bridge → reload the app → the chat still appears in the list (greyed/⚠), and
   opening it shows the offline warning, composer blocked.
4. Reconnect A → content returns; conversation continues normally.
5. **Sync path:** enable the soul's Sync toggle, connect a second node (B). Send messages on A →
   confirm B receives the encrypted relay and stores a local decrypted copy
   (`GET .../cogitations/sv-{id}/messages` on B's port). Stop A → confirm the chat is still
   readable (served from B via read-fallback). Verify the server never persisted the blob (no new
   `SyncRecord`/no server message rows).
