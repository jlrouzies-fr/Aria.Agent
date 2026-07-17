# Multi-bridge / cross-device "Bridge offline" diagnosis

## Scenario

- A soul is created and linked on machine A (e.g. a Mac).
- A second machine B (e.g. Windows) joins the same soul via **Join existing soul**.
- After joining, one or both machines show **Bridge offline** / **PERFORM COMMUNION** in Aria.Web, even though the bridge process is running.

## Critical distinction

Aria.Web tracks **two different things**:

1. **Bridge transport connection** — the `Aria.Bridge` daemon is connected to the server over SignalR (`/api/modelbridge`).
2. **Circuit verification** — *this browser tab* has proven it controls a bridge enrolled for the selected soul.

The top bar button, chat placeholder, and soul-gated UI key off **circuit verification**, not transport state. A bridge can be connected and still show "offline" if the browser tab has not (re)verified.

## Expected flow for adding a second machine

1. Machine A creates soul `MyCogitator` and links to the server.
   - `Aria.Web/Endpoints/SoulEndpoints.cs` → `POST /api/bridge/register-soul`
   - Server stores `Users.PublicKey` and a primary `SoulNodeKeys` row.
2. Machine B runs a fresh bridge and calls **Join existing soul**.
   - `Aria.Bridge/Endpoints/NodeEndpoints.cs` → `POST /soul/join`
   - This is **local to machine B**. It creates a node keypair and sets `ServerSoulId` / `ServerUrl`. It does **not** touch the server yet.
3. Machine B's bridge connects to the server and fails auth (not enrolled yet).
   - `Aria.Web/Services/ModelBridge/ModelBridgeHub.cs` → `RegisterDirectBridge`
   - `Aria.Bridge/Infrastructure/DirectTunnel.cs` → `RegisterPendingEnrollmentAsync`
   - The bridge registers as a **pending device** and shows a 6-digit join code.
4. From a **verified browser session on machine A**, open Aria.Web → **Devices**, enter the join code, and approve.
   - `Aria.Web/Components/Layout/NavMenuDevicesPanel.razor`
   - `Aria.Web/Services/Node/NodeService.cs` → `ApprovePendingAsync`
   - The Mac bridge signs an enrollment certificate via `/node/sign-enrollment`.
   - Server adds machine B to `SoulNodeKeys`.
5. Machine B reconnects and authenticates successfully.
6. The browser on machine B then verifies via `http://localhost:5741/node/attest`.

## Common mistakes

- Using **Link to server** on machine B instead of **Join existing soul**. `link-server` sends machine B's own soul public key to `register-soul`, which will either conflict with the existing primary key or overwrite it if the slot is empty.
- Approving the pending device from machine B's own browser. Approval requires an already-verified session with an online bridge (usually machine A).
- Confusing the **soul name** (`MyCogitator`) with the **node label** (`MyWorkstation`, `Environment.MachineName`). The Devices panel shows machine names, not the soul name.
- Copying the **local bridge Soul ID** from the bridge status page instead of the **Server Soul ID** from Aria.Web → Devices. The bridge status page no longer exposes the local ID for this reason; use the ID shown in Aria.Web.

## Diagnostic checklist

### 1. Check the bridge's view of itself

On the affected machine, with the bridge running:

```bash
curl http://localhost:5741/node/info
curl http://localhost:5741/node/session-code
curl http://localhost:5741/health
```

Expected for the primary bridge:

```json
{
  "serverSoulId": "714209f0-5051-4219-9026-80cdf5d44020",
  "isPrimary": true,
  "platform": "macOS",
  "label": "MyWorkstation"
}
```

For a joined node, `isPrimary` should be `false` and it should show a join code at `/node/join-code` until approved.

Relevant files:
- `Aria.Bridge/Endpoints/NodeEndpoints.cs` (`/node/info`, `/node/attest`, `/node/session-code`, `/node/join-code`)
- `Aria.Bridge/Infrastructure/DirectTunnel.cs` (tunnel connection, pending enrollment, knock loop)

### 2. Check the browser attestation request

On the browser that shows "offline", open DevTools → Network, reload the page, and look for:

```
POST http://localhost:5741/node/attest
```

- **200 OK with `{publicKey, signature}`** → the bridge signed correctly. The problem is server-side verification.
- **Failed to fetch / CORS / mixed-content** → the browser cannot reach the local bridge. Check firewall, port, or whether the page is HTTPS and the browser blocks `http://localhost`.
- **404 No soul / No signing key** → the bridge local DB does not have the expected soul/keypair.

Relevant files:
- `Aria.Web/wwwroot/aria-interop.js` (`attestViaLocalBridge`)
- `Aria.Web/Components/Layout/NavMenu.Bridge.cs` (`AttestCircuitAsync`)
- `Aria.Web/Services/Auth/CircuitAuthService.cs` (`Begin`, `CompleteAsync`)

### 3. Check server-side logs

