# Layer B — Phase 2 Remaining Work

> **Status:** ✅ **all items landed** (bridge `1.17`–`1.22-beta`, 2026-07-14) — co-equal approval via
> locally-verified sibling roster, reactive in-chat approval with approval-node pinning, sigil scoping
> decided (Item C), plus vigil/Hive pre-authorisation on top. **Enforcement is ON by default**, toggled
> per node in the bridge `// Security` tab. This document is kept as the design record.
> **Original status:** draft · **Date:** 2026-07-11 · **Depends on:** Phase 1 (per-session grants, gate
> coverage, auto-replication) shipped in bridge `1.6.0-beta`.

## Context

Phase 1 made Layer B context grants **per-session**, extended the gate to cover the agent's tool
actions (not just `HandleLocalRest`), gave the approval page a clear "8h · this browser session"
statement, and added a 60s background replicator. Two things remain before enforcement is usable as
a default:

1. **Secure co-equal approval** — approve on *any* of a soul's nodes, accepted by all (the
   inverted-location scenario: you're at the headless remote box, not your usual primary).
2. **Reactive in-chat approval** — surface "approve on your node" in the terminal instead of the node
   silently opening a local page the human may never see (critical for the headless case).

Plus one open design question (**sigils**) to resolve, not necessarily implement, before enabling.

The invariant this phase must never break:
> A node's set of grant-accepting keys (`ContextGrantStore.AcceptableKeys`) is derived only from
> **locally-held, cryptographically-verified** material — never from a roster the (untrusted) server
> supplies. If the server can add an acceptable key, it can forge grants and the whole gate collapses.

---

## Item A — Secure co-equal approval (trust-critical)

**Problem.** Today a grant is accepted only if it verifies under `{soul public key, this node's own
node key}`. So a grant signed on a *secondary* node (which signs with its own node key, not the soul
key) is rejected by its siblings. Widening `AcceptableKeys` to include sibling node keys is the fix —
but the sibling roster lives on the untrusted server (`SoulNodeKey`), and the enrollment signature is
currently **verified at enroll time then discarded** (`BridgeNodeEndpoints` validates `req.Certificate`
via `NodeCrypto.EnrollPayload` + `ApproverInSet`, but `SoulNodeKey` stores no signature). So a bridge
has nothing to re-verify a sibling key against — trusting the server's roster verbatim would let a
compromised server inject a key and forge grants.

**Design — relay a soul/approver-signed enrollment certificate each node can verify.**

1. **Persist the enrollment certificate.** Add `EnrollmentCertB64` (the signed `EnrollPayload`) and
   `ApproverPublicKeyB64` to `SoulNodeKey` (`Aria.Web/Data/Bridge/SoulNodeKey.cs` + `AppDbContext`
   creation). Populate it in `BridgeNodeEndpoints` at enroll time — the signature is already computed
   there for verification; stop discarding it.
   - Backfill: the **primary** row (soul-key node) is self-authenticating (its key *is* the soul key);
     existing non-primary rows without a stored cert are treated as unverifiable → not added to any
     bridge's `AcceptableKeys` until re-enrolled. Fail closed.

2. **Expose a verifiable roster to bridges.** Add a hub method on `ModelBridgeHub`
   (`GetSoulNodeRoster(userId)`) — modelled on the existing `GetWrappedDek` — returning, for each
   non-revoked `SoulNodeKey`: `{ nodePublicKeyB64, enrollmentCertB64, approverPublicKeyB64 }`. Add its
   REST-relay path to `TunnelAllowlist` if fetched over the tunnel instead of the hub.

3. **Verify the chain on the bridge before trusting a sibling key.** New
   `Aria.Bridge` service (e.g. `SiblingRoster`) that, on connect and periodically:
   - fetches the roster,
   - for each entry, recomputes `EnrollPayload` and verifies `enrollmentCertB64` against **either** the
     soul public key (`BridgeSoul.PublicKeyBase64`, held locally) **or** an already-trusted sibling key
     whose own cert chains to the soul key (co-equal approvers) — mirroring `ApproverInSet`,
   - stores only verified sibling public keys in a local `TrustedSiblingKeys` table.
   - Reject any entry whose cert doesn't verify, or whose `approverPublicKeyB64` isn't already trusted.

4. **Widen `AcceptableKeys` to the verified set.** `ContextGrantStore.AcceptableKeys(soul)` returns
   `{soul pubkey, own node key} ∪ {locally-verified sibling keys}`. Never the raw roster.

