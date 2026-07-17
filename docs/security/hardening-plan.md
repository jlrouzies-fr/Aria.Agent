# Security Hardening Plan — Cogitator Node & Server

> **Status:** draft · **Date:** 2026-07-08 · **Scope:** whole system (bridge, tunnel, server), not just the Terminal
> **Purpose:** a defensive review of the trust boundaries and a prioritised remediation roadmap, written ahead of hosting the terminal for an invite-only group. This is a hardening document for our own codebase — every item is framed as *finding → why it matters → how we close it*.

---

## 1. What we are protecting, and against whom

The product promise is deliberately strong: **the server orchestrates but never holds your secrets, and cannot authorise consequential acts on your machine.** The cogitator node (`Aria.Bridge`) keeps the soul key, cloud keys, OAuth tokens, and data; the server is "a switchboard that forgets."

The threat we most need to withstand — and the one this plan is built around — is:

> **A compromised or malicious server should not be able to read a user's secrets, run code on a user's machine, or destroy a user's data.**

A hobby server hosted for friends is a realistic target: modest operational hardening, high trust from users, and — critically — a *persistent outbound tunnel from every friend's laptop into it*. So the server is not merely a data-custody risk; if it is taken over, it already has a live channel into every connected node. That channel is what this plan hardens.

Two attacker profiles matter:

- **A1 — Compromised server.** Someone who gains code-execution on the hosted `Aria.Web` process. They can push any message down any node's tunnel.
- **A2 — Hostile web content.** A malicious page the user simply *visits* in their normal browser, which then tries to reach the node on `localhost:5741` (CSRF / DNS-rebinding class). No server compromise required.

Both converge on the same soft spot: **the node treats "a request arrived on loopback" and "a request arrived over the authenticated tunnel" as equivalent to "the local human asked for this."** They are not.

---

## 2. The intended trust model vs. the current reality

| Boundary | Intended guarantee | Current reality |
|---|---|---|
| Soul private key | Never leaves the node | An export endpoint returns it, gated only by a request-supplied passphrase, and it is reachable over the tunnel |
| Loopback = local human | Only software on the user's machine can reach the node | The tunnel lets the remote server issue arbitrary loopback calls; a browser page can issue state-changing POSTs (reads are blocked by CORS, writes are not) |
| Server cannot authorise high-stakes acts | Enforced by the node-signed Seal | Enforced **only** for interactive PTY; non-interactive command execution, file writes, and git run have no such gate |
| Server cannot run code on the node | (implied by the above) | Multiple endpoints run commands / mutate files and are reachable over the tunnel with no per-action consent |

The Seal ceremony is sound where it is applied — the signature genuinely requires the soul key and a human click. **The gap is coverage, not cryptography.** The same principle simply needs to extend to every capability that runs code, reveals a secret, or destroys data.

---

## 3. The one invariant to restore

Everything below is an instance of a single rule we want to hold node-wide:

> **The node — never the caller — decides what it will do. The server and the browser may *request* an action, but capability, scope, and consent are owned by the node and cannot be widened by anything in the request.**

Concretely that means: sensitive capabilities are **off by default**, **enabled only by a human at the node**, **scoped by node-side config the request cannot override**, and **the highest-stakes ones bound to a fresh signed consent**.

---

## 4. The loopback attack surface (inventory)

The node exposes ~90 endpoints on `localhost:5741`. Grouped by sensitivity, the ones that need a gate they don't currently have:

**Runs code / mutates the machine**
`/terminal/exec`, `/terminal/pty*`, `/tools/call` (shell + file write), `/project-files/write`, `/project-files/revert`, `/project-git/run`

**Reveals a secret**
`/soul/export` (master private key), `/keys`, `/keys/sync-export`, `/oauth/{provider}/token`

**Destroys or re-homes identity/data**
`/db/soul`, `/db/cogitations`, `/db/messages`, `/db/noosphere`, `/soul/unlink`, `/soul/rotate-key`, `/soul/link-server`, `/soul/switch-server`, `/soul/import`, `/soul/keypair`

