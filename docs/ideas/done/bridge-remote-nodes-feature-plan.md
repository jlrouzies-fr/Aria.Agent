# Bridge Remote Nodes — Feature Plan

> **Three coupled goals, on one foundation (multiple authenticated bridges per soul):**
> 1. **Route** a chat / agent / terminal project to a **specific bridge** — e.g. a thin laptop drives
>    Aria.Web while the work runs on the home PC's bridge (which has the LLM and the files).
> 2. **Add machines safely** — a second bridge joins the *same* identity without copying the master
>    key, via per-node keys + a co-equal-owner enrollment allow-list (any node can vouch for / revoke
>    another).
> 3. **Survive losing a machine** — end-to-end-encrypted replication of the soul's private dataset
>    (history, agents, skills, hives) across all nodes, so no single PC is a single point of loss.

Status: **design** — not yet implemented. Grounded in the current direct-tunnel architecture
(`Aria.Bridge/DirectTunnel.cs`, `Aria.Web/Services/ModelBridgeRegistry.cs`,
`Aria.Web/Hubs/ModelBridgeHub.cs`, `Aria.Bridge/Endpoints/SoulEndpoints.cs`). See also
`./bridge-direct-tunnel.md`.

---

## 1. Where we are today

- A bridge opens an **outbound SignalR** connection to the server (`DirectTunnel`) and authenticates
  with the **soul's ECDSA private key** via challenge-response (`GetDaemonChallenge` →
  `RegisterDirectBridge`). The server marks the soul verified (`SetSoulVerified("direct-{userId}")`).
- The server routes LLM calls and local REST to the bridge over that connection
  (`HandleRequest` / `HandleLocalRest`), which the bridge forwards to its own `localhost:5741`.
- **Hard limit:** `ModelBridgeRegistry._directConns` is `userId → connectionId` — exactly **one**
  bridge per soul. A second bridge for the same soul overwrites the first.
- A **channel** = a model source (`ModelSource`, local-`IsBridged` or public cloud). A **terminal
  project** = a named path entry in the `terminal` tool config (`AllowedPaths` JSON:
  `{name, path, description}`), exposed to the agent via `BuildTerminalAddendum`.
