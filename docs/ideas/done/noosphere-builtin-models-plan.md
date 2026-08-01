# Noosphere — built-in extract + embed models on Aria.Bridge

> **Status: implemented.** Opt-in built-in models live on the bridge Memory tab
> (`NoosphereBuiltinRuntime`, `/memory/builtin/*`). User-facing setup:
> [docs/readme/setup.md — Memory (Noosphere)](../../readme/setup.md#memory-noosphere).

## Context

Noosphere Inscribe extraction and vector embeddings previously required an external OpenAI-compatible
channel (LM Studio, Ollama, cloud). If that engine is down, Inscribe still queues but structured
memory fails silently from the agent's POV (async worker). Users who do not want to run a separate
inference app can opt into small on-node models.

**User decisions (fixed):**
- Sweet-spot extraction: `LFM2.5-1.2B-Instruct` Q4_K_M GGUF (~731 MB) via LLamaSharp / llama.cpp.
- Embeddings: `all-MiniLM-L6-v2` ONNX (quantized ~23 MB + vocab) via ONNX Runtime.
- Opt-in only; download from the bridge Memory tab (Whisper pattern). Not shipped in the installer.
- External channels remain available when built-in is off or models are missing.
- No Aria.Web control surface in v1.

**Verified seams:**
- `LocalWhisperService` / `LocalWhisperEndpoints` — catalog, app-data download with `.part` rename,
  progress poll, delete, lazy factory cache.
- `NoosphereExtractor` / `NoosphereEmbedder` — HTTP channel path; short-circuit when builtin ready.
- `NoosphereConfig` + `NoosphereConfigService` — node-local SQLite; `PUT /memory/config` already
  local-origin only.
- `TunnelAllowlist` `/memory/` prefix — **must exclude** `/memory/builtin` so a compromised server
  cannot trigger multi-hundred-MB downloads or enable builtin.
- Bridge status page Memory tab (`BridgeStatusPage.Memory.cs` + JS in `BridgeStatusPage.Data.cs`).

---

## Models (pinned catalog)

| Role | File(s) | Approx | SHA256 (pinned in code) |
|---|---|---|---|
| extract | `LFM2.5-1.2B-Instruct-Q4_K_M.gguf` | 731 MB | `b1b3de11…` (LiquidAI HF) |
| embed | `all-MiniLM-L6-v2-quantized.onnx` + `vocab.txt` | ~23 MB + 226 KB | Xenova HF |

Store under `%AppData%/aria-bridge/noosphere-models/`. Refuse load on hash mismatch.

**License:** LFM Open License — UI requires accept before extract download. Embeddings Apache-2.0.

---

## Design

```
Inscribe/Probe → Extractor/Embedder
                   ├─ BuiltinEnabled && files verified → NoosphereBuiltinRuntime (in-process)
                   └─ else → NoosphereChannelResolver HTTP (LM Studio / …)
```

No fake localhost OpenAI server.

### Config columns on `NoosphereConfig`
- `BuiltinEnabled` INTEGER NOT NULL DEFAULT 0
- `BuiltinLicenseAcceptedAt` TEXT NULL (ISO UTC)

### Runtime API
- `Status()` — per-role downloaded / progress / error / licenseAccepted / enabled / ready
- `StartDownload(role)` — background; extract requires license accepted
- `DeleteModel(role)` — unload + delete files
- `SetEnabled` / `AcceptLicense` via config save or dedicated endpoints
- `EmbedBatchAsync` / `CompleteChatAsync` — lazy-load ONNX session / LLamaSharp weights

### Endpoints (local-origin mutate; **not** tunnel-reachable)
| Route | Notes |
|---|---|
| `GET /memory/builtin/status` | Poll UI (includes per-role `loaded` / `anyLoaded`) |
| `POST /memory/builtin/download?role=` | extract \| embed |
| `POST /memory/builtin/unload?role=` | free RAM (role optional → both); files stay |
| `DELETE /memory/builtin/model?role=` | reclaim disk |
| `PUT /memory/builtin/config` | `{ enabled, acceptLicense? }` local-origin |

`TunnelAllowlist`: deny `/memory/builtin` (and subpaths) even though `/memory/` is a prefix.

### UI
Bridge Memory tab card **// Built-in models** above channel dropdowns: enable toggle, license
checkbox, per-role download/progress/delete, status when ready.

### Resolution
1. `BuiltinEnabled` and **both** roles verified on disk → builtin for extract + embed.
2. Else → existing channel / Auto path.
3. Builtin infer failure → `LastError` (Inscribe degraded ack + nav warn tip).

---

## Implementation steps

1. Catalog + `NoosphereBuiltinRuntime` (download/hash/inference).
2. Schema + `NoosphereConfigService` save fields; wire Extractor/Embedder short-circuit.
3. Endpoints + TunnelAllowlist exclusion + Memory tab UI.
4. Tests: hash reject, resolution preference, tunnel deny, local-origin on mutate.

## Out of scope (v1)
GPU NuGet backends, Aria.Web controls, bundling weights in the installer, separate contemplate model.