Today the only thing standing between the tunnel (A1) or a web page (A2) and these endpoints is: a hardcoded command denylist on one endpoint, and browser CORS (which does not stop the *request* from executing). That is the core problem this plan fixes.

---

## 5. Findings and remediations

Severity reflects impact under the A1/A2 threat model.

### F-1 · Master key can be read off the node — **Critical** — ✅ FIXED 2026-07-09
`/soul/export` used to serialize the soul private key into a passphrase-wrapped blob with only the passphrase in the request body, and the endpoint was reachable over the tunnel. If the master key ever leaves the node, the entire trust model collapses permanently.

**Fix:** `/soul/export` is now a local-human-only recovery ceremony:
- The request must come from the bridge's own loopback UI (`LocalRequestGuard.IsLocalOrigin` checks `Host` and `Origin`). Cross-origin and DNS-rebinding requests are rejected with `403`.
- It requires a fresh, capability-bound Inquisitorial Seal (`SealEndpoints.TryConsumeSeal(id, "soul-export")`). The seal is approved by a human on the node and consumed on use, so it cannot be replayed or used for a different capability.
- The passphrase is still typed by the human, but the tunnel allowlist (F-2) ensures the endpoint is never reachable from the hosted server.
- A "Soul Backup" card on the bridge status page drives the full ceremony.

**Verified:** `Aria.Tests/Bridge/LocalRequestGuardTests.cs` (10 cases) and `Aria.Tests/Bridge/SoulExportCeremonyTests.cs` (8 cases): valid seal + local origin returns the encrypted blob; missing/wrong-tool/unapproved/consumed seals and non-local origins all return `403`/`400`. Full suite 180/180 green. Live bridge smoke test: end-to-end export ceremony produces a blob; replay of the same seal is refused. Bridge version bumped to `0.29.0-beta`.

### F-2 · Loopback is implicitly trusted by the tunnel proxy — **Critical (structural)** — ✅ FIXED 2026-07-09
The tunnel's local-REST handler forwards an **arbitrary path** chosen by the server to `localhost:5741`. This made every loopback endpoint a remote capability, which is why F-1, F-3, F-5 were reachable at all.

**Fix:** `DirectTunnel.HandleLocalRestAsync` now consults an explicit allowlist (`Aria.Shared/TunnelAllowlist`) before forwarding any server-relayed request to `localhost:5741`. Disallowed paths return `403 { tunnelAllowlistBlocked: true }` and never reach a local endpoint.

Allowed paths are exactly those the web app currently relays: `/llm/proxy`, `/llm/probe`, `/metrics`, `/keys/*`, `/oauth/*`, `/terminal/*`, `/project-files/*`, `/project-git/*`, `/cogitations/*`, `/contacts/*`, `/memory/*`, `/sync/apply`, `/seal/*`, `/context/grants/*`, `/node/*`, `/debug/llm-log`, and a few fixed control-plane paths. Blocked by omission: `/soul/export`, `/soul/import`, `/soul/rotate-key`, `/soul/keypair`, `/soul/unlink`, `/soul/switch-server`, `/soul/link-server`, `/soul/sign`, `/db/*`, and any other local-only surface.

**Verified:** `Aria.Tests/Shared/TunnelAllowlistTests.cs` (63 cases) locks the allowlist; full suite 159/159 green. Live bridge smoke test: tunnel-relayed `/keys` still works; bridge version bumped to `0.28.0-beta`.

### F-3 · Node accepts state-changing requests from web content — **Critical** — ✅ FIXED 2026-07-09
Only CORS guarded the daemon. CORS is a *read* protection enforced by the browser; it does not prevent a cross-origin page from *sending* a POST that the node then executes. So a page the user merely visits could reach code-execution and mutation endpoints (A2), independent of any server compromise. DNS-rebinding is the aggravated form of the same class.