- The agent already gets OS-specific terminal guidance (e.g. "On Windows, `bash_exec` uses
  `cmd.exe`") but the OS is **assumed**, not reported by the bridge.

The trust model is already the right one: **possession of the soul private key = this is the
soul's machine.** The missing pieces are (a) letting the server hold **more than one** authenticated
connection per soul, (b) a way for a second machine to join the *same* soul **without copying the
master key** — solved by per-node keys + a soul/node-signed enrollment allow-list (§2), and (c) the
UI to target a chosen node. Co-equal nodes (any enrolled node can vouch for / revoke another) make
the set resilient to losing any single machine.

---

## 2. Concept: a Node, and the co-equal-owner trust model

A **Node** is one running bridge instance for a soul. The same soul may have several (laptop, home
PC, work PC). Each node has its **own** keypair — the soul's master key is **never copied** onto a
second machine. Nodes are **co-equal owners**: any enrolled node can authorize the next one and
revoke a lost one, so losing a single machine is survivable.

### Two distinct keys

| Key | Lives where | Purpose |
|---|---|---|
| **Soul key** (`User.PublicKey` on server; private on the original bridge + an encrypted backup) | the first bridge, and a user-held backup | root of trust; also the *recovery* key if you ever drop to zero nodes |
| **Node key** (per machine, generated locally, never leaves it) | each bridge | proves "I am node X" on every connect; co-equal nodes use it to sign enrollments/revocations |

### Enrollment — how a new bridge joins the *same* userId

A fresh bridge would normally create a new soul → new `serverSoulId` (= new userId). The feature's
job is to make it **reuse the existing userId** instead, by being vouched for:

1. New bridge generates its node keypair, picks a `label`, reports `platform`, and shows its
   **node public key** + a short fingerprint ("Join existing soul").
2. An **existing authority** approves it — the soul key *or* (the co-equal part) **any already-enrolled,
   non-revoked node**. The approver signs an enrollment certificate over the new node's pubkey
   (§9.3). In the UI this is "Devices → Add device" on an already soul-verified Aria.Web session,
   which has the approving bridge sign over its tunnel.
3. Server records the new node's pubkey in a per-soul **allow-list** (`SoulNodeKey`, §9.1).
4. New bridge connects and signs the challenge nonce with **its own node key**; server verifies
   against the allow-list and registers it under the existing `serverSoulId`.

### Why this is multi-user-safe

Every enrollment/revocation/connect is verified against material **scoped to one `serverSoulId`**:
the soul public key plus that soul's allow-listed node keys. Another user can't produce a signature
that verifies against *your* set, so they can't enroll a node into your account or impersonate one
of your nodes. The server never trusts a connection because it "looks right" — only because a
signature checks out against a key it already holds for that specific user.

### Authorization = a signature from the live set {soul key ∪ non-revoked node keys}

This single rule delivers co-equal ownership: enroll-node and revoke-node requests are accepted iff
signed by **any** member of that set. Consequences:

- Bridge 2 can enroll bridge 3 (signs with its node key) even if bridge 1 is gone.
- You can lose any one node while ≥1 remains; revoke the lost node from a survivor so a stolen
  machine's key is dead.
- **Hard rule:** never sit at *zero* nodes with no soul-key backup, or the account is cryptographically
  orphaned. Keep ≥2 nodes, or ≥1 node + an encrypted soul-key backup.
- **Trade-off (accepted):** a co-equal node can enroll further nodes, so a compromised node can add
  nodes. Blast radius ≈ "a compromised device can add devices," same surface as the device itself —
  the right trade for a personal tool where you own every machine. Revocation is the mitigation.

**Platform** is reported from `System.Runtime.InteropServices.RuntimeInformation` at enrollment/connect
(`Windows` / `macOS` / `Linux`).

---

## 3. Server-side changes

### 3.1 Registry: one → many connections per soul

Replace the single-connection maps with a per-soul node table:

```csharp
// Before:  userId → connectionId
// After:   userId → { nodeId → NodeConnection }
record NodeConnection(string NodeId, string Label, string Platform,
                      string ConnectionId, DateTime ConnectedAt);

ConcurrentDictionary<string, ConcurrentDictionary<string, NodeConnection>> _nodes;
```

- `RegisterDirect(userId, connectionId)` → `RegisterNode(userId, nodeId, label, platform, connectionId)`.
- `HasBridge(userId)` stays (any node present). Add `GetNodes(userId)` and
  `TryGetNode(userId, nodeId)`.
- **Default node:** when a request doesn't name a node, pick a deterministic default — the
  most-recently-connected, or a user-pinned "primary". Preserves all current single-node behaviour.
- `SoulVerified` stays keyed per soul (`direct-{userId}`) — any verified node verifies the soul.
- `Unregister(connectionId)` removes just that node; the soul stays verified while ≥1 node remains.

### 3.2 Routing: target a connection by node

**Key simplification:** there is essentially **one chokepoint**. Both LLM calls *and* bridge tool
calls (terminal / MCP) already flow through `ModelBridgeRegistry.SendRequestAsync(userId, request)`:

- LLM: `ModelBridgeHandler.SendAsync` → `SendRequestAsync`.
- Tools: `AgentService.BridgePostAsync` → `SendRequestAsync` (the bridge's `HandleLlmRequestAsync`
  forwards `req.Url` to its local `/llm/proxy`, which is also how `tools/list` / `tools/call` reach
  `localhost:5741`).

So adding an optional `nodeId` to **`SendRequestAsync`** (plus the two callers that build the
request) routes everything. `SendLocalRestAsync` (used only for cogitation REST sync, which is
soul-wide data) can keep using the default node for v1.

`SendRequestAsync` resolves the chosen `NodeConnection.ConnectionId` (fall back to default node).
`_hub.Clients.Client(connId).SendAsync("HandleRequest", …)` is unchanged — it already targets one
connection; it just receives the chosen connection id. If the **named** node is offline, throw a
clear error rather than `WaitForBridgeAsync`-ing for *any* node (see §9.4).

### 3.3 Connect auth + enrollment (two separate flows)

- **Connect (every session):** `RegisterDirectBridge(soulId, nonce, soulSig)` →
  `RegisterDirectBridge(soulId, nodePubB64, label, platform, nonce, nodeSig)`. The node signs the
  nonce with **its own** key; the server accepts iff `nodePubB64` is in that soul's non-revoked
  allow-list (`SoulNodeKey`) **or** matches the soul key itself (the original bridge). Records the
  node, emits `DirectBridgeRegistered(userId)` + new `NodesChanged(userId)` so pickers refresh.
- **Enrollment (once per new node):** a separate `EnrollNode` call (§9.3) adds a node pubkey to the
  allow-list; it must carry a certificate signed by the soul key **or any already-enrolled node**.
- **Revocation:** `RevokeNode(soulId, targetNodePub, sig)` — same {soul ∪ live nodes} signature rule;
  marks the row revoked and drops any live connection for it.

---

## 4. Bridge-side changes (`DirectTunnel`)

- Generate/persist a **node keypair** + label on first run (store on `BridgeSoul`, §9.1). The node
  private key never leaves the machine.
- Report `platform` from `RuntimeInformation`.
- At connect, sign the challenge nonce with the **node** key; the server checks it against the soul's
  allow-list (§3.3).
- Everything else (the `HandleLlmRequestAsync` / `HandleLocalRestAsync` forwarding to
  `localhost:5741`) is unchanged — a node only ever serves its **own** local resources, which is
  exactly the point (the home-PC node reaches the home PC's LLM and files).

**The first bridge** still uses `/soul/link-server` (creates the soul + userId, registers the soul
public key). **Additional bridges do NOT** — they pick "Join existing soul", generate a node key, and
get enrolled (§2 / §9.3) so they attach to the *existing* `serverSoulId` instead of minting a new one.

---

## 5. Linking channels & terminal projects to a node

**Routing unit = one session → one node.** A cogitation runs on a single node: its LLM channel and
all its bridge tools (terminal, MCP) route to the **same** node's `localhost:5741`. This matches the
target use case (home PC has both the LLM *and* the files) and avoids threading a different node per
tool. Mixing tools across nodes within one session is **explicitly out of scope for v1.**

The session's node is resolved (highest priority first):
1. a per-cogitation "run on" override, if set;
2. the active channel's `BridgeNodeId` (a local channel pinned to a node);
3. the default node.

Selecting a terminal **project** that lives on a node is one way the UI can set (1)/(2) — picking a
home-PC project implies the session runs on the home-PC node. The per-project `nodeId` is therefore
a *hint that resolves to the session node*, not an independent per-tool route.

Add an optional `BridgeNodeId` (+ cached `BridgeNodeLabel` for display) to the entities that drive
execution location:

- **Channel (model source):** a local/bridged `ModelSource` gains an optional target node. When set,
  LLM requests route to that node's connection. (Cloud providers are node-agnostic — they egress
  from whichever node injects the key; default node is fine.)
- **Terminal project:** each `AllowedPaths` entry gains `nodeId` + `platform`. The path
  `C:\src\proj` only makes sense on the Windows home PC; binding it to that node means the
  terminal tool calls route there, and `BuildTerminalAddendum` can tailor OS guidance from the
  node's real platform instead of guessing.

UX: a **node picker** (dropdown of `GetNodes(userId)` with label + platform badge) in:
- the Channel panel (per local source),
- the Terminal tool's project editor (per project row),
- optionally a per-cogitation "run on" override in the chat header.

When the active chat resolves its channel/tools, it passes the resolved `nodeId` into
`AgentService.CreateSessionAsync` → through `ModelBridgeHandler` / bridge-tool POSTs → into
`SendRequestAsync(..., nodeId)`. This threads the node from UI choice down to the SignalR dispatch.

Once agent config is custodied (§11.6), the routed node is **also** the source of the decrypted agent
definition — so in the thin-client case the chosen node supplies LLM + tools + the agent's own
config, all on one machine.

---

## 6. Platform-aware agent (sub-feature)

Independently useful: the bridge already reports `platform`. Feed it into the terminal addendum so
the agent adapts shell/path conventions to the **actual** target OS (PowerShell vs bash, `\` vs `/`,
`~` expansion) rather than the current Windows-centric assumption. This is a small, standalone win
that can ship before full multi-node routing — it only needs the node to report its platform and
`BuildTerminalAddendum` to consume it.

---

## 7. Suggested rollout order

1. **Platform reporting + terminal addendum** (§9.3 platform, §9.6) — node reports OS; addendum
   adapts. Small, no routing.
2. **Registry multi-node** (§9.2) — `_nodes` table, default-node fallback. No UI yet; behaviour
   identical for single-node users. De-risks the core data change.
3. **Node identity + enrollment** (§9.1, §9.3) — per-node keypairs, `SoulNodeKey` allow-list,
   connect/enroll/revoke verified against `{soul ∪ live nodes}`, "Join existing soul" + "Add device"
   UX, and the encrypted soul-key backup (§2 hard rule).
4. **Routing param** (§9.4–9.5) — thread `nodeId` through `SendRequestAsync` + `BridgePostAsync` +
   `ModelBridgeHandler` and the offline-node guard.
5. **UI: node picker** (§9.7) — channel + terminal-project binding; per-cogitation override.
6. **Data sync** (§11) — E2E-encrypted multi-master replication of the soul's private dataset across
   nodes. Largest piece; depends on enrollment (the DEK rides enrollment). Ship last.

Steps 1–2 are independently shippable and unlock most of the value with the least risk. Steps 3 and
6 are the two big lifts.

---

## 8. Open questions

> **Resolved in implementation (revision 2):**
> - **Approver confirmation → pairing-code flow.** Devices are no longer enrolled by pasting a node
>   pubkey. A joined bridge (`/soul/join`) registers itself as a *pending device* with a 6-digit join
>   code (`POST /api/bridge/pending-enroll`, `PendingEnrollmentService`); the human approves it from a
>   soul-verified session by typing the code shown on that device (`/api/bridge/approve-enrollment` →
>   `NodeService.ApprovePendingAsync`), which then runs the normal approver-bridge signature. Code +
>   signature both required. The bridge surfaces the code on its status page (`/node/join-code`).
> - **Node-offline / default-node → strict per-channel binding, no default.** A bridged channel
>   (`UserLocalSource.IsBridged`) must be bound to a specific connected node; chat **errors** if the
>   channel is unbound or its node is offline (`Chat.EnsureBridgeBound`) — never falls back to another
>   machine. The channel panel shows a required bridge picker; the top header shows the active
>   channel's bound bridge (`HeaderSoul`). `GetDefaultNode` survives only for node-agnostic paths
>   (cloud key egress + soul-wide REST sync), not for routing a bridged channel's LLM/tools.
> - **Device deletion.** The devices panel ✕ now *fully deletes* the allow-list row
>   (`NodeService.DeleteNodeAsync`, signed like a revoke), so the machine must re-pair to return —
>   it is no longer a soft `Revoked` tombstone.

- **Node liveness / reconnect:** picker shows online/offline; a bridged channel bound to an offline
  node now errors (above). Cloud channels remain node-agnostic.
- **Soul-key backup:** required companion feature (see §2 hard rule) — an encrypted export of the
  soul key for the zero-nodes recovery case. Scope it alongside this work.
- **Soul-key backup:** required companion feature (see §2 hard rule) — an encrypted export of the
  soul key for the zero-nodes recovery case. Scope it alongside this work.
- **Filesystem isolation:** a node only ever serves *its* local files + LLM. Confirm we never imply
  one node can read another node's filesystem (routing is per-session, §5). Cross-node *data* (history,
  agents…) is handled by replication, not remote filesystem access — see §11.

---

## 9. Implementation reference (exact signatures & call sites)

> Identity mapping to keep straight: the registry's `userId` (string) == server `User.Id` ==
> bridge's `BridgeSoul.ServerSoulId` (int). "Soul key" = `User.PublicKey` on the server /
> `BridgeSoul.PrivateKeyBase64` on the bridge.

### 9.1 Data model changes

| Entity | File | Add | Migration |
|---|---|---|---|
| `BridgeSoul` | `Aria.Bridge/Data/BridgeDbContext.cs` | `NodePublicKeyBase64`, `NodePrivateKeyBase64` (per-node keypair, generated once), `NodeId` (= thumbprint of node pubkey), `NodeLabel`, `DataKeyBase64` (the sync DEK, received at enrollment — §11) | bridge DB — match how `BridgeDbContext` is created (EF migration **or** `EnsureCreated`; verify which before relying on auto-add of columns) |
| **`SoulNodeKey`** (NEW server table = the allow-list) | `Aria.Web/Data/` + `AppDbContext` | `Id, UserId, NodeId (thumbprint), NodePublicKeyBase64, Label, Platform, EnrolledByNodeId, EnrolledAt, Revoked (bool), RevokedAt` | EF migration on `AppDbContext` — **heed CLAUDE.md**: keep `__EFMigrationsHistory` in sync (`sqlite3 aria.db "INSERT INTO __EFMigrationsHistory …"`) |
| `UserLocalSource` | `Aria.Web/Data/UserLocalSource.cs` | `string? BridgeNodeId` | same `AppDbContext` migration |
| Terminal project entries | `terminal` tool config `AllowedPaths` JSON (parsed by `AgentService.ParseNamedPaths`) | `nodeId`, `platform` keys | **none** (JSON blob) |

`NodeId = Base64Url(SHA256(nodePublicKey))[..16]` — a short stable routing/display handle derived
from the node pubkey. The **in-memory** registry (`_nodes`, §9.2) tracks *connected* nodes for
routing; **`SoulNodeKey`** is the durable *allow-list* that survives restarts and drives enrollment/
revocation. The picker shows connected nodes (from `_nodes`) and may also list enrolled-but-offline
nodes (from `SoulNodeKey`).

### 9.2 `ModelBridgeRegistry` (`Aria.Web/Services/ModelBridgeRegistry.cs`)

Replace the single-connection map; keep `_connToUser` (the file's comment explains the inversion is
deliberate for reconnect races — do **not** remove it).

```csharp
public record NodeConnection(string NodeId, string Label, string Platform,
                             string ConnectionId, DateTime ConnectedAt);

// userId → (nodeId → NodeConnection). Replaces `_directConns`.
private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, NodeConnection>> _nodes = new();
// keep: _connToUser (connId → userId), _daemonChallenges, _pending, _requestToConn, _pendingRest
```

- `RegisterDirect(userId, connectionId)` → `RegisterNode(string userId, string nodeId, string label, string platform, string connectionId)`:
  upsert into `_nodes[userId][nodeId]`, set `_connToUser[connectionId]=userId`,
  `SetSoulVerified($"direct-{userId}", true)`, fire `DirectBridgeRegistered(userId)` **and** new
  `NodesChanged(userId)`.
- `HasDirectBridge/HasBridge(userId)` → `_nodes.TryGetValue(userId, out var m) && !m.IsEmpty`.
- New: `IReadOnlyCollection<NodeConnection> GetNodes(string userId)`,
  `bool TryGetNode(string userId, string nodeId, out NodeConnection)`,
  `NodeConnection? GetDefaultNode(string userId)` (most-recent `ConnectedAt`, or user-pinned later).
- `FindConnection(userId)` → `ResolveConnId(string userId, string? nodeId)`:
  `nodeId == null` → `GetDefaultNode(userId)?.ConnectionId`; else
  `TryGetNode(userId, nodeId, out var n) ? n.ConnectionId : null`.
- `Unregister(connectionId)`: currently `_directConns.FirstOrDefault(kv => kv.Value == connectionId)`.
  Now scan `_nodes`: find the `(userId, nodeId)` whose `NodeConnection.ConnectionId == connectionId`,
  remove that node, fire `NodesChanged(userId)`. Only when that user's inner dict becomes empty do
  `SetSoulVerified($"direct-{userId}", false)` + `DirectBridgeDisconnected(userId)`. (Soul stays
  verified while ≥1 node remains.) The in-flight `_requestToConn` fail-loop is unchanged.
- `SendRequestAsync(string userId, BridgeRequest request, CancellationToken ct)` →
  add `string? nodeId = null`. Replace `FindConnection(userId)` with `ResolveConnId(userId, nodeId)`.
  See §9.4 for the offline-node case.
- `SendLocalRestAsync` — leave on default node for v1 (`ResolveConnId(userId, null)`).

### 9.3 Auth, enrollment & revocation (co-equal owners)

**The one rule:** an enroll/revoke request is accepted iff its certificate verifies under **any key
in the live set** `{ soul pubkey (User.PublicKey) } ∪ { non-revoked SoulNodeKey.NodePublicKey for this userId }`.
Connect auth proves a node holds a key that is *in that set*.

**Signed-payload layouts** (fixed-length nonce / explicit string joins so both sides match exactly;
all ECDSA P-256 / SHA-256, matching `SoulEndpoints.GenerateKeypair`):

| Action | Signed bytes | Signing key | Verified against |
|---|---|---|---|
| **Connect** | `nonce(32B)` | node private key | `nodePub` ∈ live set (or == soul pub) |
| **Enroll** | `UTF8("enroll\|" + serverSoulId + "\|" + newNodePubB64 + "\|" + label + "\|" + expiryUnix)` | approver key (soul **or** any live node) | approver pub ∈ live set |
| **Revoke** | `UTF8("revoke\|" + serverSoulId + "\|" + targetNodePubB64 + "\|" + nowUnix)` | approver key (soul **or** any live node) | approver pub ∈ live set |

**Connect** — `Aria.Bridge/DirectTunnel.cs::ConnectAndRunAsync` + `Aria.Web/Hubs/ModelBridgeHub.cs`:
- Bridge: generate/load node keypair; after `GetDaemonChallenge`, sign the bare `nonce` with the
  **node** private key; call
  `RegisterDirectBridge(serverSoulId, nodePubB64, label, platform, nonceB64, nodeSigB64)`.
- Hub: change `RegisterDirectBridge` to that signature. Verify `nodeSig` over `nonce` under
  `nodePubB64`; then accept iff `thumbprint(nodePubB64)` is a non-revoked `SoulNodeKey` for `userId`
  **or** `nodePubB64 == user.PublicKey` (the original/soul bridge auto-allowed). Then
  `_registry.RegisterNode(userId, thumbprint, label, platform, Context.ConnectionId)`.

**Enroll** — new endpoint/hub method `EnrollNode(serverSoulId, newNodePubB64, label, platform, approverPubB64, certB64)`:
- verify `approverPubB64` ∈ live set; verify `cert` over the Enroll payload under `approverPubB64`;
  check `expiry` not passed; insert a `SoulNodeKey` row (`EnrolledByNodeId = thumbprint(approverPub)`);
  fire `NodesChanged(userId)`. Idempotent on `(UserId, NodeId)`.

**Revoke** — `RevokeNode(serverSoulId, targetNodePubB64, approverPubB64, sigB64)`: same set check;
mark the row `Revoked`; if that node is currently connected, drop it (`_registry` kills the conn).

**Where the approver signature comes from (UX path):** "Devices → Add device" on a soul-verified
Aria.Web session. The new bridge shows `newNodePub` + fingerprint; the user enters/scans it; the
server asks a **connected approver bridge** to sign the Enroll payload via a new hub call
`SignEnrollment(payload)` pushed down its tunnel (the bridge signs with its node key, optionally
after a local/notification confirm + the human matching the fingerprint shown in the web UI), and
returns `certB64`. No key copying; the soul key is only involved if *it* is the chosen approver
(e.g. the very first additional node, or recovery from backup).

> **Bootstrap note:** the original bridge's node pubkey == the soul-linked identity. Treat
> `nodePub == User.PublicKey` as implicitly allow-listed so the first bridge keeps connecting with
> zero `SoulNodeKey` rows, and seed a `SoulNodeKey` row for it on first connect so it appears in the
> device list and can be revoked like any other.

### 9.4 Node-offline behavior

`SendRequestAsync` today calls `WaitForBridgeAsync(userId, 30s)` when no bridge is present. With a
**named** node:
- if `nodeId == null` → keep current behavior (wait for any/default node).
- if `nodeId != null` and `ResolveConnId` is null → **throw immediately** with a clear message
  (`"Node '{label}' is offline"`). Don't wait for a different node — that would silently run on the
  wrong machine. (Optional later: `WaitForNodeAsync(userId, nodeId, timeout)`.)

### 9.5 Threading `nodeId` from UI → dispatch

The session's node is resolved in `Chat.razor.cs` when building the session (both `InitAgentAsync`
and the reopened-cogitation path), right where `sourceName`/`modelId` are resolved:

```
resolve nodeId = perCogOverride ?? localSourceFor(sourceName)?.BridgeNodeId ?? null
```

Then pass it down the existing chain (each hop adds one optional `string? bridgeNodeId = null`):

- `AgentService.CreateSessionAsync(… , string? bridgeUserId, int? userId, …)` → add `bridgeNodeId`.
- → `BuildChatClient(… , string? bridgeUserId, …)` → add `bridgeNodeId`; pass to the handler ctor.
- → `ModelBridgeHandler(registry, userId, keyRef, requireKey)` → add `string? nodeId`; pass to
  `_registry.SendRequestAsync(_userId, bridgeReq, ct)` as the new arg.
- **Tools:** `AgentService.BridgePostAsync(string userId, string url, string body, CancellationToken ct)`
  → add `string? nodeId`; pass to `SendRequestAsync`. Callers `LoadBridgeToolsAsync` /
  `PrewarmBridgeSessionsAsync` thread the session's node so terminal/MCP tools hit the same machine.

`ModelSource` (`Aria.Agent/Helpers/ModelSource.cs`) is the runtime shape loaded from
`UserLocalSource`; carry `BridgeNodeId` onto it in `NavMenu.razor.cs` where `SetUserLocalSources`
is populated, so `Chat.razor.cs` can read it from the resolved source.

### 9.6 Platform-aware terminal addendum (§6, standalone)

`AgentService.BuildTerminalAddendum` (and the `terminal` case in `CreateSessionAsync`) currently
hard-codes Windows hints. Pass the resolved node's `Platform` in and branch the guidance
(`bash`/`/` vs `cmd.exe`+`powershell`/`\`, `~` expansion). Ships without any routing work — only
needs the node to report platform (§9.3) and the addendum to consume it.

### 9.7 UI & events

- New registry event `event Action<string>? NodesChanged;` (arg = userId). `Chat.razor.cs` already
  subscribes to `DirectBridgeRegistered`/`SoulStatusChanged` in `OnInitializedAsync`/`Dispose` —
  add `NodesChanged` the same way for any node picker.
- Node picker = dropdown over `BridgeRegistry.GetNodes(userId)` (label + platform badge + online
  dot). Place in: Channel panel (per local source → sets `UserLocalSource.BridgeNodeId`), Terminal
  project editor row (sets the project's `nodeId`/`platform`), optional per-cogitation header override.

---

## 10. Backward-compatibility invariants (do not break single-node users)

1. Every new parameter is **optional/nullable**; `nodeId == null` ⇒ default node ⇒ **exactly today's
   behavior**.
2. A bridge that still sends the old 3-arg `RegisterDirectBridge` must keep working *or* be updated
   in lockstep — pick one and state it in the PR (the bridge and server ship together here).
3. `SoulVerified`/`IsSoulVerified` semantics are unchanged (per-soul, true while ≥1 node connected).
   The whole identity gate in `Chat.razor` (`SoulVerified`) keeps working untouched.
4. `SendLocalRestAsync` (cogitation REST sync) stays on the default node — it's soul-wide data.
5. **No soul access without a connected bridge.** `SoulVerified` gates the entire UI; there is *no*
   server-side login independent of a connected, soul-verified bridge. The §11.6 custody model relies
   on this — decrypted config lives in server RAM only while a bridge is connected, and "no bridge ⇒
   locked out" is already today's behavior, so it's not a regression.

---

## 11. Data sync across nodes (lose a PC, keep your data)

> Goal: a soul's private dataset — cogitation history, sub-agents, skills, hives, contacts — lives on
> **every** node, so losing one machine loses nothing. Builds directly on the enrolled-node fabric.

> **Implementation progress (Phase 6):**
> - **6a — key fabric ✅** `Aria.Shared/SyncCrypto.cs` (AES-256-GCM records + ECDH-wrapped DEK, reusing
>   the node P-256 signing keys for key agreement); `BridgeSoul.DataKeyBase64`; primary mints the DEK on
>   tunnel connect; additional nodes get it ECDH-wrapped at enrollment (`/node/sign-enrollment` →
>   `SoulNodeKey.WrappedDek`) and unwrap on first connect (`ModelBridgeHub.GetWrappedDek`); DEK rides
>   soul export/import for recovery. Round-trip + wrong-key rejection verified.
> - **6b — server relay table ✅** `SyncRecord {UserId, EntityType, EntityId, UpdatedAt, Deleted,
>   LastWriterNodeId, CipherBlob}` + unique `(UserId,EntityType,EntityId)` and `(UserId,UpdatedAt)`
>   indexes. Server stores `CipherBlob` opaquely.
> - **6c — bridge mirror tables** ⬜ (next; build-testable, no live test)
> - **6d — sync protocol** ⬜ `PullSince`/`Push`, delta on connect+change, LWW — **needs live 2-node test**
> - **6e — Option A custody** ⬜ per-connection RAM cache; `CreateSessionAsync` reads it — **needs live test**
> - **6f — plaintext→ciphertext migration** ⬜ — **needs live test, sequence carefully**

### 11.1 Where the data is today (and the gap)

- **Cogitations + messages**: already mirrored server→bridge (`BridgeCogitationClient` writes
  `BridgeCogitation`/`BridgeMessage` on every send). So history is *already* two-homed (server + the
  active bridge) — but not fanned out to *other* nodes.
- **Sub-agents, skills, hives, tool states, local channels**: **server-only** today
  (`AppDbContext`: `SubAgent`, `SubAgentSkill`, `AgentCollective`, …). The bridge DB has no tables
  for them. **This is the gap** — to make them survive losing the server's host machine, they must
  join the replicated set, not live solely server-side.

So sync has two halves: (a) a replication mechanism, and (b) widening the custodied dataset to
include agents/skills/hives (new bridge-side tables mirroring the server entities).

### 11.2 Trust model: server is a dumb **encrypted** relay (preserves key-custody)

The whole point of bridge custody is that the **server cannot read** soul-private data. So sync must
be **end-to-end encrypted**: nodes exchange ciphertext; the server only stores/fans-out opaque blobs.

- A per-soul **Data Encryption Key (DEK)** — symmetric (e.g. AES-256-GCM), distinct from the soul
  *signing* key — encrypts every synced record.
- The DEK is generated by the first bridge and **delivered to each new node at enrollment**, wrapped
  to that node's public key (ECDH/ECIES to the node pubkey already in the Enroll payload). The server
  relays the wrapped DEK but never sees it in the clear. (`BridgeSoul.DataKeyBase64`, §9.1.)
- Result: enrolled nodes can decrypt the shared dataset; the server (and any non-enrolled party)
  cannot. Revoking a node should ideally rotate the DEK (re-wrap to remaining nodes) so a revoked
  machine can't read *future* data — list as an enhancement; v1 may accept that revoked nodes keep
  the old DEK for already-synced data (they had local plaintext anyway).

### 11.3 Replication mechanism (multi-master, last-write-wins)

Per-record change tracking on every synced entity:
- `UpdatedAt` (UTC) — several entities already have it (`BridgeCogitation.UpdatedAt`); add where
  missing.
- `Deleted` tombstone flag (don't hard-delete; propagate the deletion).
- `LastWriterNodeId` — tiebreaker for simultaneous edits.

Conflict resolution = **LWW** by `(UpdatedAt, LastWriterNodeId)`. Good enough for a personal,
rarely-concurrent dataset; CRDTs are overkill. (Messages are append-only by `Id` — trivially
mergeable; only mutable rows like agent config / titles ever conflict.)

**Topology — server as always-on rendezvous (recommended):**
- The server keeps a per-soul store of the latest **encrypted blob per record** + its `UpdatedAt`
  (it already persists cogitations; extend to an opaque `SyncRecord { UserId, EntityType, EntityId,
  UpdatedAt, Deleted, CipherBlob }` table — server reads none of `CipherBlob`).
- On connect (and on change), a node runs a delta sync against the server: "give me everything with
  `UpdatedAt >` my cursor", applies newer remote rows (decrypt → upsert locally), and pushes its own
  newer rows (encrypt → send). The server fans out by simply holding the union; offline nodes catch
  up whenever they next connect.
- Rides the existing tunnel/hub — add `PullSince(cursor)` / `Push(records)` hub methods, or a
  dedicated `/sync` REST over `SendLocalRestAsync`.

This makes the **server a durable replica too** (of *ciphertext*), which is what guarantees recovery
even if only one node ever existed before the loss: a freshly enrolled replacement node pulls the
entire encrypted dataset from the server and decrypts it with the DEK it got at enrollment.

### 11.4 The "lose a PC" guarantee

With the above: data survives losing any single participant as long as **one other replica holds it**
(another node, or the server's encrypted store). Pairs with the §2 device rule: keep ≥2 nodes *or*
≥1 node + soul-key backup, and the encrypted server store gives a third copy for free.

### 11.5 Open questions / scope flags

- **Biggest lift:** widening the custodied dataset (agents/skills/hives → bridge-side tables +
  server `SyncRecord`). Sequence it *after* node enrollment (the DEK depends on it).
- **DEK rotation on revoke** — enhancement vs v1 acceptance (above).
- **Blob granularity** — per-row blobs (simple, more rows) vs per-table snapshots (fewer requests,
  coarser conflicts). Per-row recommended for LWW.
- **Server storage cost / retention** — encrypted history grows; consider per-soul caps or
  node-driven pruning (tombstone GC once all known nodes have acked).
- **Schema migrations across nodes** — nodes on different app versions syncing; version the
  `SyncRecord`/entity schema and tolerate unknown fields.
- **Could the server be skipped?** Pure P2P (node↔node via the hub relay) avoids server storage but
  loses the always-on rendezvous; the encrypted-server-store model is simpler and more robust. Prefer
  it unless storing even ciphertext server-side is unacceptable.

### 11.6 Custody of agent / tool / skill / hive config (chosen: Option A)

Today these live **plaintext** in the server `AppDbContext` (`SubAgent`, `SubAgentSkill`,
`SubAgentToolState`, `AgentCollective`/`CollectiveMember`/`MemberEdgeNode`, `UserLocalSource`).
A user puts personal data in them (directives, machine paths, names), so they shouldn't sit in clear
text on the server. They join the encrypted `SyncRecord` set (§11.2–11.3): **server stores ciphertext
only; plaintext lives on nodes.**

**Decryption model — Option A (server-side just-in-time, chosen):** the server still orchestrates the
agent loop (`AgentService.CreateSessionAsync`), so it needs plaintext at session-build time. Since the
whole UI is already gated behind a connected, soul-verified bridge (§10.5), the cleanest delivery is:

- On connect, each node pushes the **decrypted** soul-private config (agents/skills/hives/tool
  states/local channels) over its authenticated tunnel into a **per-connection in-memory cache** on
  the server (`IMemoryCache`/a scoped dictionary keyed by userId — **never persisted**).
- `CreateSessionAsync` and the agent/hive UIs read config from that in-memory cache instead of the DB.
- When the bridge disconnects, drop the cache. (You're locked out at that point anyway — §10.5 — so
  there's no regression.)

This keeps today's server-orchestrated loop intact, gives **at-rest custody** (nothing personal in the
server DB), and limits server plaintext exposure to **RAM, only while a bridge is connected**. The
node that a session is routed to (§5) is naturally the same node that supplied that decrypted config,
so config + LLM + tools all converge on one machine in the thin-client case.

- **Option B** (move the agent loop onto the bridge → zero server plaintext) is the clean future
  upgrade; out of scope now (large rewrite of where the loop runs).
- **Option C** (DEK in the browser/WASM, decrypt client-side) is rejected for v1 — puts the DEK in an
  ephemeral context and reworks the verification gate for little gain over A.

**Migration:** existing plaintext rows get encrypted in place on first run of an enrolled node (it has
the DEK), then the server columns are cleared to ciphertext-only. Sequence this carefully so a
half-migrated DB never strands a user's agents.

---

## 12. Per-browser authentication & data-exposure hardening

> **Why this section exists:** the original gate was *server-global* — `SoulVerified` keyed by
> `direct-{userId}`, set true whenever **any** bridge for that soul connects. Consequence (observed):
> a second machine's browser pointed at the server over the LAN selects the soul and sees all its
> data, because the server's *own* bridge is connected — the browser never proved anything. The
> server also binds `0.0.0.0` with **no auth middleware**, so several `/api/*` endpoints leak data
> regardless of the UI. This is the security model for closing that, in three steps. **Steps 1–2 are
> the real fix and are implemented; step 3 is the future hardening that makes it robust against host
> compromise.**

### Threat model
Personal tool on a home LAN. Adversary = another device/person on the same network who can reach the
server (`:5129`) and the hub. **Not** in scope: an attacker with shell/DB access on the machine that
runs the bridge (that machine is the trust root — anything on its loopback can sign). The bridge
binds **loopback-only** (`127.0.0.1:5741`), so a LAN adversary cannot reach the signing endpoint and
therefore cannot forge the proof below.

### Step 1 — per-circuit attestation via the browser's own local bridge ✅
Verification becomes **per-browser-circuit**, keyed `circuit-{sessionToken}-{userId}` (was global
`direct-{userId}`):
1. Each Blazor circuit holds a unique `UserSessionState.SessionToken` (GUID).
2. On soul selection the circuit issues a single-use nonce (`CircuitAuthService.Begin`) and asks the
   browser to relay a sign request to **its own** local bridge (`ariaInterop.attestViaLocalBridge` →
   `POST http://127.0.0.1:5741/node/attest`), which signs `attest|{userId}|{token}|{nonce}` with that
   machine's **effective key** (node key, or the soul key on the primary).
3. `CircuitAuthService.Complete` verifies the signature against soul `userId`'s live set
   `{ soul pubkey ∪ non-revoked SoulNodeKey }` and only then sets `SoulVerified(circuit-token-userId)`.
4. The UI gate (`NavMenu`/`Chat`/`HeaderSoul`) reads that per-circuit key.

Result: a browser unlocks **only** if its *own* machine runs a bridge enrolled for the selected soul.
A PC browser hitting the Mac server with no/unenrolled PC bridge → `127.0.0.1:5741` unreachable or key
not in the set → stays locked → onboarding. This also closes the **soul-picker** gap: you can only
view souls your local bridge actually holds. The signature binds `nonce|token|userId` (single-use,
in-process nonce) so a proof can't be replayed onto another circuit/soul. The direct tunnel is
unchanged and still does all **routing**; this step only governs **UI identity**.

> **Mechanism note:** implemented as a one-shot `IJSRuntime` fetch to the local bridge rather than
> reviving the streaming WASM relay (`reference/wasm-relay`). Same browser→local-bridge proof and same
> CORS/PNA surface, but no second render mode / WASM project to maintain. Loopback `http://127.0.0.1`
> is exempt from mixed-content blocking; Chrome **Private Network Access** preflight is the one live
> risk to validate (bridge CORS is already `AllowAnyOrigin`).

#### Step 1b — secure-context limit & the session-code fallback ✅
The automatic loopback fetch only works from a **secure context** (`https://` or `localhost`).
Chromium/Edge **block private-network/loopback requests from insecure contexts entirely**, so a remote
browser loading the server over plain **`http://LAN-IP`** (the common dev/LAN setup) can never
auto-attest — the PNA preflight shim can't rescue an insecure initiator. Confirmed live: the Mac
(localhost) unlocks, a PC over `http://192.168.x.x` does not.

Two ways out; we ship the second so HTTPS isn't mandatory:
- **Serve Aria.Web over HTTPS** → every client is a secure context and auto-attest works everywhere
  (cleanest, but needs a LAN-trusted TLS cert on each client).
- **Manual session-code pairing (implemented, self-identifying).** Each bridge process exposes a stable
  `GET /node/session-code` (8 chars, 32-symbol unambiguous alphabet ≈ 40 bits) shown on its **local
  status page** (`http://localhost:5741`, always a secure localhost context the user *can* reach on
  their own machine). The locked Aria.Web panel offers an "enter session code" form; the user pastes the
  code; `CircuitAuthService.UnlockByCodeAsync` searches **every** connected bridge across all souls
  (`registry.AllNodes()` → `SendLocalRestAsync(uid, …, nodeId)`) and the bridge whose live code matches
  identifies which soul this browser belongs to. On match it unlocks that soul **for this circuit** and
  returns its userId, and the UI auto-switches to it. **Reading the code requires being at that machine's
  localhost — that is the co-location proof**, equivalent to the loopback fetch.

  > **Why self-identifying matters (bug found live):** on an insecure page the browser can't query its
  > own loopback bridge to discover *which* soul is local, so the soul picker defaulted to the first soul
  > with any connected bridge — which on a host that runs both the server and a bridge is the *server
  > owner's* soul (e.g. picking "JL" on a PC whose bridge holds "JL-Windows"). Per-circuit gating still
  > kept it **locked** (no data leak — verified by audit: all gates use `CircuitAuth.IsVerified`), but it
  > was confusing and tried to match against the wrong machine's bridge. Making the code resolve the soul
  > removes manual soul-selection on remote machines entirely.

  Rate-limited to 10 tries/min per circuit. `aria-interop.js` auto-detects `window.isSecureContext` and
  the form text adapts (primary path on insecure pages, fallback hint on secure ones). The bridge keeps
  the PNA preflight shim for the HTTPS path. Also fixed in this pass: `BridgeGatewayModal` was still
  reading the dead server-global `IsSoulVerified(direct-{userId})`; it now reads the per-circuit gate.

### Step 2 — enforce the gate on every path, shrink the surface ✅
Per-circuit UI gating is necessary but **not sufficient** on its own: endpoints that don't go through
the circuit must not leak. The browser UI talks to its services **in-process** (not via `/api/*`), so
the data-bearing REST endpoints were either unused by the UI or duplicated it. Hardening:
- **Removed** the browser-redundant, unauthenticated read/act endpoints that bypassed the gate
  (`GET /api/bridge/nodes`, `GET /api/bridge/pending-enrollments`, `POST /api/bridge/approve-enrollment`
  — the latter was a 6-digit brute-force hole —, `request-enroll`, `request-revoke`, `delete-node`,
  `/api/debug/soul-status`). These are all reachable from the soul-verified circuit in-process instead.
- **Kept** the genuinely machine-to-machine endpoints, which already carry their own proof:
  `enroll-node`/`revoke-node` (approver signature), `pending-enroll` (open by necessity — a new bridge
  isn't enrolled yet; gated downstream by the human + join code + approver signature), `register-soul`
  / `unlink-*` (onboarding / signature), OAuth `auth/*`, `vox/transcribe`.

### Step 3 — end-to-end encryption of soul data (future; the robust step) ⬜
Steps 1–2 are *authorization* — solid for the LAN/personal threat model, but the data still sits
**plaintext** in the server DB, so a missed gate or theft of `aria.db` exposes everything. The
architecturally robust step is to make access **cryptographic, not just gated**:
- Adopt the §11 fabric: soul-private data is stored **ciphertext-only** on the server (`SyncRecord`),
  encrypted under the per-soul **DEK** that only enrolled nodes hold (delivered ECDH-wrapped at
  enrollment). The server becomes a dumb encrypted relay (§11.2).
- Custody model = §11.6 **Option A**: each connected node pushes the **decrypted** config into a
  **per-connection in-memory cache**; `CreateSessionAsync` and the UIs read that cache, never the DB;
  the cache is dropped on disconnect. So server plaintext exists only in RAM, only while a bridge is
  connected — and is naturally scoped to the attested circuit's node from step 1.
- Net effect: even with a missed authorization check or a stolen DB file, an adversary gets only
  ciphertext; reading it requires a node private key, which never leaves a machine. This upgrades the
  guarantee from "the server won't *serve* you data unless authorized" to "the server *cannot* read
  the data at rest." Sequence after §11 (the DEK fabric) lands; pair with the §11.6 plaintext→cipher
  migration so a half-migrated DB never strands a user.