5. **Sign with the node's own key when the soul key is absent.** `GrantAsync` already falls back to
   `NodePrivateKeyBase64`; once siblings trust that key (step 4), a secondary-approved grant verifies
   everywhere. No change needed beyond confirming this path.

**Revocation** must drop trust immediately: only **non-revoked** roster entries are accepted, so
revoking a node on the server (`SoulNodeKey.Revoked`) removes it from every bridge's trusted set on the
next roster refresh — same property Layer A device trust already relies on.

**Reuse:** `NodeCrypto.EnrollPayload` / `ApproverInSet` (chain logic), `GrantCrypto.Verify`,
`GetWrappedDek` (server-relays-opaque-blob precedent), Layer A's `GrantVerifier.VerifyAny` pattern.

**Non-goal / explicitly rejected:** replicating the soul **master private key** to secondary nodes
(the "master-key co-primary" option). Co-equal *approval* does not require it and it doubles the
crown-jewel blast radius.

---

## Item B — Reactive in-chat approval

**Problem.** Under enforcement, a blocked sensitive op returns a refusal up the tunnel
(`DirectTunnel.GateSensitiveAsync` → `CompleteRequest(false, "Context approval required …")` on the
streaming path, or `403 { contextApprovalRequired }` on `HandleLocalRest`). Today the *node* also
auto-opens its local approval page — useless on a headless remote box where no human is. The human at
the browser sees only a failed turn.

**Design — surface the approval where the human is (the browser), routed to a node that can sign.**

1. **Detect the refusal server-side.** In the LLM/tool stream handling (`AgentService.StreamAsync` /
   the `CogitationStreamRouter`), recognise the `contextApprovalRequired` / "Context approval required"
   marker and emit a structured signal to the circuit instead of a raw error token.

2. **Render an in-chat prompt.** A banner in the chat: *"Sensitive operations need approval on your
   node — [Approve for this session (8h)]"*, showing the session id and scope, matching the node page's
   wording.

3. **Drive the approval.** The button triggers a ceremony (modelled on `SealService.RunCeremonyAsync`)
   that, over the tunnel, tells a node **that holds a signing key and has a human present** to open
   `/context/approve?session={SessionToken}` — the local machine's node when browsing there; for the
   headless case, route it to whichever node the human is actually at. On approval the node signs the
   session-scoped grant.