```bash
fly logs --app <your-app> | grep -E '\[Bridge/Direct\]|\[Bridge/Knock\]|\[CircuitAuth\]|\[Attest\]|\[Node\]'
```

Look for:

- `[Bridge/Direct] Authenticated as soul ...` — bridge transport is up.
- `[Bridge/Direct] Node ... not enrolled (or revoked)` — the key is not in the soul's allow-list.
- `[Bridge/Direct] No public key on record` — the `Users.PublicKey` row is missing/null.
- `[Bridge/Knock] Recorded UI access knock ...` — bridge is connected and the access gate is open for that IP.

Note: `CircuitAuth.CompleteAsync` currently returns `false` silently on failure; you may not see a log line unless logging is added.

Relevant files:
- `Aria.Web/Services/ModelBridge/ModelBridgeHub.cs` (`RegisterDirectBridge`, `UiAccessKnock`)
- `Aria.Web/Services/Auth/CircuitAuthService.cs`
- `Aria.Web/Middleware/AccessGateMiddleware.cs`

### 4. Check the server database

SSH into the fly.io instance and query SQLite:

```bash
fly ssh console --app <your-app>
sqlite3 /data/aria.db "SELECT Id, Name, PublicKey FROM Users WHERE Id = '714209f0-5051-4219-9026-80cdf5d44020';"
sqlite3 /data/aria.db "SELECT NodeId, IsPrimary, Revoked, NodePublicKeyBase64 FROM SoulNodeKeys WHERE UserId = '714209f0-5051-4219-9026-80cdf5d44020';"
```

Compare `Users.PublicKey` and the primary `SoulNodeKeys.NodePublicKeyBase64` with the `publicKey` returned by `/node/attest`. They must match byte-for-byte for the primary bridge.

Relevant files:
- `Aria.Web/Data/Users/User.cs`
- `Aria.Web/Data/Bridge/SoulNodeKey.cs`
- `Aria.Web/Endpoints/SoulEndpoints.cs` (`register-soul` seeds/updates these rows)

### 5. Check the Devices panel

Open the sidebar → **Devices**.

- A filled dot `●` means the bridge is connected to the server (transport up).
- An empty dot `○` means the node row exists but is not currently connected.
- **Pending Devices** shows unapproved joined bridges waiting for a join code.

Relevant files:
- `Aria.Web/Components/Layout/NavMenuDevicesPanel.razor`
- `Aria.Web/Services/Node/NodeService.cs` (`GetNodesAsync`, `ApprovePendingAsync`)
- `Aria.Web/Services/Node/PendingEnrollmentService.cs`

## Most likely root causes

### A. The browser tab lost circuit verification

The bridge is connected, but the Blazor Server circuit reconnected/refreshed and has not re-run `/node/attest`. Try:

1. Refresh the page.
2. Use the session-code fallback: copy the code from `http://localhost:5741`, click **PERFORM COMMUNION**, expand **ALREADY HAVE A BRIDGE RUNNING?**, paste the code, and unlock.

A successful code unlock is cached in the tab's `sessionStorage` (`aria_unlock_code`) and silently
re-verified on every reload, so the code only needs to be entered once per tab per bridge restart
(the code rotates when the bridge process restarts). Historical bug: `NavMenu.razor.cs` only retried
the cached code when the page was an *insecure* context, so HTTPS deployments forced a manual
re-entry on every refresh — fixed by retrying the cached code unconditionally after discovery.

### B. Server `Users.PublicKey` does not match the bridge's key

This happens if:

- A second bridge accidentally used **Link to server** and overwrote the primary key.
- A key rotation was started but not completed.
- The `SoulNodeKeys` primary row was deleted or revoked.

Fix: re-link the primary bridge, or restore the soul from an encrypted backup on the primary machine.

### C. Concurrent attestation race

`NavMenu.Bridge.cs` → `AttestCircuitAsync` uses a `_attesting` flag, but if two calls are initiated in rapid succession (e.g. page load + node-changed event), the single-use nonce in `CircuitAuthService` can be overwritten before the bridge signs it. One request succeeds, the other fails. If the UI swallows the failure, the circuit may remain locked.

### D. Browser blocks the HTTPS → localhost fetch

On some browsers, an HTTPS page fetching `http://localhost:5741` can be blocked even though `window.isSecureContext` is true. If `/node/attest` never appears in Network, or appears red, this is the cause. The session-code fallback exists exactly for this case.

Known blockers, all producing "Failed to fetch" in the console (`[aria] discover: ...` / `[aria] attest: ...` error lines from `aria-interop.js`):

- **Safari**: blocks mixed content to `http://localhost` outright (no loopback exemption).
- **Chrome/Edge with Local Network Access (LNA)**: newer Chrome versions gate public-site → loopback requests behind a "local network" permission prompt. If denied (or auto-denied), the fetch fails. Check `chrome://settings/content/localNetworkAccess` (or the tune icon in the address bar) and allow the Aria.Web origin.
- **Brave shields / privacy extensions**: block localhost requests as fingerprinting vectors.

