# Bridge Remote Nodes — Security Notes & Open Checklist

Status: **in development** (bridge version `0.2.0`). This document is the security companion to
[`bridge-remote-nodes-feature-plan.md`](./bridge-remote-nodes-feature-plan.md). It records the trust
model as actually implemented, the recent device-deletion fix, the new key-rotation + GUID-soul-ID work
(§9), known broken state from testing, and — most importantly — **what still needs to be verified before
this feature can be trusted on an untrusted LAN**.

> ⚠️ Treat everything here as "security-relevant, not yet audited." Do not expose the Aria.Web server
> to a hostile network until the [verification checklist](#verification-checklist-do-before-trusting-this)
> is green.

---

## 1. What the feature does

A *soul* (user identity) may have more than one *node* (an `Aria.Bridge` daemon). One node is the
**primary** (holds the soul master key); additional nodes **join** with their own ECDSA P-256 node
keypair and must be **enrolled** into the soul's allow-list before they can connect or act.

Two independent things must be true for a browser to see/use a soul's data:

1. **A node is connected.** The bridge proves possession of an authorized key via a SignalR
   challenge-response (`ModelBridgeHub.RegisterDirectBridge`).
2. **The browser circuit is soul-verified.** The *specific browser tab* proves co-location with an
   authorized bridge — per-circuit, never server-global (§12).

### Co-location proofs (how a browser becomes verified)

| Context | Mechanism | File |
|---|---|---|
| Secure (localhost / HTTPS) | Automatic loopback attestation — browser fetches `127.0.0.1:5741/node/attest`, bridge signs `attest\|userId\|token\|nonce` | `aria-interop.js`, `CircuitAuthService.CompleteAsync`, `NodeEndpoints` `/node/attest` |
| Insecure (`http://LAN-IP`) | **Session-code fallback** — browser can't fetch loopback; user reads the per-process code from the bridge's localhost status page and pastes it; server fetches each connected bridge's code over the tunnel and matches | `CircuitAuthService.UnlockByCodeAsync`, `NodeEndpoints` `/node/session-code` |

The session-code path is **self-identifying**: the matched bridge tells the server which soul the
browser belongs to, so a remote machine never has to (and must not) pre-select a soul. This closed the
earlier flaw where a LAN browser defaulted to the *Mac* soul and was checked against the wrong bridge.

---

## 2. Trust anchors (what we rely on)

- **The bridge binds loopback only** (`127.0.0.1:5741`). Reaching it ⇒ you are code-executing on that
  machine. This is the root co-location proof for both attestation and the session code.
- **The soul/node private keys never leave the bridge.** The server relays signatures and opaque
  wrapped-DEK blobs; it never holds soul secrets.
- **The server is trusted with its own database.** It stores the allow-list (`SoulNodeKeys`) and routes
  all traffic. We do *not* defend against a malicious server operator — that is out of scope (they own
  the box). We *do* defend against a network attacker on the same LAN.
- **ECDSA P-256** for all node identity, challenges, enroll certs, and revoke signatures.

---

## 3. Authority model (§9.3 co-equal owners)

Enroll and revoke/delete are valid only if **signed by the soul key OR any non-revoked enrolled node**
(`ApproverInSet`, duplicated in `Program.cs` and `NodeService.cs`). The signature is verified against
the *live* key set, so a foreign key can't enroll itself into someone else's soul.

`§12` removed the REST surface for node management: list / enroll / revoke / approve / delete are
**in-process only**, invoked by `NodeService` from a per-circuit soul-verified Blazor session. There is
no unauthenticated LAN endpoint that returns soul data or mutates the allow-list. (A previous 6-digit
`approve-enrollment` REST endpoint was a brute-force hole and is gone.)

---

## 4. Recent fix — device deletion ("Approver not authorized")

### Symptom
Deleting a device failed with **"Approver not authorized"** from both machines.

### Root cause
`DeleteNodeAsync` / `RequestRevokeAsync` asked **one** bridge to sign the revocation — whichever
`ModelBridgeRegistry.GetDefaultNode` returned (the *most-recently-connected* node). If that connection's
effective key was not in the authority set (e.g. the only reachable bridge was a different soul's node,
a stale connection, or the node being deleted itself), `ApproverInSet` rejected it and the whole
operation failed. There was **no fallback** when no co-equal bridge was online — even though the action
is already gated behind a soul-verified circuit.

### Fix (`NodeService.AuthorizeRemovalAsync`)
1. **Smart signer selection.** Iterate *every* connected bridge for the soul (preferring a witness over
   the target being removed), and accept the first that returns an **in-set, signature-verified**
   revocation. Robust against `GetDefaultNode` picking an unsuitable connection.
2. **Soul-verified fallback.** If no authorized bridge is online to sign, permit the removal **only when
   the calling circuit is already soul-verified for that soul** (`circuitAuth.IsVerified(userId)`). This
   is the recovery path for orphaned / offline devices.
3. **Diagnostic logging.** Every rejected signer logs *why* (`[Node/delete] signer … returned
   out-of-set key …`), so future failures are explainable instead of silent.

### Why the fallback is not a weakening
The revoke signature only ever protected against a **network attacker** hitting the old REST endpoint —
which §12 already closed by making node management in-process. It never protected against the **server
operator**, who can edit `SoulNodeKeys` directly. A soul-verified circuit *is* co-equal authority by
definition (the human proved control of an authorized bridge to unlock). So the fallback removes a
usability dead-end without granting the server any power it didn't already have.

> ⚠️ **Residual asymmetry to review:** ENROLL still requires a real co-equal signature (correct — adding
> an attacker key must never be possible from the server alone). DELETE/REVOKE now also accept the
> soul-verified-circuit fallback. Confirm this asymmetry is acceptable: the worst a rogue server can do
> on the delete path is *remove* your nodes (a DoS), never *add* one.

---

## 5. Known broken state (from two-machine testing)

Observed in the live `Aria.Web/aria.db` during this work — **data corruption from repeated
join/re-join testing, not a live exploit**, but it must be cleaned and the root cause found:

- **Soul `JL-Windows` (userId 10) has an empty `PublicKey`** yet owns **three** `SoulNodeKeys` rows all
  marked `IsPrimary = 1`, each with a *different* key (`DESKTOP-47OJQSG`). A soul should have exactly
  one primary, and its `PublicKey` should never be blanked while node rows survive.
  - **Consequence:** those rows are undeletable via the UI — `DeleteNodeAsync` returns `"Unknown soul"`
    (empty `PublicKey`) and they are `IsPrimary` anyway (`"Cannot delete the primary node"`).
  - **Likely cause to investigate:** unlink / re-link / re-join clears `Users.PublicKey` but leaves
    `SoulNodeKeys` behind, and the join path can mint multiple primaries. See
    `ModelBridgeHub.RegisterDirectBridge` (`isPrimary = nodePublicKeyB64 == user.PublicKey`) and the
    soul link/unlink path in `SoulEndpoints` / `Program.cs`.
- **Soul `JL` (userId 9)** is healthy: node 1 = Mac primary (soul key), node 6 = "PC" (non-primary,
  enrolled by the Mac) — node 6 is the deletable target the fix addresses.

**Recommended cleanup:** either delete the `JL-Windows` soul and re-create it cleanly, or hand-fix the
DB (restore its `PublicKey`, collapse to a single primary). Until then, treat `JL-Windows` as a test
artifact, not evidence of feature behaviour.

---

## 6. Threat model summary

| Threat | Defence | Status |
|---|---|---|
| LAN attacker reads soul data via the server | Per-circuit attestation; data gates use `CircuitAuth.IsVerified` | ✅ believed closed — **re-verify (checklist)** |
| LAN attacker calls node mgmt over HTTP | No REST surface; in-process only (§12) | ✅ closed |
| Insecure-context browser can't unlock | Session-code fallback (40-bit, rate-limited, self-identifying) | ✅ implemented |
| Server alone enrolls an attacker key | Co-equal signature required for enroll | ✅ enforced |
| Brute-force session code / join code | 40-bit code, 10 tries/min/circuit; join code short-lived | ⚠️ verify rate limit covers all paths |
| Stale/duplicate connection used as signer | Smart signer selection + in-set verify | ✅ new |
| Orphaned device can't be removed | Soul-verified circuit fallback | ✅ new |
| Replay of attest / revoke payloads | Single-use nonce (attest); `nowUnix` + signature (revoke) | ⚠️ **revoke has no nonce/expiry window — review** |
| Wrapped-DEK leaks plaintext | Server relays opaque blob; only node key unwraps | ✅ by design — **verify crypto** |
| Brute-force enumeration of soul IDs | Soul ID is a 128-bit GUID (`User.Id`), not a sequential `int` (§9) | ✅ implemented |
| Leaked soul master private key | Owner rotates to a fresh keypair (§9.2) | ⚠️ **weak — see §9.5:** endpoint authenticates only the *new* key, so a GUID alone enables takeover; full key+GUID compromise is unrecoverable without an independent factor |
| Stolen node key rotates itself to evade revocation | Node-key self-rotation is **disallowed**; recovery is owner-revoke + re-join (§9) | ✅ enforced |
| Replay of a rotation request | Per-`(soulId,newPub)` single-use nonce, 2-min TTL (`RotationChallengeStore`) | ✅ implemented |

---

## 7. Verification checklist (do before trusting this)

### Per-circuit isolation (the headline guarantee)
- [ ] Two browsers, different machines, **same** soul selected: verifying circuit A must **not** verify
      circuit B. Confirm `circuit-{token}-{userId}` keys are independent and `ClearCircuit` fires on
      disposal.
- [ ] A browser on a machine with **no** linked/enrolled bridge sees **no** soul data — confirm every
      data gate (`NavMenu`, `Chat.razor`, `HeaderSoul`) reads `CircuitAuth.IsVerified`, never the
      server-global `IsSoulVerified(direct-{userId})`.
- [ ] Refresh re-locks then silently re-verifies from `sessionStorage` — and a *stale/forged* cached
      code does **not** unlock (it is re-checked live each load).

### Attestation & codes
- [ ] Loopback attest rejects a nonce that wasn't issued, a reused nonce, and a signature from a key not
      in the soul's set.
- [ ] Session code: confirm 40-bit entropy, the 10/min/circuit limit actually trips, and a wrong code
      returns the generic "didn't match any connected bridge" (no soul enumeration leak).
- [ ] Session code rotates on bridge restart and is only ever shown on `localhost` (never over LAN).

### Authority / allow-list
- [ ] Enroll with a key **not** signed by an in-set approver is rejected (`ApproverInSet`).
- [ ] Revoke/delete: smart selection skips out-of-set signers and the fallback fires **only** when the
      circuit is genuinely soul-verified for that exact soul.
- [ ] **Revoke replay:** decide whether `revoke|userId|targetPub|nowUnix` needs a freshness window /
      nonce. Today an old valid revoke signature could in principle be replayed — confirm impact.
- [ ] A connection whose row was deleted mid-session can no longer act as signer (it's dropped from the
      set; `RemoveNode` stops routing).

### Key rotation & GUID IDs (§9)
- [ ] Master rotation: a request with a valid new-key signature but the **wrong** `serverSoulId` GUID is
      rejected (no soul match), and a request with a mismatched signature is rejected (possession fail).
- [ ] Rotation nonce is single-use and expires (replay of a captured `rotate-master-key` body fails).
- [ ] After rotation, the bridge reconnects with the **new** key and tunnel auth verifies against the
      overwritten `Users.PublicKey`; the **old** key no longer authenticates.
- [ ] Rotation revokes all non-primary nodes; confirm they can no longer connect and must re-join.
- [ ] A **joined node** calling `/soul/rotate-key` is refused (not silently routed to a missing endpoint).
- [ ] Offline rotation (server down) leaves local key state **unchanged** — no divergence, no orphan.
- [ ] No `int`-schema `*.db` remains; a fresh GUID-schema DB is created and `register-soul` writes an
      explicit GUID `Users.Id`.

### Bridge surface
- [ ] `Aria.Bridge` binds **only** `127.0.0.1` (no `0.0.0.0`); confirm in `Program.cs` / launch config.
- [ ] `/node/attest`, `/node/sign-enrollment`, `/node/sign-revocation`, `/node/session-code` are
      reachable **only** from localhost and are not proxied out by the server beyond the intended tunnel.

### Crypto
- [ ] Wrapped-DEK: ECDH wrap/unwrap (`SyncCrypto`) round-trips and the server-relayed blob reveals
      nothing without the node private key.
- [ ] All payload strings byte-match between `Aria.Web` (`NodeCrypto`) and `Aria.Bridge`
      (`NodeEndpoints`) — enroll, revoke, attest. A mismatch silently breaks verification.

### Data hygiene
- [ ] Clean the `JL-Windows` corruption (§5) and find the root cause: unlink/re-join must not blank
      `Users.PublicKey` while `SoulNodeKeys` survive, nor mint multiple primaries.
- [ ] Deleting a soul cascades to `SoulNodeKeys` (FK `ON DELETE CASCADE` exists — verify it fires under
      `EnsureCreated`).

---

## 8. Open hardening TODOs (post-checklist)

- Add a freshness window / single-use nonce to revoke signatures if §7 finds replay is meaningful.
- Reconcile the `EnsureCreatedAsync` vs `Migrations/` split before any schema change (see CLAUDE.md).
- Consider signing the *delete* (not just revoke) so the audit trail records who removed a node, even on
  the soul-verified fallback path.
- Surface a "this device removed itself / was removed" notice to the affected bridge.
- Rate-limit the loopback attest path the same way as the code path.

---

## 9. Key rotation & GUID soul identities

Two related changes landed together: soul IDs became unguessable GUIDs, and a leaked soul master key
can now be rotated without losing the soul.

### 9.1 GUID soul IDs

`User.Id` and every foreign-key `UserId` column (15 tables in `Aria.Web`, plus
`BridgeSoul.ServerSoulId` and the SignalR/REST wire formats) changed from sequential `int` to a
`Guid.NewGuid().ToString()` **string**. `register-soul` now inserts an explicit GUID instead of
relying on `last_insert_rowid()`.

- **Why:** the session-code and join-code paths reference the soul by `serverSoulId`. A sequential
  `int` let an attacker walk `1, 2, 3, …` to enumerate or target souls. A 128-bit GUID makes that
  infeasible.
- **Why `string`, not the `Guid` CLR type:** the ID crosses JSON/SignalR/REST at every hop (JSON has
  no GUID type), SQLite stores `Guid` as TEXT anyway, and the bridge side already used
  `string Id = Guid.NewGuid().ToString()`. End-to-end `string` avoids parse/format friction. Trade-off:
  no compile-time "is-a-GUID" guarantee — a malformed ID is caught at lookup time, not by the type. The
  security goal (128 bits of unguessable entropy) is fully met regardless.
- **Schema impact:** `EnsureCreated` does **not** migrate; it skips creation when tables already exist.
  Old `int`-schema `aria.db` / `aria-bridge.db` files must be removed (they were moved aside to
  `*.old-intschema-*` during this work). A fresh DB is created on next start; souls must be re-created
  and re-linked.

### 9.2 Master-key rotation (implemented, online-only)

Endpoint chain: bridge status page **▶ ROTATE KEYPAIR** → `POST /soul/rotate-key` (bridge) →
`POST /api/bridge/rotation-challenge` then `POST /api/bridge/rotate-master-key` (server).

1. The bridge generates a **fresh** P-256 keypair.
2. It asks the server for a nonce (`RotationChallengeStore.Issue`, keyed by `(serverSoulId, newPub)`,
   2-min single-use TTL).
3. It signs the nonce with the **new private key** and submits `{serverSoulId, newPublicKey, signature}`.
4. The server matches the soul by its **immutable GUID `serverSoulId`**, verifies the signature against
   the *submitted* new key (possession proof), then **overwrites** `Users.PublicKey` and **revokes all
   non-primary `SoulNodeKeys`** (they were vouched-for under the old master key).
5. Only after a `2xx` does the bridge persist the new keypair locally.

**Authentication rests on two facts:** knowing the GUID soul ID (unguessable, §9.1) and proving
possession of the new key. The **old key is deliberately not required** — it may be the compromised one.

**Why node-key self-rotation is refused.** A joined node calling `/soul/rotate-key` is rejected with a
message to revoke + re-join. Allowing a node to rotate its own key would let a *stolen* node key rotate
to a new key and dodge the owner's revocation. The master key holder is the ultimate authority and may
self-rotate; a node may not.

### 9.3 Orphaned keys & offline rotation — the answer to "how does it safely reconnect?"

**There are no orphaned public keys.** Server-side, a soul has exactly **one** `Users.PublicKey`, keyed
by the stable GUID. Rotation *overwrites* that single column — it does not append to an allow-list (only
*node* keys use an allow-list; the master key is singular). The GUID, not the key, is the durable anchor
for "this is the same soul," so a rotated bridge re-binds to the identical soul row.

**Rotation is currently online-only**, and that is the safe default:

- `/soul/rotate-key` performs the server round-trip *synchronously* and swaps the local key **only after**
  the server accepts. If the server is unreachable, the call fails and **nothing changes locally** — so
  local and server key state can never diverge, and no orphan is created.
- Reconnection "to the proper same soul" works because tunnel auth (`GetDaemonChallenge` →
  `RegisterDirectBridge`) verifies the daemon's signature against `Users.PublicKey`. After a successful
  rotation both sides hold the new key, so the next connect verifies cleanly. The match is on GUID; the
  key is just the credential.

**If we later allow rotating while disconnected** (rotate now, reconcile on reconnect), the design is:

1. Stage the new keypair locally as *pending* (keep the old one usable meanwhile).
2. On the next connectivity, push `/api/bridge/rotate-master-key` **before** attempting tunnel auth;
   on `2xx`, promote the pending key and discard the old one.
3. **Accept that the old (possibly leaked) key stays server-trusted until that reconnect.** Offline
   rotation therefore gives *no* immediate security benefit — it only stops local use of the old key.
   This is the only "orphan-like" window, and it lives on the **server** (a stale-but-single trusted
   key), never as a dangling second key. Because rotation needs only the GUID + new-key signature, even
   a crash that loses the staged key self-heals: generate another new key and rotate again.

The crash-safety also covers the online path: if the server persists the new key but the bridge dies
before saving it, the bridge simply rotates again (new key #2) on restart — the endpoint doesn't depend
on the previous key, so it always converges.

### 9.4 Still to do (rotation)

- [x] **Offline-initiated rotation** (§9.3 staging model) — **decided against.** It adds staging
      complexity for no real security benefit (the old key stays server-trusted until reconnect anyway).
      Rotation stays online-only.
- [ ] **Node-key rotation/refresh** has no server endpoint by design; confirm "revoke + re-join" is an
      acceptable recovery for a compromised *node* (vs. master) key, and that the bridge UI guides it.
- [ ] **Rotation does not yet notify connected browsers/nodes.** After a master rotation, enrolled nodes
      are revoked server-side but won't learn why until their next failed connect — surface a notice.
- [ ] **Audit trail:** record rotations (who/when) so a surprise rotation is visible to the owner.
- [ ] **Rate-limit `/api/bridge/rotation-challenge`** — it's GUID-gated, but add a per-soul limit so a
      known GUID can't be used to spam nonce issuance.
- [ ] Verify the master-rotation revoke-all-nodes step against the §5 multi-primary corruption case
      (a soul with several `IsPrimary` rows) behaves sanely.

### 9.5 Open problem — master-key takeover by GUID (and key) compromise

Two layered findings, in increasing severity. **Neither is fixed yet** — captured here for design, no
code changed.

#### (a) GUID-only takeover — current endpoint is too weak

`/api/bridge/rotate-master-key` authenticates possession of the **new** key only. The `serverSoulId`
GUID is unguessable by brute force but is **not a secret**: it crosses the wire on every link/rotation
call, is shown truncated on the bridge status page, and lands in logs. An attacker who merely *learns* a
victim's GUID can set it in their own bridge DB, generate a fresh keypair, sign the rotation nonce with
that **new** key, and overwrite the victim's `Users.PublicKey` — a full takeover **without ever holding
the victim's key**.

> **Minimum fix:** also require a signature over the rotation nonce with the **current (old) key**,
> verified against the stored `Users.PublicKey`. That proves the requester is the *current holder* and
> closes the GUID-only path. Legitimate use is unaffected — a "leaked" key is *copied*, not lost, so the
> owner can still sign with it. (Cheap, no new secret. Should be done regardless of (b).)

#### (b) Key + GUID compromise — the fundamental limit

If the attacker holds the **private key as well as** the GUID, the old-key signature in (a) no longer
helps — they can produce it too. Cryptographically the attacker is **indistinguishable from the owner**:
both hold the same secret. This is a theorem, not a bug:

> **If the sole authenticator is the private key, its compromise is unrecoverable by that key alone.**
> Recovery requires a *second, independent factor established before the compromise.*

Candidate independent factors, with trade-offs:

| Defence | How it stops a key+GUID attacker | Cost / trade-off |
|---|---|---|
| **Recovery passphrase** — server stores only an Argon2/PBKDF2 hash; rotation / break-glass requires proving knowledge of it | Attacker has key+GUID but not the passphrase | Owner must safeguard it; loss = no recovery. Fits the existing soul-export passphrase concept (`ExportSoulRequest`, AES-GCM + PBKDF2 200k in `SoulEndpoints`). **Recommended primary defence.** |
| **Multi-device quorum** — rotation co-signed by ≥2 enrolled node keys (or soul-key + a node key) | Attacker must compromise keys on *multiple* machines, not one | Only works for souls with ≥2 devices; single-node souls excluded. Extends the existing §3 / §9.3 co-equal-owner model. |
| **Trusted-operator reset** — owner edits the server DB / runs a CLI reset after an out-of-band identity check | The server operator is already trusted (§2); the attacker is not the operator | Only meaningful when owner ≈ operator (self-hosted). Natural fit for this product's deployment. |
| **Hardware-backed, non-exportable key** — TPM / Secure Enclave / OS keystore holds the private key | Prevents the leak at the *root* — the key can't be exfiltrated | Platform-specific; **no help if the key already leaked.** Best *preventative*, not *recovery*. |
| **Rotation notify + delay window** — out-of-band alert on any rotation; effect deferred; owner can veto | Buys detection / response time | The veto itself needs an independent factor, else the attacker vetoes back. Weak alone; useful only as a *layer* over the rows above. |

#### Recommended direction (to decide)

1. **Do the (a) old-key-signature fix now** — it's cheap and closes the GUID-only takeover, which is the
   embarrassing one (no key needed).
2. **Add an optional recovery passphrase** as the independent factor that makes true key-compromise
   recoverable. Reuse the existing export-passphrase machinery.
3. **For multi-device souls, prefer quorum rotation** over single-signer.
4. **Document the honest limit:** a single-device soul with no recovery passphrase has **no recovery from
   full key compromise, by design.** The realistic mitigation there is *prevention* — hardware-backed
   keys — plus the existing full-wipe + re-register escape hatch (which itself relies on the server
   operator, i.e. the owner, since `register-soul`'s name-fallback is operator-controlled).

> Note the asymmetry with **node** keys: those already have an allow-list and co-equal revocation (§3),
> so a single compromised *node* key is recoverable (an owner revokes it). The gap is specifically the
> **singular master key** — it has no second factor and no quorum, so its compromise is the one
> unrecoverable event in the model.
