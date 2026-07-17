# Server-Hosting Security Review

_Assessment of the Aria.Web perimeter when hosted on a public server (e.g. Fly.io). The soul
crypto core is sound — nonce'd ECDSA challenge-response, dual-signature key rotation, no-overwrite
of live keys on register. The weaknesses below are all in the **server perimeter around it**._

Reviewed surface: `AccessGateMiddleware`, `Program.cs` forwarded-headers config, `ClientIpResolver`,
`SoulEndpoints`, `BridgeNodeEndpoints`, `MaintenanceEndpoints`, `VoxEndpoints`, `AccessEndpoints`,
`ModelBridgeHub` + `ModelBridgeRegistry`, `UiAccessKnockService`.

## Status

| # | Finding | Severity | Status |
|---|---------|----------|--------|
| 1 | Spoofable `X-Forwarded-For` defeats IP perimeter | Critical | ✅ **Fixed** 2026-07-08 |
| 2 | Maintenance/Vox endpoints not soul-verified | Critical | ✅ **Fixed** 2026-07-08 |
| 3 | No rate limiting | High | ✅ **Fixed** 2026-07-09 |
| 4 | Guest code is a static shared bearer | High | ◐ Partially mitigated (2026-07-09) — see note |
| 5 | No security headers | Medium | ✅ **Fixed** 2026-07-09 |
| 6 | SignalR relay methods not bound to connection | Medium | ◐ Fixed for LLM + local-REST streams; terminal PTY is a follow-up |

---

## Critical

### 1. IP perimeter is bypassable by a spoofed `X-Forwarded-For` — ✅ FIXED

`Program.cs` configures `ForwardedHeaders` with `KnownNetworks.Clear()` + `KnownProxies.Clear()`, and
`ClientIpResolver` trusts the **leftmost** `X-Forwarded-For` entry. Leftmost XFF is client-supplied,
so anyone can send `X-Forwarded-For: 203.0.113.9` and present as any IP. This defeats **both**
IP-based gates in `AccessGateMiddleware`:

- `IpRestriction:AllowedIPs` allow-list → forge an allow-listed IP.
- The bridge "knock" gate → forge a knocked IP.

On Fly.io the trustworthy client value is `Fly-Client-IP` (Fly sets it and strips client attempts),
but `ClientIpResolver` prefers XFF and only falls back to `Fly-Client-IP`.

**Fix:** on Fly, read `Fly-Client-IP` first (or exclusively); generally, pin `KnownProxies` to the
real proxy and take the rightmost untrusted hop — or don't derive trust from IP at all.

**Done (2026-07-08):** `ClientIpResolver.GetClientIp` now resolves `Fly-Client-IP` **first**. Fly
sets that header from the real edge connection and strips any client-supplied copy, so it cannot be
forged; in production the spoofable `X-Forwarded-For` branch is never reached. XFF remains only as a
fallback for non-Fly hosting, with an inline comment that it is not a security boundary unless a
trusted proxy overwrites it. This closes the allow-list *and* knock-gate bypass in one place, since
both the access gate and the bridge knock hub resolve the client IP through this helper.

### 2. Maintenance + Vox endpoints keyed by an unauthenticated `userId`, gated only by the coarse access gate — ✅ FIXED

`/api/maintenance/*` and `/api/vox/transcribe` take `userId` as a query param and are **not**
soul-verified — only behind the access gate. Once anyone is past the gate (guest code, allow-listed
or spoofed IP, or a shared-egress knock), they can pass **any** `userId` and:

- `GET /api/maintenance/node-llm-log` — read another soul's recent **prompt + response content**.
- `GET /api/maintenance/local-sources` / `node-keys` — enumerate channel URLs, node bindings, and
  provider-key *names*.
- `POST /test-channel`, `test-key-roundtrip`, `replicate-keys` — **drive LLM calls and mutate vaults
  on another soul's node** (cost + side effects).
- `/api/vox/transcribe?userId=…` — transcribe through another soul's node / Whisper key.

