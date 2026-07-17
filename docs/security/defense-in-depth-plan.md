# Defense-in-Depth Plan

_Consolidated design for the next security layers, on top of the perimeter fixes already shipped
(see [server-hosting-review.md](server-hosting-review.md)). Written 2026-07-09._

This plan adds two **approval-on-new-context** layers, both built on the same primitive: a
**node-signed grant that the server cannot forge**, approvable at **any** of a soul's nodes, and
**mesh-replicated** to the others. It deliberately does **not** use IP as an enforcement gate — see
[§6](#6-why-not-ip-as-a-gate).

- [1. Threat model & what each layer defends](#1-threat-model)
- [2. What already exists (reuse, don't rebuild)](#2-what-already-exists)
- [3. Layer A — device-based browser trust](#3-layer-a--device-based-browser-trust)
- [4. Layer B — server→bridge request gating](#4-layer-b--serverbridge-request-gating)
- [5. Shared primitive — node-signed context grants + mesh replication](#5-shared-primitive)
- [6. Why not IP as a gate](#6-why-not-ip-as-a-gate)
- [7. Data model](#7-data-model)
- [8. Failure modes & fail-closed policy](#8-failure-modes)
- [9. Phasing](#9-phasing)

---

## 1. Threat model

| Adversary | Has | Current defense | Gap this plan closes |
|---|---|---|---|
| **Stolen browser credential** (guest code / `aria-trusted` cookie) | A bearer token, from any network | Coarse access gate (IP/cookie); soul data still key-gated | Bearer token alone lets them into the *app tier* from anywhere → **Layer A** binds trust to a node-approved device |
| **Compromised hosted server** | Full control of the server, the tunnel it speaks on, and every request it pushes to your bridge | Bridge trusts server-relayed `HandleRequest`/`HandleLocalRest` and runs them with local authority (keys, files, shell, MCP, memory) | Server can drain keys / read files / run commands unattended → **Layer B** makes the *bridge* independently gate sensitive relayed ops behind a node-signed grant the server can't produce |
| **Header spoofer** | Ability to set `X-Forwarded-For` | ✅ Fixed — `Fly-Client-IP` first | — |
| **Rogue tunnel socket** | A SignalR connection, guessed request GUID | ✅ Fixed (LLM + REST) — connection-ownership check | terminal PTY still open (tracked) |

Core invariant, unchanged and never weakened by anything below: **soul-scoped data unlocks only on
ECDSA proof of key possession.** IP matches and device trust grant the *coarse app tier* only.

---

## 2. What already exists

Both new layers are assembled from parts already in the tree — this is mostly *generalizing* and
*wiring*, not new crypto.

| Part | Where | Reused for |
|---|---|---|
| **Inquisitorial Seal** — server sends nonce → node shows local approval page → human approves → node signs → server verifies against soul key | `SealService.RequestSealIdAsync`, `Aria.Bridge/Endpoints/SealEndpoints.cs`, `ActionDescriptor` | Layer B's approval ceremony, verbatim |
| **`aria-trusted` cookie** — set after bridge control is proven, 90-day, DataProtection-encrypted | `CircuitAuthService.AppendTrustedCookie`, `AccessGateMiddleware.TryValidateTrustedCookie` | Layer A's device credential (upgraded to node-approved) |
| **Co-equal node approval** — soul key OR any non-revoked node may authorize | `BridgeNodeEndpoints.ApproverInSet`, `SoulNodeKeys` | "any bridge can approve" for both layers |
| **Mesh replication** — every node exports, server relays ciphertext, every node imports | `KeyReplicationService.ReplicateAsync` (`/keys/sync-export` ↔ `/keys/sync-import`) | replicating grants to all up nodes |
| **Governance classifier / GovernedTool** — per-call policy, sensitivity, gate/seal escalation | `Aria.Harness/Governance/` | Layer B's "is this operation sensitive?" decision |
| **Node crypto** | `NodeCrypto.Verify/Thumbprint`, `SoulNodeKeys.WrappedDek` | grant signatures + per-node encryption |

---

## 3. Layer A — device-based browser trust

> **Status: ✅ complete (2026-07-09).** Device-id cookie, node-signed grant storage, access-gate
> integration, the approval + revoke endpoints, **co-equal any-node approval**, and the in-app "trust
> this browser" button all work; 18/18 tests green. Cross-node replication is **not needed** here (the
> `TrustedDevices` table is server-side, shared by all nodes). Optional future polish only: folding the
> legacy `aria-trusted` cookie into the device id. See [§9.2](#92-layer-a-2026-07-09).

**Goal:** a stolen guest code or copied cookie is useless from an unapproved device. Trust attaches
to a **device the node approved once**, and survives IP changes (so roaming/domestic-IP churn never
locks you out — that concern is handled here, not by IP allow-listing).

**Model:** replace the "prove-control-once → 90-day bearer cookie" with a **device credential the
node signs**:

1. First visit from an unrecognized browser → issue a random **device id** (cookie), mark it
   *pending*. The browser can see the public app shell but soul actions stay locked (unchanged).
2. To trust the device, the human approves it **at any connected node** — reuse the Seal ceremony
   with an `ActionDescriptor` of `{ tool: "trust-device", argsPreview: <device label + resolved
   Fly-Client-IP as a hint>, reason }`. The node signs the device id + expiry.
3. Server verifies the signature against the soul key (exactly as `SealService` does today) and
   records the device as trusted (see [§7](#7-data-model)). The approval **mesh-replicates** so every
   node knows this device is trusted.
4. `AccessGateMiddleware` gains a `TryValidateTrustedDevice` check: a device-id cookie whose signed
   grant is on record (and unexpired, unrevoked) passes the gate — *regardless of IP*.

**IP's role here (soft):** the resolved `Fly-Client-IP` is stored alongside the grant and shown in
the approval prompt ("approve laptop from 82.x.x.x?"). A later request from a *known device* on a
*new* IP is allowed, but can optionally raise a re-confirmation (adaptive step-up), never a hard
block. The IP is a hint and an anomaly signal, not a gate.

**Relationship to the knock:** the existing 60 s bridge knock (`DirectTunnel.KnockLoopAsync`) stays
as the *network-scoped* convenience tier ("trust the network my live verified bridge sits on"). Layer
A is the *device-scoped* tier that also covers roaming. They compose; neither unlocks soul data.

**Layer A gates browsers, not bridges — headless nodes are unaffected.** A daemon-only PC (a second
node of the soul with no browser) connects to the server as a *bridge*: outbound SignalR,
ECDSA node-key auth (`RegisterDirectBridge`), on the `/api/bridge` + `/api/modelbridge` paths that
`AccessGateMiddleware` **whitelists and skips**. It sets no device cookie, is never evaluated by the
device gate, and needs no browser or device approval — it just routes work, authenticated by its
enrolled node key. Device trust has no bearing on a headless node. So a two-PC setup (browse on one,
daemon-only on the other) is unaffected by Layer A.

---

## 4. Layer B — server→bridge request gating

> **Status: ✅ live and enforced by default (foundation 2026-07-09 · per-session 2026-07-11 · Phase 2 complete 2026-07-14).** The choke-point gate, the sensitivity classifier, the `ContextGrants` store, the local approval page, **per-session** grant scopes, **reactive in-chat approval** with approval-node pinning, and secure co-equal approval (locally-verified sibling roster) all exist and are wired into both `HandleLocalRestAsync` and the streaming path. Classification is **body-aware** for `/tools/call` (reads don't prompt), and grants are **node-signed and mesh-replicated** across a soul's nodes. Enforcement is **ON by default**, owned by a per-node toggle in the bridge `// Security` tab (never the server). Unattended vigils and Hive collectives are pre-authorised with scoped, replicating seals. Remaining-work tracker: [phase2-context-grants-remaining.md](phase2-context-grants-remaining.md) (all items landed). See [§9.3](#93-layer-b-foundation-2026-07-09).

**Goal (the important one):** the bridge stops blindly executing whatever the server pushes. A
compromised server cannot silently drain keys, read local files, run shell, call MCP tools, or read
memory — because those operations require a **node-signed grant the server can't forge**.

Today every server push lands in `DirectTunnel.HandleLocalRestAsync` (and `HandleRequest` for LLM)
and is executed against the local endpoints (`/llm/proxy`, `ProjectFileEndpoints`, `ToolEndpoints`,
`BuiltinTools` shell/file, memory) with full local authority and **no origin check**.

**Design — bridge-side gate at the tunnel boundary:**

1. **Classify sensitivity locally, on the bridge.** In `HandleLocalRestAsync`, map the request path
   to a sensitivity class (reuse the Harness governance taxonomy where possible):
   - *Benign / read-only-safe* (health, key-presence check, format probes) → run as now.
   - *Sensitive* (`/llm/proxy` with a real key, file **write**/read outside a granted scope, shell
     exec, MCP `tools/call`, memory read/write) → require a **valid context grant**.
2. **Context grant.** A grant is a node-signed token bound to a *context* — e.g. the active soul +
   session/device that legitimately initiated work — with a TTL. If a matching unexpired grant is
   present in the bridge's local vault, sensitive ops proceed without prompting.
3. **New/unrecognized context → Seal, or block.** No grant → the bridge runs the **Seal ceremony
   locally** (it already hosts `/seal/*`): opens the approval page, waits for the human. On approval
   the node signs a grant for that context; on timeout/reject the request is **refused** (returns a
   synthetic error up the tunnel, matching the governance "refuse, don't throw" convention). Default
   is **fail-closed**.
4. **Any node approves; grant mesh-replicates.** Approval can happen at any of the soul's nodes
   (co-equal model). The signed grant replicates node-to-node via the existing mesh
   (`/context-grants/sync-export` ↔ `/sync-import`, modeled on `KeyReplicationService`) so a second
   machine doesn't re-prompt.

**Headless / browser-less node (e.g. a 2-node soul where you browse on one PC and the other runs the
daemon only).** The node that needs the grant is **not** where the human is, so the approval must not
happen there. Route the Seal to the node the human is actually using (the browser PC) and replicate
the signed grant to the headless node — which never opens a page. This works with **what is already
distributed today**: only the **primary** node can sign with the soul key ("primary signs with the
soul key", `NodeEndpoints`), and every secondary already holds the **soul public key**
(`BridgeSoul.PublicKeyBase64`). So:
- v1 (no new plumbing): grants are **soul-key-signed at the primary** (the browser PC) and verified
  by any secondary against the soul public key it already holds.
- Extension ("a *secondary* can also approve"): requires distributing the **sibling node public-key
  roster** to each node (the bridge has no local sibling-key table today — only its own keypair + the
  soul pubkey + its `WrappedDek`). Defer unless needed.

If a sensitive **new** context is routed to a headless node while no browsable/approver node is up, it
**fails closed** and waits — consistent with "the bridge is load-bearing."

**Why this actually defends against a compromised server:** the grant and the Seal signature are
made with the **soul/node private key**, which never leaves the node. The server can relay bytes but
cannot manufacture a signature that verifies — same guarantee the Seal already gives for
Paranoid-mode high-stakes tools, now generalized to *any sensitive relayed op in an unrecognized
context*.

**Granularity knob (policy):** how wide a grant is (per-session vs per-tool-class vs per-path) and
its TTL are policy, tunable per governance mode. Paranoid = narrow + short + re-Seal high-stakes each
time; Standard = per-session grant covering routine ops. Start coarse (per-session) to avoid prompt
fatigue, tighten under Paranoid.

---

## 5. Shared primitive

> **Status: ✅ built and in use (2026-07-09).** The signing/verification core, the ceremony wrapper,
> and a shared canonical payload (`Aria.Shared/GrantCanonical.cs`) exist and are unit-verified.
> Consumed by **Layer A** (device grants, server-verified) and **Layer B** (context grants,
> bridge-signed + verified + mesh-replicated). See [§9](#9-phasing).

Both layers reduce to one reusable mechanism — build it once:

```
Node-signed grant:
  payload = { grantType, subjectId, contextId, expiryUnix }      // subjectId = deviceId or sessionId
  signature = ECDSA_sign(soulKey, canonical(payload))            // only a node can produce this
Approval:
  any connected node runs the Seal ceremony → returns signature
Verification:
  server (Layer A) or bridge (Layer B) verifies via NodeCrypto.Verify against the soul public key
Replication:
  export signed grant → server relays (can't read/forge) → import into every other up node
Revocation:
  drop the grant row + mesh-replicate the tombstone; reuse the node-revocation path
```

`grantType` distinguishes `trust-device` (Layer A, verified server-side) from `context` (Layer B,
verified bridge-side). Everything else — signing, ceremony, mesh, revoke — is shared.

---

## 6. Why not IP as a gate

Recorded here so it isn't re-proposed:

- **Layer A:** IP churns (mobile/CGNAT/VPN) → false lockouts; and it doesn't survive roaming. A
  node-approved *device* does. IP is kept only as a display hint + soft step-up signal.
- **Layer B:** the **server supplies the origin IP** to the bridge, and the server is the adversary
  in this direction. It would simply label malicious requests with an approved IP. A field the
  attacker controls is worth zero as a gate. The node-signed grant is controlled by the *node*, so it
  actually holds.

Principle: **a layer whose secret is held by the adversary is not a weak layer, it's a no-op.** Spend
the effort on the key-bound grant, which is a strong layer in the same spot.

---

## 7. Data model

**Server (`AppDbContext`)** — new `TrustedDevices` (Layer A):

| Column | Notes |
|---|---|
| `UserId` | soul |
| `DeviceId` | random, matches the browser cookie |
| `Label`, `LastIp` | human hint (IP is display only) |
| `SignatureBase64` | node signature over `{deviceId, expiry}` |
| `ApprovedByNodeId` | which node signed |
| `ExpiresAt`, `Revoked`, `RevokedAt` | lifecycle (mirror `SoulNodeKeys`) |

**Bridge (`BridgeDbContext`)** — new `ContextGrants` (Layer B): `ContextId`, `GrantType`,
`ExpiryUnix`, `SignatureBase64`, `ApprovedByNodeId`, `Revoked`. Exported/imported as signed blobs by
the new mesh endpoints.

No change to the core `Users.PublicKey` / `SoulNodeKeys` trust anchor.

---

## 8. Failure modes

- **No node up → nothing works.** Accepted and intended: the bridge is load-bearing infrastructure.
  Both layers therefore fail *closed* with a clear "connect a node to approve" state, not a silent
  denial.
- **Node up but human absent → sensitive ops block for MaxWait (3 min, as Seal today), then refuse.**
  Benign/read ops keep working.
- **Prompt fatigue.** Mitigated by per-session context grants + mesh replication (approve once per
  device/session across all nodes). Only genuinely new contexts or Paranoid high-stakes re-prompt.
- **Grant theft.** Grants live in the bridge vault / server DB like other secrets; they're
  soul-scoped and node-signed, and revocable via the existing revoke+mesh path.
- **Clock skew** on TTLs — use generous windows and UTC, as the existing challenge stores do.

---

## 9. Phasing

1. **Primitive first** ([§5](#5-shared-primitive)) — generalize `SealService`/`ActionDescriptor` to
   emit and verify signed grants; add the grant tables ([§7](#7-data-model)); add a
   `GrantReplicationService` cloned from `KeyReplicationService`.
   - **◐ In progress.** *Done:* the signing/verification core and ceremony wrapper (see
     [§9.1](#91-what-was-built-2026-07-09)). *Remaining:* grant tables + `GrantReplicationService`.
2. **Layer A** — device-id cookie + `trust-device` Seal + `AccessGateMiddleware.TryValidateTrustedDevice`;
   demote the raw `aria-trusted` cookie to carry a device id. Ship behind a config flag; verify a
   stolen cookie from a new device is rejected until approved.
   - **✅ Complete**, incl. co-equal any-node approval and the in-app button (see
     [§9.2](#92-layer-a-2026-07-09)). *Optional future polish:* fold `aria-trusted` into the device id.
     (Cross-node replication not needed — `TrustedDevices` is server-side.)
3. **Layer B** — sensitivity classifier + grant check in `DirectTunnel.HandleLocalRestAsync`; local
   Seal-on-miss; mesh endpoints. Start with a **conservative** sensitive set (`/llm/proxy`, shell,
   file-write) and per-session grants to limit prompts; widen under Paranoid.
   - **◐ Foundation + body-aware classification + node-signed, mesh-replicated grants, enforcement
     opt-in** (see [§9.3](#93-layer-b-foundation-2026-07-09)). *Remaining:* per-session (not just
     per-soul) context, and reactive Seal-on-miss (today it's a proactive approval page). Enforcement
     defaults OFF until these mature.
4. **Adaptive IP step-up** (optional) — use `LastIp` mismatch to force re-confirmation of an
   otherwise-trusted device/context.

Each phase is independently shippable and fail-closed; none weakens the key-possession invariant.

### 9.1 What was built (2026-07-09)

The reusable, no-persistence core of the primitive — server-side, **no bridge changes** (the node
already signs the seal nonce bytes verbatim, so a canonical grant payload is passed as those bytes):

| Piece | File | Role |
|---|---|---|
| `NodeCrypto.GrantPayload(type, subject, context, expiryUnix)` | `Aria.Web/Services/Node/NodeCrypto.cs` | Canonical signed bytes, same pipe-delimited style as the existing `EnrollPayload`/`RevokePayload`. |
| `SignedGrant` + `GrantVerifier.Verify` | `Aria.Web/Services/Node/SignedGrant.cs` | Self-describing grant; stateless verify recomputes the bytes and checks signature **and** expiry. Fails closed on missing fields or a `\|` in any field (which would make the layout ambiguous). |
| `SealService.RequestSignatureAsync(userId, desc, payloadBytes, ct)` | `Aria.Web/Services/ModelBridge/SealService.cs` | Extracted the ceremony into `RunCeremonyAsync`; signs caller-supplied bytes and returns the verified signature. `RequestSealIdAsync` now calls it with a random nonce (behaviour unchanged). |
| `GrantService.RequestGrantAsync(...)` | `Aria.Web/Services/ModelBridge/GrantService.cs` | Drives the Seal over canonical grant bytes and returns a `SignedGrant`. Constants `DeviceGrant` / `ContextGrant`. Registered as a singleton. |

**Verified:** `Aria.Tests/Web/SignedGrantTests.cs` (6 tests, all green) mints grants exactly as the
bridge's `/seal/{id}/approve` does — P-256 keypair in the bridge's key format, `SignData(payload,
SHA256)` — and asserts: valid grant verifies; tampered subject/context/type/expiry, expired grant,
wrong soul key, `\|`-injection, and missing inputs all fail closed. Full build clean; both apps
restarted and healthy.

**Not yet done in this phase:** the bridge-side `ContextGrants` table + a `GrantReplicationService`
cloned from `KeyReplicationService` (both belong to Layer B / multi-node). The server-side
`TrustedDevices` table landed with Layer A below.

### 9.2 Layer A (2026-07-09)

A working, integration-tested device-trust gate on a single node — the first consumer of the §5
primitive:

| Piece | File | Role |
|---|---|---|
| `TrustedDevice` entity + `CREATE TABLE IF NOT EXISTS TrustedDevices` | `Aria.Web/Data/TrustedDevice.cs`, `Data/DatabaseInitializer.cs`, `Data/Context/AppDbContext.cs` | Stores a node-signed device grant (soul, deviceId, signature, expiry, revoked). |
| `TrustedDeviceService` | `Aria.Web/Services/Auth/TrustedDeviceService.cs` | Issues the opaque `aria-device` cookie; `IsDeviceTrustedAsync` **re-verifies** the stored grant against the soul's public key (a tampered DB row can't grant access); `RecordTrustAsync` verifies before persisting; `RevokeAsync`. |
| Access-gate tier | `Aria.Web/Middleware/AccessGateMiddleware.cs` | Mints a device id for every browser reaching the gate, and adds a 5th tier: a device carrying a still-valid node-signed grant passes **regardless of IP**. |
| Approval + revoke endpoints | `Aria.Web/Endpoints/DeviceEndpoints.cs` | `POST /api/devices/trust-this` (soul-verified) drives the node Seal over a `trust-device` grant for the current browser, then records it; `POST /api/devices/revoke`. |
| In-app "trust this browser" button | `Components/Layout/NavMenuDevicesPanel.razor` + `NavMenu.Contacts.razor.cs` (`TrustThisBrowserAsync`) + `wwwroot/aria-interop.js` (`trustThisBrowser`) | Shown in the Devices panel when the soul is verified. The **browser** fetches the endpoint (so the HttpOnly `aria-device` cookie is sent — a server-side circuit call wouldn't carry it), the node opens its Seal page, and the button reports the outcome. |

Trust attaches to the **node-approved device, not an IP**, so it survives roaming / domestic-IP churn
(the concern raised for a mobile/changing-IP user). A **daemon-only node (no browser) is unaffected**
— it's a bridge, not a browser, and never touches this gate.

**Co-equal approval (upgraded 2026-07-09).** Device grants are verified against **any of the soul's
current keys** — the soul master key *or* any non-revoked `SoulNodeKeys` entry
(`GrantVerifier.VerifyAny` + `TrustedDeviceService.AcceptableKeysAsync`) — not only the primary soul
key. This realizes "any bridge can approve" and removes the primary-only dependency. Because only
*non-revoked* keys are accepted, **revoking the node that approved a device automatically drops that
device** (its signature no longer matches any accepted key) — no extra bookkeeping.

**Verified:** `Aria.Tests/Web/TrustedDeviceGateTests.cs` (7 tests, green) drives the real gate from
an external IP: a soul-key-approved device and an **enrolled-secondary-node-approved** device both
open the gate; no-cookie, unknown-device, expired-grant, revoked-device, and **revoked-approver-node**
are all **403**. `SignedGrantTests` (6) and existing `AccessGateTests` (5) still green — 18/18, no
regression. Full build clean; both apps restarted and healthy; `/api/devices/trust-this` returns 403
for an unverified soul.

**UI wired (2026-07-09):** the Devices panel now shows a **TRUST THIS BROWSER** button when the soul
is verified; it drives the node Seal and reports success/failure inline. Smoke-verified end-to-end
plumbing: `/api/devices/trust-this` returns 403 for an unverified soul, the `trustThisBrowser`
interop is served in `aria-interop.js`, and `/` renders 200 with the changes. (The full click →
node-approval → trusted flow needs a live soul-verified session + a human at the node, so it isn't
part of the headless test suite.)

Cross-node grant *replication* is **not needed for Layer A** — the `TrustedDevices` table lives on the
shared server, so every node's traffic already sees the same trust records (replication matters only
for Layer B's bridge-local `ContextGrants`).

**Layer A is complete.** Optional future polish: fold the legacy `aria-trusted` cookie into the same
device-id credential so there's a single browser-trust mechanism.

### 9.3 Layer B foundation (2026-07-09)

The server→bridge gate at the tunnel choke point, built **fail-safe**: enforcement is opt-in, so the
running app is unchanged until an operator turns it on.

| Piece | File | Role |
|---|---|---|
| `RequestClassifier` + `RequestSensitivity` | `Aria.Shared/RequestClassifier.cs` | Pure classifier → Benign/Sensitive. Path-only overload: `/llm/proxy`, `/tools/call`, `/terminal/exec`, and the whole `/project-files` + `/project-git` surface (Explorer panel, "#" picker, file viewer — server-driven project reads are exfiltration) are Sensitive. **Body-aware overload** refines `/tools/call` by the tool being invoked — read-only built-ins (`read_file`/`list_dir`/`glob`/`commands_index`/`GetCurrentDateTime`) are Benign so reads don't prompt; write/exec built-ins and **any MCP tool** are Sensitive. Fail-safe: unparseable/unknown body → Sensitive. Shared so bridge (enforce) and server/UI (explain) agree. |
| `ContextGrant` table + `ContextGrantStore` | `Aria.Bridge/Data/BridgeDbContext.cs`, `Services/BridgeDatabaseInitializer.cs`, `Infrastructure/ContextGrantStore.cs` | Local record that a human approved sensitive ops for a context (soul), with TTL + revoke. `EnforcementEnabled` then read `ARIA_BRIDGE_ENFORCE_GRANTS` (default off) — **superseded: now a persisted per-node toggle in the bridge UI, default ON**. |
| Gate seam | `Aria.Bridge/Infrastructure/DirectTunnel.cs` (`GateSensitiveRequestAsync`) | Classifies every `HandleLocalRest`; a Sensitive request with no valid grant is **refused with 403** up the tunnel (fail-closed) and the node opens the approval page (throttled). Enforcement off → observe/log only, forward as before. |
| Approval page + endpoints | `Aria.Bridge/Endpoints/ContextEndpoints.cs` | `GET/POST /context/approve` (local page → 8 h grant), `POST /context/revoke`, `GET /context/status`. Local-only surface — only software on the user's machine can approve. |

**Why this defends against a compromised server:** the grant is stored and checked **on the node**;
the server can relay bytes but cannot fabricate a grant or bypass the classifier. Enabling it makes
the node refuse to spend keys / run shell / execute tools on the server's say-so without a local human
OK.

**Verified:** `Aria.Tests/Shared/RequestClassifierTests.cs` (**35 cases**, green) locks the taxonomy —
sensitive paths, sub-paths, case/query handling, control-plane + `/tools/list` benign, and the
body-aware `/tools/call` matrix (read-only built-ins benign; write/exec built-ins, MCP tools,
camelCase/PascalCase bodies, and unparseable/unknown bodies all sensitive). Live bridge (now
**0.26.1-beta**): `/context/status` reports `enforcementEnabled:false` by default; `POST
/context/approve` → `granted:true`, `revoke` → `granted:false`; with `ARIA_BRIDGE_ENFORCE_GRANTS=1`
the status flips to `enforcementEnabled:true`. Reverted to default-off; tunnel healthy; full suite
**53/53**. The tunnel-driven enforced-block path (a live sensitive request returning 403) is composed
of these verified parts but isn't in the headless suite (needs a live soul session).

**Body-aware `/tools/call` (added 2026-07-09):** the gate now passes `req.Body` to the classifier, so
under enforcement a `read_file`/`list_dir`/`glob` doesn't prompt while `bash_exec`/`write_file`/MCP
calls do — the key change that makes enforcement usable rather than prompting on every tool call.

**Node-signed + mesh-replicated grants (added 2026-07-09, bridge 0.27.0-beta):**

| Piece | File | Role |
|---|---|---|
| Shared canonical payload | `Aria.Shared/GrantCanonical.cs` | Single source of truth for the signed bytes; `Aria.Web`'s `NodeCrypto.GrantPayload` now delegates to it, so a bridge-signed grant verifies server-side and on siblings. |
| Sign on approval / verify on check | `Aria.Bridge/Infrastructure/GrantCrypto.cs` + `ContextGrantStore` | Approval signs the grant with the soul key (or this node's key); `HasValidGrantAsync` re-verifies the signature (a live row isn't enough — a tampered/injected row fails). |
| Export / import | `Aria.Bridge/Endpoints/ContextEndpoints.cs` (`/context/grants/export`, `/context/grants/import`) | Import stores a grant only if its signature verifies — the relaying server can neither forge nor alter one. |
| Replication orchestration | `Aria.Web/Services/ModelBridge/GrantReplicationService.cs` + `POST /api/maintenance/replicate-grants` (soul-verified) | Relays each node's live signed grants to its siblings, mirroring `KeyReplicationService`. |

**Verified on the live bridge (0.27.0-beta):** `POST /context/approve` → `granted:true` with a real
ECDSA signature in `/context/grants/export`; importing that grant → `imported:1`; importing the same
grant with a **tampered expiry** → `imported:0`; with a **forged signature** → `imported:0`. So the
untrusted server can't inject or mutate a grant in transit. Cross-boundary agreement is unit-tested
(`NodeCrypto` delegates to `GrantCanonical`; a signature over the shared payload verifies with the
server's `GrantVerifier`). `replicate-grants` is soul-verified (403 otherwise). Suite **55/55**;
reverted to a clean, default-off state.

**Per-session context + gate coverage + auto-replication (added 2026-07-11, bridge 1.6.0-beta).**
Layer B is now session-scoped and covers the agent's actions, not just the `HandleLocalRest` surface:

| Piece | File | Role |
|---|---|---|
| Session-scoped grants | `Aria.Bridge/Infrastructure/ContextGrantStore.cs` (`ContextId`, `HasValidGrantForRequestAsync`) | Grant context is `{soul}` \| `{sessionId}`; a request from a session is authorised by a grant for that session **or** a soul-wide grant (no double-prompting). Signed/replicated through the same path unchanged. |
| Session id on the wire | `Aria.Shared/BridgeRequest.cs`, `LocalRestRequest` (optional `SessionId`), `HarnessContext.SessionId`, `WebHarnessRuntime`, `AgentService.CreateSessionAsync`, `Chat.Session.razor.cs` (`UserSessionState.SessionToken`) | The browser circuit's per-tab token is stamped on sensitive bridge calls. Null (background/maintenance, old servers) → soul-wide fallback, fully backward-compatible. |
| Gate covers tool actions | `Aria.Bridge/Infrastructure/DirectTunnel.cs` (`GateSensitiveAsync` on both `HandleLocalRest` **and** `HandleLlmRequestAsync`) | The streaming path is now gated by the *inner* url: a `/tools/call` action is gated (body-aware); a plain LLM completion is benign, so chat is never blocked. Closes the gap where agent tool calls bypassed the gate entirely. |
| Session-aware approval page | `Aria.Bridge/Endpoints/ContextEndpoints.cs` | The local page states **"VALID FOR 8 HOURS · THIS BROWSER SESSION"** with the session id, and signs a grant scoped to it. |
| Auto-replication | `Aria.Web/Services/ModelBridge/GrantReplicationBackgroundService.cs` | A 60s background loop relays each multi-node soul's signed grants to its siblings, so an approval on one node satisfies a headless sibling without a second approval. |

**Verified:** `Aria.Tests/Bridge/ContextGrantScopeTests.cs` (3, green) locks the `{soul}|{session}` id format; full suite unchanged (255 pass, the 2 failures are a pre-existing `Souls.ProjectsEnabled` schema drift, not Layer B). Live bridge 1.6.0-beta: session-scoped `POST /context/approve?session=…` yields a signed grant with contextId `{soul}|{session}` in `/context/grants/export`; `/context/status` reports `enforcementEnabled:false` by default.

**Phase 2 items — all landed (bridge 1.17–1.22).** Reactive **in-chat** approval surfacing is in:
a blocked sensitive op raises an in-chat prompt, the ceremony opens on the node the human pinned for
approvals, and the grant is replicated immediately before the halted turn retries
(`ContextApprovalService`, `ApprovalNodePicker`). **Secure co-equal approval** is in: each node builds
its grant-accepting key set from a locally-verified sibling roster (`Aria.Bridge/Services/Trust/SiblingRoster.cs`),
never from the server-supplied roster verbatim. **Enforcement is now ON by default**, persisted per node
and toggled from the bridge `// Security` tab (`/context/enforcement`) — the earlier `ARIA_BRIDGE_ENFORCE_GRANTS`
env-var gate is superseded. Vigil/Hive pre-authorisation with scoped (and one-shot) seals landed on top.
Item-by-item record: [phase2-context-grants-remaining.md](phase2-context-grants-remaining.md).

---

## Non-goals

- IP as an enforcement gate (see [§6](#6-why-not-ip-as-a-gate)).
- Third-party IP-resolution APIs on the bridge — the server-observed tunnel IP (knock) is already the
  true public IP and needs no external dependency.
- Any path by which the hosted server can self-authorize a sensitive act. If a change would let the
  server proceed without a node signature, it's wrong.
