# Aria Security Model

> **TL;DR** — Your secrets and high-stakes actions live on **your** cogitator node (`localhost:5741`), not the hosted server. The server is a switchboard that forgets. This page explains, in plain language, what each security control does, what attack it stops, and what you will see as a user.

---

## 1. What we are protecting against

Aria's design assumes two realistic attackers:

| Attacker | What they can do |
|---|---|
| **A1 — Compromised server** | Someone gains code-execution on the hosted `Aria.Web` process. They can push any message down the outbound tunnel to your node. |
| **A2 — Malicious web page** | A page you simply *visit* in your normal browser tries to reach `localhost:5741` (CSRF / DNS-rebinding). |

Both converge on the same dangerous idea: **treating "a request arrived on loopback" as "the local human asked for this."** They are not the same. The controls below close that gap.

---

## 2. Security controls

| # | Control | What it does | Concrete protection | User experience | Technical implementation |
|---|---------|--------------|---------------------|-----------------|--------------------------|
| **F-1** | **Soul master key never leaves the node** | The soul's private key cannot be exported over the tunnel or by a random web page. | A compromised server cannot ask your node to dump the master key. A malicious site cannot trigger a backup. | You export your soul only from the bridge status page ([http://localhost:5741](http://localhost:5741)) after clicking a local approval prompt. | `/soul/export` is local-origin only and consumes a fresh `soul-export` Inquisitorial Seal. |
| **F-2** | **Tunnel allowlist** | The server can only relay a fixed set of paths to your node. | A compromised server cannot call `/soul/export`, `/db/*`, `/soul/unlink`, `/soul/rotate-key`, etc., over the tunnel. | No visible change; dangerous paths silently return `403 tunnelAllowlistBlocked` if ever requested remotely. | `DirectTunnel.HandleLocalRestAsync` consults `Aria.Shared.TunnelAllowlist` before forwarding any server-relayed request. |
| **F-3** | **Local-origin guard on mutating requests** | The node rejects `POST`/`PUT`/`DELETE`/`PATCH` that do not originate from localhost. | A malicious page you visit cannot unlink your soul, wipe data, or change config by calling `localhost:5741`. | If you somehow trigger a cross-origin mutation, the browser console shows `403 localOriginRequired`. | `LocalOriginMiddleware` checks `Host`/`Origin` on every mutating request, with a tiny allowlist for legitimate cross-origin endpoints. |
| **F-4** | **Node-side Terminal policy + opt-in** | Commands run only if the matching capability is enabled at the node, scoped to node-defined paths/blocklists. Project declarations also live on the bridge; the web displays them read-only. | A compromised server cannot run shell commands on your machine by default, cannot add project paths, and cannot bypass your path rules. An empty node-side Allowed Paths list blocks all paths rather than trusting the server. | You enable each capability separately on the bridge status page — **Agent Projects** (the agent's file/grep/git/bash tools inside declared projects), **Quick Exec** (one-shot commands from the web Terminal), and **PTY** (interactive shell, seal-gated) — all off by default. Set allowed paths and blocked commands there; the web Terminal tool modal only shows them with a link back to the bridge. | `ProjectsEnabled` / `QuickExecEnabled` / PTY (`PtyEnabledUntil`, via a `terminal_pty` seal) as three independent switches — the legacy `TerminalEnabled` master switch is retained only to seed the split flags on upgrade; `TerminalAllowedPathsJson` / `TerminalBlockedCommandsJson`, `GET /terminal/projects`, `SecurityPolicy.FromNodeAndRequest` (empty node paths = block all; non-empty node paths: web may only narrow). |
| **F-5** | **Seal-gated destructive soul operations** | Re-homing, unlinking, key rotation, and import require a fresh node-approved seal. | A compromised server cannot rotate your keys or point your node at an attacker's server. | A local approval page pops up; you click **GRANT SEAL** before the action proceeds. | `/soul/link-server`, `/switch-server`, `/unlink`, `/import`, `/rotate-key` consume capability-bound seals (`TryConsumeSeal`). |
| **F-6** | **Sign-what-you-show Seals** | The node signs exactly the human-readable statement you see, bound to one capability. | A stolen seal approved for "soul-export" cannot be reused to enable PTY (confused-deputy). | The approval page shows the exact capability, scope, and expiry in a styled box; the signature covers that text. | `SealStatement.Build` produces the canonical statement; `SealService` verifies it; `/terminal/pty-enable` consumes only `terminal_pty` seals. |
| **F-7** | **Vault encryption at rest** | Sensitive columns in the local SQLite vault are encrypted with a key protected by the OS keychain/DPAPI. | Someone who copies your vault file cannot read your soul private key, OAuth tokens, or data key without your OS user session. | First startup after upgrade may briefly migrate plaintext values; afterwards the vault is encrypted transparently. | AES-256-GCM value converter on `[Encrypted]` properties; DEK protected by `WindowsDpapiProtector`, `MacKeychainProtector`, or `LinuxSecretServiceProtector`. |
| **F-8** | **Node-side security audit trail** | Every sensitive capability invocation is logged on the node. | You can detect if your node was asked to do something unusual — even by a compromised server. | A new **// Security** tab on the bridge status page lists recent allow/deny events. | `SecurityAuditLog` writes to `AuditEvents`; `GET /audit/log` is consumed by the status page. |
| **F-9** | **Bridge-side custody of data and secrets** | Conversation content, OAuth tokens, and cloud LLM API keys are stored on the local bridge node, not on the hosted server. | A compromised server cannot read your chat history, your Outlook/Gmail tokens, or your OpenAI/Anthropic keys because the server never receives them — cloud calls are proxied through the node. | Your chat history loads only while your node is connected; API keys are entered once on the bridge and shown as "stored on node" in the UI. | `Cogitation` keeps only an index row on the server (title/timestamps); messages live in `BridgeCogitation`/`BridgeMessage`. OAuth tokens live in `BridgeOAuthToken`. LLM keys live in the bridge `LlmKeys` table; **there is no endpoint that returns a key to the server** — every cloud call is proxied through the node's `/llm/proxy` (`BridgeHttpHandler`), which injects the key locally so it only leaves the node as the outbound `Authorization` header. |
| **F-10** | **Bridge-side Terminal policy + encrypted LLM keys at rest** | Terminal allowed paths and blocked patterns are declared on the bridge; the web UI displays them read-only. Cloud LLM API keys in the bridge vault are encrypted by the OS-protected data encryption key. | A compromised server cannot add project paths, shorten the command blocklist, or trick the Terminal into running commands outside the node-defined scope. A copied vault file no longer exposes raw LLM API keys. | The Terminal tool modal shows the bridge-side allowed paths and blocked patterns with a link to edit them on [http://localhost:5741](http://localhost:5741). LLM keys entered on the bridge are encrypted automatically. | `BridgeSoul.TerminalAllowedPathsJson` / `TerminalBlockedCommandsJson`; `GET /terminal/config`; web passes an empty blocklist so the bridge's standing policy is authoritative. `LlmKeys` values are encrypted with `VaultEncryption` and a one-time `MigrateLlmKeysAsync` re-encrypts legacy plaintext rows. |
| **F-11** | **Node-authoritative file & git scope** | The project-file picker and git panel are bound by the same node-declared Allowed Paths as the Terminal, not by paths the server sends. | A compromised server cannot read, write, or run git outside your declared projects — even by supplying its own `AllowedPaths` or an empty list. | No visible change when your projects are declared; if none are declared, the picker/git panel return `403` until you add a project on the bridge. | `/project-files/*` and `/project-git/*` resolve their policy through `NodeTerminalPolicy.ResolveAsync` (node paths = maximum scope, request may only narrow, empty node list = block all) — the same `SecurityPolicy.FromNodeAndRequest` used by `/terminal/exec`. |
| **F-12** | **Channels authored only on the node** | LLM channels (name → URL, models, key) are created and stored ONLY on the bridge. The server keeps no channel config and never chooses where a keyed call is sent: the node resolves the destination host from its own record. | A compromised server cannot add a channel, edit a channel's URL, or point a stored key at a host it controls — so it cannot exfiltrate a key by redirecting the call, nor substitute/delete keys over the tunnel. Cross-node key replication (which relayed plaintext through the server) is removed. | Channels and API keys are configured on the bridge status page ([http://localhost:5741](http://localhost:5741), `// Channels`). The web channel panel is a read-only mirror with a link to the bridge. | `BridgeChannel` table + `GET /channels` (read-only, tunnel-allowed) and local-origin `PUT/DELETE /channels/{name}` (kept OUT of the tunnel allowlist). `/llm/proxy` pins the destination via `PublicProviderCatalog.PinToHost` (public) or the `BridgeChannel.Url` (custom); `/keys/{provider}` writes are local-origin only; `/keys/sync-export\|import` and `KeyReplicationService` are deleted. |
| **F-13** | **Node-approved device trust (Layer A)** | A browser passes the entry gate only when it carries a device grant a node signed once. IP is a display hint, never a gate — trust survives roaming and domestic-IP churn. | A stolen entry code or copied cookie is useless from an unapproved device. Grants verify against the soul key **or any non-revoked node key**, so revoking the node that approved a device drops that device automatically. | On a new browser you click **TRUST THIS BROWSER** in the Devices panel; your node opens its Seal page, you approve once, and that browser passes the gate from any network afterwards. | `aria-device` cookie + server-side `TrustedDevices` table; `TrustedDeviceService` re-verifies the stored signature on every request (a tampered DB row can't grant access); `AccessGateMiddleware.TryValidateTrustedDevice`; `POST /api/devices/trust-this` / `/revoke`. |
| **F-14** | **Context grants on server-pushed ops (Layer B, on by default)** | The bridge classifies every server-relayed request; a *Sensitive* one — provider-key spend (`/llm/proxy`), shell, the project file/git surface, MCP tool execution — is refused unless a node-signed grant covers the browser session. | A compromised server cannot silently spend your keys, browse your declared projects, run shell, or drive MCP tools: the grant is signed and checked **on the node**, and the server can relay bytes but cannot forge a signature. Enforcement is fail-closed. | The first sensitive op in a session shows an **in-chat approval prompt** (the node opens its local approval page); approving grants ~8 hours for that session, shared across chat, Explorer, `#` picker, and git. The toggle lives on the bridge status page (`// Security` tab) — never on the server. | `RequestClassifier` (body-aware for `/tools/call`: read-only built-ins stay Benign); gate in `DirectTunnel.GateSensitiveAsync` on both the REST and streaming paths; grants signed with the soul/node key, stored in `ContextGrants`, re-verified on use, and mesh-replicated to sibling nodes (`/context/grants/export\|import` + 60 s background loop); ceremonies open on the node you pinned (`ApprovalNodePicker`). |

---

## 3. The Inquisitorial Seal in one minute

The Seal is Aria's "human at the node" proof. It is used for the highest-stakes actions:

1. The server asks the node for a seal over the tunnel, describing the action.
2. The node opens a **local** page on `http://localhost:5741/seal/{id}`.
3. You read the exact statement and click **GRANT SEAL** or **REFUSE**.
4. If you grant, the node signs the statement with your soul private key.
5. The server verifies the signature against your public key. The action proceeds only if the signature is valid and the seal is for the right capability.

Because the private key never leaves the node, the hosted server **cannot** grant a seal on your behalf, no matter how compromised it is.

Two extensions worth knowing:

- **You choose where approvals open.** With several nodes, the approval-node picker in the app shell pins which machine hosts approval pages — so a headless node never tries to open a browser nobody is watching, and the ceremony always lands where you actually are.
- **Unattended runs are pre-authorised while you are present.** Booking a vigil or launching a Hive collective runs the same approval ceremony up front, scoped to that one run (`vigil:{id}`, about a 2-hour window; `hive:{id}`, about 8 hours) and replicated to whichever node will execute it — including a remote, unattended one. A Hive **"this run only"** seal is revoked the moment the run completes or fails, so the next launch asks again.

---

## 4. Honest limits

These are the current gaps and caveats — stated plainly so you can calibrate trust.

| Limit | What it means |
|---|---|
| **Vault encryption is file-level values, not full-disk** | We encrypt sensitive columns inside the SQLite file. Other columns, schema, and metadata are plaintext. It protects against a leaked vault file, not against a live compromise of your logged-in user account. |
| **Orphaned server-side Terminal config rows** | Older server database rows (`UserToolConfigs` for the Terminal tool) that stored `BlockedCommands` are no longer read. They are harmless dead data, but no migration removes them yet. |
| **Linux fallback is weaker** | If the freedesktop Secret Service is unavailable on Linux, the DEK is derived from `/etc/machine-id` + username and stored in a local file. This still separates key from vault, but is not OS-keychain backed. |
| **Server compromise can still push allowed messages** | F-2 narrows what the server can ask, and F-14 makes the node refuse sensitive asks that lack a human-approved grant. If you switch Layer B **off** on a node, that node returns to trusting allowlisted tunnel traffic — the node-side controls (F-4, F-6, F-11, F-12) still gate the worst of it, but unattended sensitive ops are no longer refused. |
| **A live context grant is session-scoped, not per-tool** | Within the ~8 h window, any op classified Sensitive proceeds without re-prompting — that is the deliberate anti-prompt-fatigue tradeoff. Paranoid-mode Seals still gate high-stakes acts individually, and you can revoke the grant early from the bridge. |
| **Approval needs a reachable node** | If no node is connected — or no approvable node for a headless setup — sensitive ops **fail closed** and wait. The bridge is load-bearing by design. |
| **Keys are per-node** | Channels and keys are authored on each node and no longer replicated between a soul's machines (F-12). If you run several nodes, you configure the channels you want on each. This is deliberate: no key ever transits the server for replication. |
| **Secondary nodes hold no soul key** | Multi-node routing relies on the primary node for soul-key operations. A joined node's local compromise does not expose the master key, but it may expose its own node key and synced data. |
| **Web display of channels/projects is a mirror** | The web fetches channel and project names/paths from the bridge to show in the UI. A compromised server could tamper with that cached *display* (e.g., hide a channel), but it cannot author a channel, choose a call's destination, or make the bridge enforce paths the node has not declared. |

---

## 5. Design principles

1. **The node owns capability, scope, and consent.** Requests describe intent; they never grant it.
2. **Off by default.** Every capability that runs code, reveals a secret, or destroys data is disabled until a human at the node turns it on.
3. **Sign what you show.** Any consent signature covers the exact human-readable statement the user approved.
4. **Loopback is not a human.** The tunnel and the browser are both remote from the node's point of view.
5. **Prefer allowlists to denylists** for anything security-bearing.
6. **Say the honest limit.** Where a guarantee isn't yet enforced, we document it plainly.

---

## 6. Deep-dive technical roadmap

For the full finding-by-finding remediation plan, file paths, test coverage, and version history, see [`docs/security/hardening-plan.md`](../security/hardening-plan.md).

For the original architecture security framing, see [`architecture.md`](./architecture.md#security-guarantees).