**Fix:** `LocalOriginMiddleware` runs after routing in the bridge pipeline and enforces `LocalRequestGuard.IsLocalOrigin` on every mutating request (`POST`/`PUT`/`DELETE`/`PATCH`). Requests with a non-local `Host` or a non-local `Origin` are rejected with `403 { localOriginRequired: true }` before they reach an endpoint.

Tunnel-relayed requests are unaffected: `DirectTunnel.HandleLocalRestAsync` forwards via `HttpClient` to `http://localhost:5741`, so `Host` is local and no `Origin` header is sent.

A tiny allowlist covers endpoints that legitimately accept cross-origin browser traffic:
- `/node/attest` — browser attestation from Aria.Web (which may be served from a LAN IP).
- `/health` — benign health probe that load balancers may POST.

Preflights (`OPTIONS`) and read methods (`GET`/`HEAD`/`TRACE`) are skipped.

**Verified:** `Aria.Tests/Bridge/LocalOriginMiddlewareTests.cs` (14 cases): local mutating requests pass; cross-origin and DNS-rebinding requests are blocked; allowlist and read/OPTIONS paths are allowed. Full suite 194/194 green. Live smoke test: cross-origin `POST /soul/unlink` → `403`; cross-origin `POST /node/attest` → `200`; tunnel-relayed `/keys` still works. Bridge version bumped to `0.30.0-beta`.