4. **Replicate immediately.** After approval, kick `GrantReplicationService.ReplicateAsync(userId)`
   once (don't wait for the 60s tick) so the executing node (possibly headless) gets the grant now.
   Then retry the blocked turn.

5. **Poll/settle.** The browser polls `/context/status` (via tunnel) until `granted:true`, then
   re-issues the request.

**Reuse:** `SealService` ceremony shape, `ContextEndpoints` (`/context/approve`, `/context/status`),
`GrantReplicationService`, the existing `contextApprovalRequired` refusal envelope.

---

## Item C — Sigils (design decision)

**Decision: sigils ride the session grant; high-stakes sigils still require a per-action Seal.**

A sigil is a user-facing bundle of capabilities, not a separate security boundary. When a sigil's
actions are classified as sensitive, they are governed by the same Layer B gate as any other
sensitive operation:

- If the browser session already has a live node-approved context grant, the sigil's sensitive
  actions proceed without an extra prompt (same 8h pass as any other tool call in this session).
- If there is no live grant, the in-chat approval banner (Item B) is shown; approving it issues a
  session-scoped grant that covers the sigil's actions too.
- A sigil that is explicitly marked high-stakes (e.g. destructive or soul-key operations) still
  escalates to a per-action Inquisitorial Seal, exactly as any other `NeedsSeal` tool call does.
  The session grant does not bypass Seals.

**Rationale.** Treating every sigil as its own `contextId` would fragment approval state and force
re-approval each time the user switches sigils, which is hostile to flow. The session grant is the
right scope: it captures "this human, in this browser session, trusts this terminal to run sensitive
operations". Sigils are just one way those operations are grouped. Keeping Seals as a separate,
per-action high-stakes gate preserves the stronger consent for the operations that deserve it.

No extra implementation is required for this decision — the existing session-grant model and Seal
path already cover it.

---

## Definition of done (before flipping enforcement on by default)

- [x] `SoulNodeKey` persists + relays a verifiable enrollment cert; bridges build `AcceptableKeys`
      only from locally-verified keys (Item A).
- [x] A grant approved on **any** enrolled node is accepted on its siblings; revoking that node drops
      the trust on the next refresh.
- [x] In-chat approval prompt appears on a blocked sensitive op and completes the approve → replicate →
      retry loop, including the headless-executing-node case (Item B).
- [x] Sigil scoping decided and documented (Item C).
- [x] **Trust tests (the point of the whole feature):** prove the untrusted server **cannot**
      (a) forge a grant, (b) tamper contextId/expiry, (c) inject an acceptable key via a poisoned
      roster, (d) manufacture consent. Extend `ChannelEndpointsTests`-style live checks +
      `ContextGrantScopeTests` with a roster-injection case.
- [x] Full suite green — verified 2026-07-22: 414 passed / 4 skipped / 0 failed. The pre-existing
      `Souls.ProjectsEnabled` schema-drift failures (raw-SQL `Souls` inserts missing the split
      capability columns) were fixed in the test setup and no longer fail.

---

## Addendum — the joined node's trust anchor (2026-07-30)

Item A step 3 assumes each bridge holds the soul public key locally. That holds for the **primary**,
which owns the master keypair. A **joined** node holds only its own node keypair, so it has nothing to
verify the roster against, and a build that let it derive the soul key from the roster's `IsPrimary`
entry broke the invariant above: a malicious relay could nominate its own key `R` as primary and
self-sign the node's enrollment certificate under `R`. The cert and the claimed primary agreed with
each other, verification passed, `R` landed in `Souls.PublicKeyBase64`, and every subsequent grant the
server signed with `R` sailed through the Layer B gate without a human ever approving anything.

There is no cryptographic escape from this: every candidate key reaches a joined node via the server.
The anchor has to come from outside that channel, so it comes from the human:

- `Souls.SoulKeyPinnedAt` records that a human at *that machine* confirmed the key. A joined node
  treats `PublicKeyBase64` as untrusted while this is null — including values written by the earlier
  deriving build, which are deliberately not grandfathered in.
- The primary serves its own fingerprint at `GET /soul/fingerprint` and on the Soul panel
  (**⧉ COPY**, grouped as `abcd-efgh-ijkl-mnop`), straight off that machine's bridge. The
  reference value never transits the server, which is what makes the comparison mean something.
- Confirming the fingerprint is the **last step of joining**, not a separate ceremony: after the
  pairing code is approved, the joined node's Soul panel shows **JOIN · CONFIRM MASTER KEY**
  (paste + confirm). `GET /soul/pin` remains as a deep link for the Devices warning. The page
  never displays the server's candidate: showing it would let the human confirm the server's
  claim against itself. Pin only on a match, so a server presenting `R` fails the comparison.
- After pinning, a roster whose primary differs from the pin is refused wholesale
  (`SoulKeyTrust.PinMismatch`) rather than silently adopted, so the key cannot be swapped later.
- None of `/soul/pin`, `/soul/pin-key`, `/soul/unpin-key`, `/soul/pin-status` are on
  `TunnelAllowlist`, so the server cannot drive the anchor it is being anchored against. A test
  asserts this.

Cost: a joined node refuses sibling and primary-signed grants until someone finishes the join
fingerprint step. That is the intended failure mode — fail closed, and say so in the log.

Failing closed silently is still a bad experience: from the chat side it reads as "seals stopped
replicating to the Windows box", with nothing pointing at the cause. So each node reports its own pin
state (`ok` / `unpinned` / `mismatch`, `Aria.Shared.SoulKeyPinState`) to the server on connect and on
every 60-second knock, and Aria.Web pulses a red **JOIN NOT FINISHED** warning on that device's
row in DEVICES, pointing at the last join step on that machine.

That report is a **node self-assertion the server cannot verify, and it is display-only**. Nothing may
branch on it for trust, and nothing does: a node claiming `ok` while unpinned still refuses grants,
because the decision is `ResolveSoulMasterPublicKey` running locally on that node. The warning tells
the human which machine to walk to; it does not, and must not, become a way for the server to learn or
influence whether a node is anchored. The ceremony itself stays off `TunnelAllowlist`.

**Not** fixed here, deliberately: `EnrollmentExpiryUnix` is still not checked at verification time.
It is set to `now + 10 minutes` at enrollment (`NodeService.RequestEnrollAsync`) and stored forever,
so it bounds the freshness of the signing ceremony, not the lifetime of the node's trust. Enforcing it
during roster verification would revoke every node ten minutes after it was enrolled. Giving
enrollment certs a real, renewable validity period is a separate piece of work.

## Cross-references
- `docs/security/defense-in-depth-plan.md` §3–§5, §9.3 (Layer B design + Phase 1 changelog)
- `docs/readme/security.md` (user-facing model)
- `docs/security/hardening-plan.md` F-2/F-4/F-11/F-12 (node-authoritative posture this builds on)