The UI already has the right primitive: the `direct-{userId}` soul-verified predicate
(`ModelBridgeRegistry.IsSoulVerified`). These endpoints should require it, the way the in-process
`NodeService` path does. This is the exact "unauthenticated LAN surface keyed by userId" that the
`BridgeNodeEndpoints` note says was deliberately avoided for enroll/revoke — maintenance/vox slipped
through.

**Fix:** guard every maintenance/vox endpoint with a soul-verified check on the supplied `userId`.

**Done (2026-07-08):** every `userId`-scoped endpoint now requires `ModelBridgeRegistry.IsSoulVerified(userId)`
(the same node-signed `direct-{userId}` predicate the UI uses) before doing any work, returning
`403 { ok: false, error: "Soul not verified …" }` otherwise:

- `MaintenanceEndpoints` — `nodes`, `local-sources`, `node-keys`, `test-key-roundtrip`,
  `replicate-keys`, `node-llm-log`, `test-channel` (shared `SoulVerified`/`Unverified` helpers). The
  `format-cache` purge is left ungated — it takes no `userId` and only clears a rebuildable global
  cache.
- `VoxEndpoints` — `/api/vox/transcribe`.

Verified with `curl`: a bogus `userId` now returns `403` on `nodes`, `node-llm-log`, and
`transcribe` instead of leaking soul data. A live, key-proven bridge for that exact soul must be
connected for the call to proceed.

---

## High

### 3. No rate limiting anywhere — ✅ FIXED

- `/access/pathoftheworthy` (POST) is a brute-forceable guest-code oracle.
- `unlink-challenge`, `rotation-challenge`, and `register-soul` can be ground/spammed —
  `register-soul` creates unbounded `User` rows.

**Fix:** add ASP.NET `AddRateLimiter` — a tight fixed-window on the access-code POST and the
anonymous `/api/bridge/*` challenge/register endpoints.