### F-4 · Command execution has no consent gate — **Critical** — ✅ FIXED 2026-07-09 (node-side policy + bridge toggle)
`/terminal/exec` (and `/tools/call`'s shell, `/project-files/write`, `/project-git/run`) used to run code / mutate files with no equivalent of the Seal. Worse, the path allowlist and command blocklist that were supposed to constrain it **arrived in the request**, so a hostile caller supplied its own sandbox.

**Fix:**
- **Node-side Terminal policy.** `BridgeSoul` now stores `TerminalAllowedPathsJson` and `TerminalBlockedCommandsJson`. New endpoints `GET /terminal/config` and `POST /terminal/config` let a human at the node define the maximum scope. `SecurityPolicy.FromNodeAndRequest` builds the effective policy: the web request may only *narrow* the allowed-path set and add to the blocked-command set; it can never widen what the node allows. A "Terminal Policy" card on the bridge status page edits these values.
- **Bridge-side Terminal capability toggle.** `TerminalEnabled` is the master switch, off by default. Quick Exec, Tab completion, agent `bash_exec`, and PTY are all refused until a human at the node enables Terminal Capability on `http://localhost:5741`. The web Terminal tool-options modal already surfaces this requirement.
- **PTY still requires its own time-limited Inquisitorial Seal** on top of the master toggle (`PtyEnabledUntil`).

**Honest limit:** the standing grant for non-interactive terminal execution is currently the persistent `TerminalEnabled` toggle (enabled/disabled at the node, revocable at any time). The plan originally called for an expiring grant window for Quick Exec matching PTY's `PtyEnabledUntil`; that is tracked as a future increment if we want stricter expiry semantics for Quick Exec.

**Verified:** `Aria.Tests/Bridge/SecurityPolicyTests.cs` (8 cases) covers node/request merging, narrowing, and blocklist union; `Aria.Tests/Bridge/TerminalNodeConfigTests.cs` (6 cases) verifies config persistence and endpoint enforcement. Full suite green. Bridge version bumped to `0.31.0-beta`.

### F-5 · Destructive & identity endpoints are ungated — **High** — ✅ FIXED 2026-07-09
Database wipes (`/db/*`), unlink, key rotation, and server re-homing (`/soul/link-server`, `/switch-server`, `/import`) could previously be driven over the tunnel or by web content. Impact ranges from data loss to pointing a node at an attacker's server.

**Fix:**
- These endpoints are already **not tunnel-reachable** (F-2 allowlist omits `/db/*` and `/soul/*`) and **origin-checked** by `LocalOriginMiddleware` for mutating requests (F-3).
- Re-homing and key operations now additionally require a **fresh, capability-bound Inquisitorial Seal** approved at the node:
  - `/soul/link-server` — seal capability `soul-link-server`
  - `/soul/switch-server` — seal capability `soul-switch-server`
  - `/soul/unlink` — seal capability `soul-unlink`
  - `/soul/import` — seal capability `soul-import`
  - `/soul/rotate-key` — seal capability `soul-rotate-key`
- The bridge status page drives the seal ceremony before calling each endpoint; consumed seals cannot be replayed.

**Verified:** `Aria.Tests/Bridge/SoulSealGatedEndpointsTests.cs` (9 cases): missing seal → 400, unapproved/wrong-tool seal → 403, approved correct-capability seal → endpoint proceeds. Full suite 229/229 green. Bridge version bumped to `0.32.0-beta`.

### F-6 · The Seal authorises a time-window, not an action, and isn't bound to what the human saw — **High** — ✅ FIXED 2026-07-09
Approving one Seal used to open a fixed grant window during which the server could act freely; and the tool name / reason / args shown on the approval page were strings supplied by the requester, while the signature covered only an opaque nonce. So *what the user read* and *what they authorised* were not cryptographically tied, and `pty-enable` accepted any approved seal id (confused-deputy).

**Fix:**
- Interactive seals now sign a **canonical, human-readable statement** built by `Aria.Shared.SealStatement.Build` and rendered verbatim on the approval page. The statement contains the exact capability, scope, details, expiry, and nonce, and the node signs it with the soul private key.
- The server reconstructs the expected statement in `SealService.RunCeremonyAsync` and verifies the signature covers that exact text. If the statement is missing, malformed, or does not match the requested capability, the seal is rejected.
- `/terminal/pty-enable` no longer accepts any approved seal: it consumes a seal bound to the `terminal_pty` capability via `SealEndpoints.TryConsumeSeal(req.SealId, "terminal_pty")`. Wrong-capability seals are rejected and cannot be replayed.
- Durable node-signed grants (device/context grants, defense-in-depth §5) keep signing the caller-supplied raw payload bytes, verified independently by `GrantVerifier`. The seal request carries an explicit `signStatement` flag so the two modes do not collide.

**Verified:** `Aria.Tests/Bridge/SealEndpointsTests.cs` now covers statement signing and raw-mode durable grants; `Aria.Tests/Bridge/TerminalNodeConfigTests.cs` verifies PTY enable rejects a seal approved for a different capability. Full suite 233/233 green. Bridge version bumped to `0.33.0-beta`.

### F-7 · Secret-at-rest posture — **Medium** — ✅ FIXED 2026-07-09
Cloud keys and the soul key were stored base64 (not encrypted) in the node vault. That meant anything that could read the vault file — a compromised local process, a backup tool, or a read-only leak — could read the secrets.

**Fix:**
- Added field-level encryption at rest for sensitive SQLite columns. A random 256-bit data encryption key (DEK) is protected by the OS keychain/DPAPI/Secret Service and stored in a sidecar file; the protected blob is useless without the OS credential store.
- Platform-specific protectors in `Aria.Bridge.Services.Vault`:
  - **Windows:** DPAPI (`System.Security.Cryptography.ProtectedData`), `CurrentUser` scope.
  - **macOS:** Keychain generic password via the `security` CLI.
  - **Linux:** freedesktop Secret Service via `secret-tool`; falls back to a machine+user-derived key file if Secret Service is unavailable.
- Sensitive properties are marked `[Encrypted]` and transparently encrypted/decrypted by an EF Core value converter:
  - `BridgeSoul.PrivateKeyBase64`, `NodePrivateKeyBase64`, `DataKeyBase64`
  - `BridgeOAuthToken.AccessToken`, `RefreshToken`
- AES-256-GCM with a random nonce per value; ciphertext is prefixed with `enc:1:`.
- Legacy plaintext values remain readable (decrypt returns them as-is) and are re-encrypted on first write. A one-time startup migration loads and saves all existing souls and OAuth tokens so the vault is encrypted immediately after upgrade.

**Honest limit:** this encrypts values inside the SQLite DB, not the whole file. The DB schema/metadata and non-sensitive fields are still plaintext. The `LlmKeys` table is now also encrypted via the vault layer (F-10); this note applied only to earlier increments.

**Verified:** `Aria.Tests/Bridge/VaultEncryptionTests.cs` (4 cases): round-trip encryption, legacy plaintext readability, EF transparent encryption of `PrivateKeyBase64`, and raw-DB verification that the stored value is encrypted. Full suite 239/239 green. Bridge version bumped to `0.35.0-beta`.

### F-8 · Server-side blast radius & tunnel authority — **Medium** — ✅ FIXED 2026-07-09 (node-side audit trail)
Because the trigger for the whole threat model is server compromise, server hardening matters — but note that *no amount of server hardening substitutes for F-2/F-4*, because a fully-owned server process can push anything. The right posture: minimise what the server *can* express down the tunnel (F-2), and keep the consequential decisions on the node.

**Fix:**
- Added a **node-side security audit trail** (`Aria.Bridge.Services.SecurityAuditLog`) backed by the bridge SQLite database. It records sensitive capability invocations with category, action, capability, detail, timestamp, and allow/deny outcome.
- The following operations are now logged:
  - **Seal** approvals and rejections.
  - **Terminal** PTY enable/revoke and every `/terminal/exec` invocation (allowed, blocked, error, interactive hint).
  - **Soul** export, import, rotate-key, unlink, link-server, and switch-server — including denials from missing/wrong-capability seals or non-local origins.
- Events are retained for 30 days / 1,000 entries (whichever is smaller) and exposed via `GET /audit/log`.
- The bridge status page has a new **// Security** tab showing the recent audit trail so a human can see exactly what their node was asked to do and whether it was allowed.

**Additional increment:** server-side storage of OAuth tokens (`UserOAuthTokens`) and cloud LLM API keys (`UserLlmApiKeys`) has been removed from `Aria.Web`. Cloud LLM calls are proxied through the node's `/llm/proxy` (which injects the key locally), so the server never receives an LLM key; OAuth tokens are fetched per-call via `WebHarnessRuntime.GetOAuthTokenAsync`. Neither secret persists on the server. Conversation messages also live on the bridge for new cogitations (`OriginNodeId` set at creation); the server keeps only an index row (title/timestamps/folder).

**Honest limit:** operational host hardening and tunnel-authority gating (so a read-only server DB leak does not grant tunnel control) remain standard operational work, not code-level controls. The audit trail and removal of server-side secret tables are the code-level increments for F-8.

**Verified:** `Aria.Tests/Bridge/SecurityAuditLogTests.cs` (2 cases) verifies seal approvals and rejections create audit events retrievable via `/audit/log`. Full suite 235/235 green. Bridge version bumped to `0.34.0-beta`.

### F-9 · Honest limits in user-facing docs — **Low, but do it early** — ✅ FIXED 2026-07-09
The README/architecture presented blocklist + path-lock as the Terminal's security story. Under the A1/A2 model that overstates the guarantee, and the individual findings (F-1 through F-8) were scattered across the codebase with no single user-facing summary.

**Fix:**
- Created `docs/readme/security.md` — a single, accessible security overview that explains every control (F-1 through F-8) in plain language.
- Each control is presented in a table with: **what it does**, **concrete protection** (the attack it stops), **user experience**, and **technical implementation**.
- Added an explicit **Honest limits** section stating current gaps: LlmKeys plaintext, vault encryption is value-level not full-disk, Linux fallback, residual server authority over allowed paths, and secondary-node scope.
- Linked the new security page from `docs/readme/architecture.md` and the main `README.md` archives table.

**Verified:** Documentation renders correctly; no code changes required.

### F-10 · Bridge-side Terminal policy + encrypted LLM keys at rest — **High** — ✅ FIXED 2026-07-09
Two remaining server-side levers weakened the "server is a switchboard" story. First, the Terminal tool still let the web UI edit an "extra blocked patterns" list that was sent with every command request; a compromised server could shorten the effective blocklist or add project paths. Second, while OAuth tokens and the soul key were encrypted at rest, cloud LLM API keys in the raw-SQL `LlmKeys` table remained plaintext in the vault file.

**Fix:**
- **Terminal policy fully owned by the bridge.** `BridgeSoul` already held `TerminalAllowedPathsJson` and `TerminalBlockedCommandsJson`; now the web Terminal tool is read-only for these values. The server fetches them via `GET /terminal/config` and displays them in the tool-options modal with a link back to the bridge status page. `Chat.Terminal.razor.cs` no longer parses a server-side `BlockedCommands` config and sends an empty array; `TerminalClient.GetConfigAsync` is the only source of truth. `SecurityPolicy.FromNodeAndRequest` already enforced that the request may only narrow the node's declared scope, so removing the server-side edit surface closes the widening path completely.
- **LlmKeys encrypted by the vault DEK.** `LlmKeyStore` now decrypts `KeyB64` through `BridgeDbContext.Vault` after reading, and `LlmKeyEndpoints` encrypts on `PUT /keys/{provider}`, decrypts on `GET /keys/sync-export`, and re-encrypts on `POST /keys/sync-import`. A one-time startup migration `MigrateLlmKeysAsync` walks the raw `LlmKeys` table, decrypts any legacy plaintext values, and re-encrypts them under the vault DEK; progress is tracked by marker file `.llm-keys-encryption-migrated`.

**Honest limit:** old `UserToolConfigs` rows that stored `BlockedCommands` for the Terminal tool are now dead data in the server database. They are not read and pose no runtime risk, but no migration cleans them up yet.

**Verified:** `Aria.Tests/Bridge/LlmKeyEncryptionTests.cs` (4 cases): round-trip encrypted storage, legacy plaintext readability, sync export/import preserves encryption, and migration re-encrypts legacy rows. `Aria.Tests/Bridge/TerminalNodeConfigTests.cs` and `Aria.Tests/Bridge/SecurityPolicyTests.cs` cover node-side policy enforcement. Full suite 241/241 green. Bridge version bumped to `0.38.0-beta`.

### F-11 · No server-readable key path + node-authoritative file/git scope — **High** — ✅ FIXED 2026-07-10
Two residual leaks survived F-9/F-10. First, F-9's increment had added a `GET /keys/{provider}` endpoint that returned the **plaintext** cloud key to the server per call (used by voice transcript-fixing and a now-dead `WebHarnessRuntime.GetApiKeyAsync`); a live-compromised server could fetch any key on demand over the tunnel — contradicting "the server never holds your keys." Second, the node-authoritative, fail-closed path policy from F-4/F-10 was applied only to `/terminal/exec`: the equally powerful, equally tunnel-reachable `/project-files/*` and `/project-git/*` endpoints still trusted the **request's** `AllowedPaths` and fell back to allow-all when it was empty — so a compromised server could send `AllowedPaths: []` (or `["/"]`) and read, write, or run git anywhere on the node.

**Fix:**
- **No endpoint returns a usable LLM key to the server.** Removed `GET /keys/{provider}` and `BridgeLlmKeyClient.GetKeyAsync`. `AgentService.GetUserApiKeyAsync` is gone; `WebHarnessRuntime.GetApiKeyAsync` now returns `null`. Voice transcript-fixing (`AgentService.FixTranscriptAsync`) routes through the node like every other cloud call — a `BridgeHttpHandler` (→ `/llm/proxy`, streaming) that injects the key locally. `PUT`/`DELETE /keys/{provider}` remain for configuring keys from the web UI; only the *read* path is removed.
- **File & git endpoints are node-authoritative.** New `NodeTerminalPolicy.ResolveAsync(db, requestAllowedPaths)` loads the node's declared Allowed Paths and returns `SecurityPolicy.FromNodeAndRequest(nodePaths, requestPaths)` — node paths are the maximum scope, the request may only narrow, and an empty node list blocks every path. All five `/project-files/*` sites and every `/project-git/*` site now resolve through it instead of `new SecurityPolicy(req.AllowedPaths)`.

**Behavior change:** if a node has declared no Allowed Paths, the project-file picker and git panel now return `403` (fail closed) instead of operating on server-supplied paths — matching the Terminal. Users declare projects on the bridge status page to restore them.

**Honest limit:** ~~user-initiated key replication (`/keys/sync-export` → `/keys/sync-import`) still relays plaintext keys through the server~~ — **superseded by F-12**, which deletes `/keys/sync-export|import` and `KeyReplicationService` entirely and makes keys per-node. No key transits the server for replication, and normal per-call operation never sends the server a key.

**Verified:** `Aria.Tests/Bridge/ProjectFileNodePolicyTests.cs` (4 cases): empty node paths block file reads even when the request claims wide scope, requests cannot widen node paths, reads under declared paths succeed, and `/project-git/run` is blocked under empty node paths. Full suite 245/245 green. Bridge version bumped to `0.39.0-beta`.

### F-12 · Channels authored only on the node (kills key redirection + replication) — **High** — ✅ FIXED 2026-07-10
Two residual server levers over keys remained. (1) `/llm/proxy` attached the node's stored key to a **server-supplied URL**, so a compromised server could point `KeyRef=OpenAI` at its own host and capture the key — and even the bridge's own channel URL was *synced from the server* (`SyncedLocalSource`), so it was poisonable. (2) `/keys/sync-export|import` + `KeyReplicationService` relayed plaintext keys through the server between a soul's nodes, and `PUT/DELETE /keys/{provider}` were tunnel-reachable (key substitution/deletion).

**Fix — channels are now node-authoritative, the server holds only names:**
- **Bridge owns channels.** New `BridgeChannel` table + `ChannelEndpoints`: `GET /channels` (read-only mirror, tunnel-allowed) merges seeded public providers (`Aria.Shared.PublicProviderCatalog`) with node-authored custom channels and each one's key-presence. `PUT/DELETE /channels/{name}` are **local-origin only** and deliberately kept out of `TunnelAllowlist`, so the server cannot author or edit a channel. A new `// Channels` tab on the bridge status page is the authoring surface (channel URL/models + API keys).
- **Proxy pins the destination.** `/llm/proxy` resolves the egress host from the node's own record — `PublicProviderCatalog.CanonicalUrlFor` (public) or `BridgeChannel.Url` (custom) — via `PublicProviderCatalog.PinToHost`, and ignores `req.Url`. An unknown `keyRef` with a key/`RequireKey` is refused. The server can no longer choose where a keyed call (and its key) goes.
- **Server stops storing/pushing channels.** The `UserLocalSources` table + `UserLocalSourceService` DB backing are removed; the service is now a bridge-backed cache (`BridgeChannelClient` → `GET /channels`). `BridgeSyncService` no longer sends channel config down (closes URL poisoning). The web channel panel is read-only with a link to `http://localhost:5741`.
- **Replication removed.** `KeyReplicationService`, `POST /api/maintenance/replicate-keys`, and bridge `/keys/sync-export|import` are deleted, with their `TunnelAllowlist` entries. `/keys/` (key writes) is no longer tunnel-reachable at all.

**Decisions:** full inversion in one pass; **clean slate** — existing synced channels are not adopted (existing keys in the bridge `LlmKeys` table survive; channels are re-added on the bridge page). Keys are now per-node (see honest limits).

**Verified:** `Aria.Tests/Shared/ProviderPinningTests.cs` (URL pinning: matching base kept, tampered host redirected, missing base prepended, query preserved, malformed → authoritative base) and `Aria.Tests/Bridge/ChannelEndpointsTests.cs` (public seeding, key presence, custom channel author/delete, public name reserved, channel writes require local origin); `TunnelAllowlistTests` updated (`/channels` allowed; `/keys/*`, `/channels/{name}`, sync paths blocked). Full suite 257/257 green (4 legacy local-LM integration tests skipped). Bridge version bumped to `1.0.0-beta` (breaks the sync/tunnel surface — clients update in lockstep).

---

## 6. Remediation roadmap (phased)

**Phase 0 — stop the bleeding (small, high-leverage)**
- ✅ F-2: allowlist tunnel-reachable paths in the local-REST proxy; refuse terminal, soul export/import/rotate, db-admin, key export.
- ✅ F-4a: move terminal allowed-paths / blocked-commands to node-side config (request may only narrow).
- ✅ F-9: correct the docs' honest-limits section and create a user-facing security overview.

**Phase 1 — close the critical holes**
- ✅ F-1: make soul export a local-human-only ceremony; never accept its passphrase over the tunnel.
- ✅ F-3: origin/Host checks on all mutating endpoints; defeats DNS-rebinding/CSRF.
- ✅ F-4b: Terminal opt-in, off by default; persistent `TerminalEnabled` toggle is the node-side standing grant for Quick Exec (expiring grant is a future increment).
- ✅ F-4c: add a bridge-side Terminal capability toggle gating both Quick Exec and PTY, and show the bridge-side status in the Terminal tool-options modal so server-side enablement is not mistaken for node-side authorisation.

**Phase 2 — tighten authorisation semantics**
- ✅ F-6: sign-what-you-show for Seals; per-capability, single-use, short-lived grants.
- ✅ F-5: gate destructive/identity endpoints as local-human-only with Seal confirmation for re-homing and key ops.

**Phase 3 — defense in depth**
- ✅ F-7: encrypt the node vault at rest via the OS keychain.
- ✅ F-8: node-side audit trail on the status page.

**Phase 4 — approval-on-new-context layers** *(designed in [defense-in-depth-plan.md](./defense-in-depth-plan.md); remaining-work tracker: [phase2-context-grants-remaining.md](./phase2-context-grants-remaining.md))*
- ✅ Layer A — node-approved device trust for browsers: `aria-device` cookie + `TrustedDevices` grant, verified against the soul key or any non-revoked node key; revoking the approver node drops its devices.
- ✅ Layer B — server→bridge request gating: `RequestClassifier` + node-signed, mesh-replicated context grants enforced in `DirectTunnel` (REST **and** streaming paths); per-session scopes; reactive in-chat approval; approval-node pinning; **enforcement ON by default**, per-node toggle in the bridge `// Security` tab.
- ✅ Pre-authorisation for unattended runs: vigil (`vigil:{id}`, ~2 h) and Hive collective (`hive:{id}`, ~8 h) seals minted while the human is present, replicated to the executing node; Hive "this run only" seals revoked at run end.

---

## 7. Design principles to carry forward

1. **The node owns capability, scope, and consent.** Requests describe intent; they never grant it.
2. **Off by default.** Every capability that runs code, reveals a secret, or destroys data is disabled until a human at the node turns it on, with an expiry.
3. **Sign what you show.** Any consent signature covers the exact human-readable statement the user approved.
4. **Loopback is not a human.** The tunnel and the browser are both remote from the node's point of view; neither is the local operator.
5. **Prefer allowlists to denylists** for anything security-bearing.
6. **Say the honest limit.** Where a guarantee isn't yet enforced, document it plainly.

> Cross-reference: `docs/ideas/terminal-pty-mode.md` (Seal-gated PTY, already implemented) and `docs/ideas/bridge-remote-nodes-security.md` (multi-node key custody). This plan supersedes the security framing in the Terminal idea docs. The follow-on layers (device trust, context-grant gating) live in [`defense-in-depth-plan.md`](./defense-in-depth-plan.md) and [`phase2-context-grants-remaining.md`](./phase2-context-grants-remaining.md); the user-facing summary is [`docs/readme/security.md`](../readme/security.md).
