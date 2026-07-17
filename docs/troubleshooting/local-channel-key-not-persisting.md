# Local-LLM channel API key "not saved" (KEY ON FILE badge never appears)

## Symptom

A bridged local-LLM channel (e.g. an LM Studio endpoint that requires an API token) is edited in
Aria.Web → Channels, an API key is entered and **INSCRIBE CHANNEL** is clicked. No error is shown,
the channel itself saves fine (URL, models, bridge binding), but:

- The **⚿ KEY ON FILE** badge never appears when the channel is reopened.
- Chat through that channel fails with the underlying provider's auth error, e.g.:
  ```
  COGITATOR FAULT: Retry failed after 4 tries. (The model endpoint rejected the request: An LM
  Studio API token is required ... Authorization header using the 'Bearer' scheme ...)
  ```
- `GET /api/maintenance/node-keys?userId=<soul>` (see [multi-node.md](../readme/multi-node.md#diagnostics))
  shows `"providers": []` for the node the channel is bound to, even right after a save.

## Two confirmed, fixed root causes

### A. Ambiguous multi-bridge save silently skipped the whole save

`SaveLocalSourceAsync` (`Aria.Web/Components/Layout/NavMenu.Channels.razor.cs`) requires an explicit
`BridgeNodeId` when more than one bridge is online for the soul. Before the fix, the **INSCRIBE
CHANNEL** button stayed clickable even with no bridge picked; clicking it hit this gate and returned
*before* the channel row or the key were saved, with only a message reused from the connectivity-probe
UI slot — easy to miss, and it looked identical to "nothing happened."

Fixed: `NavMenuChannelPanel.razor` now disables the button until a bridge is explicitly selected in
that ambiguous case.

### B. Paste-then-immediately-click raced the field's debounce commit

`DebouncedInput` (`Aria.Web/Components/Shared/DebouncedInput*`) only commits the DOM value to the
bound C# field after a 150ms debounce, or on blur. Typing-then-pausing accidentally gives the
debounce time to fire before a subsequent click. **Pasting the whole key and immediately clicking
Save** does not — blur's commit round-trip may not finish before the click handler runs, so
`SaveLocalSourceAsync` saw `_localSourceApiKey == ""` and its
`if (!string.IsNullOrWhiteSpace(_localSourceApiKey))` gate silently skipped the entire key-persist
step. No error, channel row still saved fine — exactly this symptom.

Fixed: Save buttons now explicitly flush the field (`DebouncedInputBase.FlushAsync()`) before reading
its value, instead of relying on blur timing. See `SaveLocalSourceWithFlushAsync` /
`SaveKeyWithFlushAsync` in `NavMenuChannelPanel.razor`.

## ROOT CAUSE FOUND & FIXED (2026-07-04): `/sync/apply` wiped the key vault

The key was being stored correctly — then **wiped a few milliseconds later by a different sync
mechanism**. The bridge request log for a real save showed the smoking gun:

```
PUT  /keys/Mac            200   ← key stored
GET  /keys                200   ← read-back verify sees it ✅
GET  /keys/sync-export    200   ← mesh replication (fine)
POST /keys/sync-import    200
POST /sync/apply   (3196b) 200  ← THIS wiped LlmKeys
GET  /keys                200   ← now empty
```

`Aria.Bridge/Endpoints/SyncEndpoints.cs` → `/sync/apply` mirrors the server's authoritative config
snapshot (agents/tools/sources/MCP) into the bridge for the Console to read. It also contained, from
**before secrets were moved to the bridge**:

```csharp
// Cloud API keys are authoritative from the server as well.
await db.Database.ExecuteSqlRawAsync("DELETE FROM LlmKeys;");
foreach (var key in snapshot.ApiKeys) { INSERT ... }
```

In the current soul model the server holds **no** keys, so `snapshot.ApiKeys` is empty for a bridged
soul (`BridgeSyncService.BuildSnapshotAsync` reads the legacy, now-unused server-side
`UserLlmApiKeys` table). So every `/sync/apply` did `DELETE FROM LlmKeys` and inserted nothing —
**wiping the vault**. And `/sync/apply` is pushed (`BridgeSyncService.PushSnapshotAsync`) after *any*
config change, including the channel-row save at the end of `SaveLocalSourceAsync` — so saving a
channel deleted its own just-stored key.

This is why nothing else reproduced it: only a config-sync push triggers `/sync/apply`, and none of
the isolated storage/replication tests did.

**Fix** (`SyncEndpoints.cs`, bridge ≥ 0.9.5-beta): `/sync/apply` no longer deletes `LlmKeys`. It
only upserts any keys the snapshot happens to carry (for legacy server-authoritative deployments),
leaving bridge-local keys intact. Cross-node key distribution remains handled by the encrypted
`KeyReplicationService` mesh. Verified: a `/sync/apply` with empty `apiKeys` now leaves a
just-stored key in place.

> **Deployment note:** this fix is **bridge-side**. Every one of a soul's bridges must be updated to
> ≥ 0.9.5-beta — a `fly deploy` of Aria.Web alone does not fix it. Any bridge still on ≤ 0.9.4 keeps
> wiping its own vault on the next config sync.

## Backend exhaustively proven correct (2026-07-04)

Every bridge-side persistence and replication path was reproduced directly against the live Mac
bridge (which is connected to the production server alongside the Windows node), and **none loses a
key**:

| Test (direct against `localhost:5741` and the live production mesh) | Result |
|---|---|
| Direct local `PUT /keys/X` then `GET /keys` | key persists |
| 30 concurrent `PUT`s of distinct keys | all 30 persist, no lock errors, no lost writes |
| `sync-export` then re-`sync-import` of the node's own blob | key persists |
| Import an **empty** (`count:0`) export blob into a vault that has keys | keys **not** cleared |
| Full 2-node mesh `replicate-keys`, Mac-preferred and Windows-preferred | keys persist and propagate to both |
| Asymmetric mesh (one node empty, one full) then replicate | empty node gets the keys back; **peer never wiped** |
| Bridge process restart | keys survive |

Conclusion: the storage layer, the sync/replication endpoints, and the tunnel relay are all correct.
`KeyReplicationService` + `/keys/sync-import` are upsert-only and cannot delete; an empty node
importing into a full one is a no-op; deletions do not propagate. The "both nodes empty" state is
**not reproducible** through any backend path.

## Fix applied: read-back verification at save time

Because the loss could not be reproduced server-side, `SaveLocalSourceAsync`
(`Aria.Web/Components/Layout/NavMenu.Channels.razor.cs`) no longer trusts the bridge's `200 {"ok":true}`.
After the `PUT /keys/{provider}` it reads the bound node's vault back (`GET /keys`, via
`NodeHasProviderAsync`) and confirms the provider is actually present, retrying the PUT once on
failure. Outcomes:

- **Verified present** → success, `KEY ON FILE` reflects reality.
- **200 but read-back absent** → a loud UI notice *and* a `[ChannelSave] PERSIST FAILURE` **error**
  log at the exact failure instant, with the node id and provider — the smoking gun for any recurrence.
- **Non-200 / offline** → the pre-existing explicit failure notice.

This closes the "silently thinks it saved but didn't" class regardless of root cause, and instruments
the precise moment if the elusive production-only loss ever happens again.

## Historical open mystery (see git history before the read-back fix)

Even after both fixes above, one reproduction remains: a channel bound to a **Windows** bridge
(`WindowsELFI2`), continuously running (confirmed 38+ min uptime, single process — a second instance
would fail to bind port 5741) still shows the key missing after a save that the bridge itself
reported as successful.

What was directly observed and verified, in order:

1. Server-side logging added to `SaveLocalSourceAsync` (`[ChannelSave]` tag) confirms, for the real
   user action: `keyPresent=True keyLen=35`, gate checks pass, `PUT /keys/Window → node
   c-bvj8Jhgl6AzLrI` returns **`status=200 body={"ok":true}`** — i.e. the bridge's own
   `PUT /keys/{provider}` handler (`Aria.Bridge/Endpoints/LlmKeyEndpoints.cs`) executed the
   `INSERT ... ON CONFLICT DO UPDATE` without throwing.
2. Checking `GET /api/maintenance/node-keys` shortly after (tens of seconds later) shows
   `"providers": []` for that same node — the key is gone.
3. A direct round-trip test (`POST /api/maintenance/test-key-roundtrip?...&provider=Window`) — PUT,
   then GET, then DELETE, all within a single server request — succeeds cleanly
   (`foundAfterPut: true`), proving the write/read mechanism itself is not fundamentally broken and
   works for that exact provider name in isolation.
4. Ruled out: bridge process restart (uptime confirmed continuous across the whole window), a second
   bridge process on the same machine (would fail to bind the port), any code path that calls
   `DELETE /keys/{provider}` other than the user's own "CLEAR KEY" button and the diagnostic
   roundtrip-test endpoint above (neither was in play), and `KeyReplicationService`/`/keys/sync-import`
   deleting anything — that code path only ever upserts keys present in an imported blob and returns
   early (no DB write at all) when the imported set is empty, so it cannot remove an existing key.

**Net: a write that the bridge itself acknowledged as successful does not survive until the next read
from the same continuously-running process, with no code path found (yet) that would delete it.**

### Caution for whoever continues this investigation

`/api/maintenance/test-key-roundtrip` **deletes** whatever it just wrote as its last step. It was run
once against the literal provider name `Window` (the real channel's name) during this investigation,
purely to test in isolation — by the time it ran, `node-keys` had *already* shown the key missing, so
it did not cause the original loss, but reusing this endpoint against a real provider name is
inherently destructive and should be avoided — always use a throwaway provider name (e.g.
`RoundtripProbe`).

### Where to pick this up

The next diagnostic step is **bridge-side** logging (this investigation only instrumented the
Aria.Web side, since that's redeployable without the user's involvement): add logging in
`Aria.Bridge/Endpoints/LlmKeyEndpoints.cs`'s `PUT`/`GET /keys` handlers that logs
`SELECT COUNT(*) FROM LlmKeys` (or the full provider list) immediately before and after each
operation, plus in `KeyReplicationService`/`/keys/sync-export`/`/keys/sync-import` on the bridge side.
Since this requires a new Aria.Bridge build running on the *user's Windows machine* (not just a
`fly deploy`), it needs the user to pull/rebuild/restart their bridge — more friction than the
Aria.Web-only changes made so far, which is why this was deferred.

Worth checking specifically once that logging exists:

- Does the periodic (non-save-triggered) `KeyReplicationService` timer tick overlap with the
  on-save replication call in a way that, despite the upsert-only code, somehow still clears the row
  (e.g. via a transaction/connection issue specific to concurrent SQLite access from two overlapping
  requests on the same file)?
- Is the Windows bridge's resolved `BridgeDatabaseInitializer.DbPath` stable, or could something
  (antivirus, OneDrive sync, a second copy of the data folder) cause reads and writes to
  intermittently hit different underlying files despite being the same logical path?

## Diagnostic tooling added during this investigation

- `GET /health` on Aria.Web now returns `{ status, startedAtUtc, uptimeSeconds }` instead of a bare
  `"ok"` — compare `startedAtUtc` against when you deployed to confirm a push actually took effect.
- `GET /api/maintenance/local-sources?userId=<soul>` — lists a soul's configured local-LLM channels
  with `bridgeNodeId`, `boundNodeLabel`, and `boundNodeOnline`, so a stale/dead binding is visible
  without shell access.
- `POST /api/maintenance/test-key-roundtrip?userId=<soul>&nodeId=<node>&provider=<name>` — PUT/GET/
  DELETE round-trip against a specific node's key vault. **Destructive on whatever provider name you
  pass** — always use a throwaway name, never a real channel's name.
- `[ChannelSave]` log lines in `SaveLocalSourceAsync` — logs whether a key was present (and its
  length only, never the value), the bridge-selection gate outcome, the resolved target node, and the
  raw PUT status/body, so a "silently didn't save" report is diagnosable from `fly logs` alone.

See also [multi-bridge-offline-diagnosis.md](multi-bridge-offline-diagnosis.md) for the broader
multi-node/circuit-verification troubleshooting flow this complements.