**Done (2026-07-09):** `AddRateLimiter` wired in `ServiceCollectionExtensions.AddAriaServices`,
`app.UseRateLimiter()` added after `UseRouting` in `Program.cs`. Two controls, both partitioned by
the **resolved client IP** (`ClientIpResolver` — Fly-Client-IP first, same value the access gate
trusts, so the partition key isn't spoofable):

- **Global limiter** on `/api/bridge/*` — 30 requests / minute / IP. Covers register-soul, the
  challenge issuers, enroll/revoke, pending-enroll, and any future bridge endpoint in one place. The
  `/api/modelbridge` SignalR hub and authenticated app traffic are unlimited.
- **`access-code` policy** on the guest-code POST — 5 / 5 min / IP.

Verified with `curl`: 35 rapid `register-soul` calls → requests 31–35 returned `429`; 8 guest-code
POSTs → requests 6–8 returned `429`.

### 4. Guest invite code is a static, case-insensitive, shared bearer for whole-app access — ◐ PARTIALLY MITIGATED

It doesn't unlock soul data (key possession is still required — good), but it gates everything else.

**Fix:** ensure configured codes are high-entropy and short-lived; prefer per-guest codes over one
shared secret.

**Done (2026-07-09):** the brute-force vector is closed by the `access-code` rate limit (#3 — 5
attempts / 5 min / IP), so a low-entropy code can no longer be guessed at speed. The remaining items
are **operational, not code**: use high-entropy codes with short expiries in `GuestAccess:Codes`, and
issue per-guest codes rather than one shared secret. No code change makes those choices for the
operator.

---

## Medium

### 5. No security headers — ✅ FIXED

Nothing sets `Content-Security-Policy`, `X-Frame-Options` / `frame-ancestors`,
`X-Content-Type-Options: nosniff`, or `Referrer-Policy`. A Blazor Server circuit is clickjackable
without a frame-ancestors deny.

**Fix:** add a small response-header middleware.

**Done (2026-07-09):** `SecurityHeadersMiddleware` added and wired via `app.UseSecurityHeaders()`
early in `Program.cs` (right after `UseForwardedHeaders`, so it also covers the access-gate 403 and
guest-code pages). Every response now carries `X-Content-Type-Options: nosniff`,
`X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, and a CSP.

The CSP is deliberately conservative — it sets **only** `frame-ancestors` (clickjacking) and does not
restrict script/style sources, because Blazor Server relies on its own injected scripts and a
stricter policy would need nonces/hashes to avoid breaking the circuit. The middleware sets
`frame-ancestors 'none'` on any response that doesn't already carry a CSP (APIs, error pages);
on rendered Blazor documents the framework sets its own `frame-ancestors 'self'`, which the
middleware does not clobber. Both values block cross-origin framing, and `X-Frame-Options: DENY` is
present on every response as the legacy belt-and-suspenders. Verified with `curl` on both an API
route (`'none'`) and `/` (`'self'`).

_Follow-up (optional): a full nonce-based CSP restricting `script-src`/`style-src` would add XSS
defense-in-depth, but requires threading a per-request nonce through the Blazor host page._

### 6. SignalR relay methods don't bind requests to their connection — ◐ FIXED (LLM + local-REST); terminal is a follow-up

`SendChunk` / `CompleteRequest` / `CompleteLocalRest` / `TerminalChunk` / `TerminalClosed` look up
`_pending[requestId]` with no check that the calling `Context.ConnectionId` owns it. Request IDs are
GUIDs so it isn't practically exploitable today, but any connected socket that learns a requestId
could inject into or complete another request's stream. `_requestToConn` already exists.

**Fix:** enforce connection ownership in these handlers as defense-in-depth. (`RegisterDirectBridge`
and `UiAccessKnock` are correctly gated, for contrast.)

**Done (2026-07-09):** the two request-stream paths — which carry model output and key/MCP/memory
responses — now verify ownership before accepting a chunk or completion:

- `WriteChunk` / `Complete` take the caller's `Context.ConnectionId` and check it against
  `_requestToConn[requestId]` (new `OwnsRequest` helper). A mismatch is silently dropped.
- A new `_restToConn` map records which connection each local-REST request was dispatched to;
  `CompleteLocalRest` only accepts a completion from that same connection.
- `ModelBridgeHub` passes `Context.ConnectionId` into all three.

**Remaining:** `TerminalChunk` / `TerminalClosed` dispatch PTY output by `sessionId`, and there is no
`sessionId → connectionId` ownership map today (sessions register browser-side callbacks in
`TerminalPtyService`, decoupled from the bridge connection). Binding those needs a session-ownership
map established at PTY-open time — a deeper change, deferred as a follow-up. Lower risk than the LLM
path since it requires guessing a live session GUID and only affects terminal output framing.

---

## Low / noted

- Minimal-API POSTs (`replicate-keys`, `test-channel`, the worthy form) don't enforce antiforgery —
  CSRF from a gate-passing browser is possible but low-value.
- Cloud keys stored base64-at-rest on the node — already documented as an accepted limit; fine since
  they never leave the user's machine.

---

## Suggested order of work

1. ~~Fix #1 (Fly-Client-IP trust) and #2 (soul-verify guard on maintenance/vox)~~ — ✅ done 2026-07-08.
2. ~~Add #3 (rate limiter)~~ — ✅ done 2026-07-09.
3. ~~Add #5 (security headers) and #6 (hub ownership check)~~ — ✅ done 2026-07-09 (#6 covers LLM +
   local-REST; terminal PTY ownership remains a follow-up).

### Remaining follow-ups

- **#6 terminal:** add a `sessionId → connectionId` ownership map so `TerminalChunk` / `TerminalClosed`
  can be bound like the other relay methods.
- **#4 ops:** high-entropy, short-lived, per-guest `GuestAccess:Codes`.
- **#5 optional:** nonce-based CSP restricting `script-src` / `style-src` for XSS defense-in-depth.
- **Low/noted:** antiforgery on the maintenance POSTs.