If this hits **every machine/browser**, suspect LNA or an extension policy rather than an enrollment problem — the giveaway is that the session-code unlock works (which proves both the bridge transport and the enrollment are fine, since the server matches the code over the live tunnel).

## Key code paths

| Concern | File | Function |
|---------|------|----------|
| Bridge local soul / join | `Aria.Bridge/Endpoints/SoulEndpoints.cs` | `POST /soul`, `POST /soul/join`, `POST /soul/link-server` |
| Bridge node identity / attest | `Aria.Bridge/Endpoints/NodeEndpoints.cs` | `/node/info`, `/node/attest`, `/node/session-code`, `/node/join-code` |
| Bridge → server SignalR tunnel | `Aria.Bridge/Infrastructure/DirectTunnel.cs` | `ConnectAndRunAsync`, `RegisterPendingEnrollmentAsync` |
| Server bridge auth | `Aria.Web/Services/ModelBridge/ModelBridgeHub.cs` | `RegisterDirectBridge`, `UiAccessKnock` |
| Server bridge registry | `Aria.Web/Services/ModelBridge/ModelBridgeRegistry.cs` | `RegisterNode`, `GetNodes`, `SoulVerified` |
| Browser → bridge attest | `Aria.Web/wwwroot/aria-interop.js` | `attestViaLocalBridge` |
| Blazor attestation trigger | `Aria.Web/Components/Layout/NavMenu.Bridge.cs` | `AttestCircuitAsync`, `DiscoverAndSelectUserAsync` |
| Server verification | `Aria.Web/Services/Auth/CircuitAuthService.cs` | `Begin`, `CompleteAsync`, `UnlockByCodeAsync` |
| Device approval | `Aria.Web/Services/Node/NodeService.cs` | `ApprovePendingAsync`, `RequestEnrollAsync` |
| Devices UI | `Aria.Web/Components/Layout/NavMenuDevicesPanel.razor` | — |
| Access gate | `Aria.Web/Middleware/AccessGateMiddleware.cs` | `InvokeAsync` |
| Server soul registration | `Aria.Web/Endpoints/SoulEndpoints.cs` | `POST /api/bridge/register-soul` |

## Random 403 "ACCESS DENIED" on your own machines (fixed)

With two bridges online, an earlier `UiAccessKnockService` kept only the **latest knock per user**
— each machine's knock (IPv4 and IPv6 count as different IPs even on one LAN) evicted the other's,
so the access gate flip-flopped between machines every ~40 s. Knocks are now stored per IP with a
per-user cap. If you see gate 403s from a machine whose bridge is connected, check the server logs
for `[Bridge/Knock]` entries and compare the recorded IP with the one on the 403 page (they can
differ by IP *family* or IPv6 privacy suffix).

## Remote diagnostics without shell access

Production-safe endpoints (behind the access gate) for cross-machine debugging — full list and the
routing rules they exercise: [`docs/readme/multi-node.md`](../readme/multi-node.md#diagnostics).

```bash
curl "https://<host>/api/maintenance/nodes?userId=<soulId>"
curl "https://<host>/api/maintenance/node-keys?userId=<soulId>"
curl -X POST "https://<host>/api/maintenance/test-channel?userId=<soulId>&source=<channel>"
curl "https://<host>/api/maintenance/node-llm-log?userId=<soulId>&nodeId=<node>"
```

## Wiping / unlinking a soul

When the bridge's **WIPE SOUL + ALL DATA** button is used (or `DELETE /db/soul`):

1. The local bridge deletes the entire `Souls` row, which removes the private key from the local SQLite database.
2. It calls `POST /api/bridge/unlink-soul` on the server, which:
   - Nulls `Users.PublicKey`.
   - **Revokes every enrolled node** in `SoulNodeKeys` for that soul.
   - Drops any live bridge connections for those nodes.
   - Clears any pending enrollment requests.

This ensures a wiped/re-registered soul cannot be silently rejoined by old device keys. If you need to add devices back after a wipe, run **Join existing soul** on each device and approve them again.

Relevant files:
- `Aria.Bridge/Endpoints/DbAdminEndpoints.cs` → `DELETE /db/soul`
- `Aria.Web/Endpoints/SoulEndpoints.cs` → `POST /api/bridge/unlink-soul`
- `Aria.Web/Services/Node/PendingEnrollmentService.cs`

## Quick recovery

If the primary bridge's key has been overwritten or corrupted:

1. On the primary machine, export the soul backup from the bridge status page or `POST /soul/export`.
2. On any other machine, unlink (`POST /soul/unlink`) if needed.
3. On the primary machine, re-import the backup if necessary.
4. Re-link the primary bridge to the server (`POST /soul/link-server`).
5. For each additional machine, use **Join existing soul**, then approve from the primary machine's verified browser session.
