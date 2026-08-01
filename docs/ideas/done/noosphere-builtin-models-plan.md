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
- Extract choice: LiquidAI LFM family (same ChatML-like template) — **LFM2.5-1.2B** and **LFM2-2.6B**,
  each as Q4_K_M / Q5_K_M / Q6_K (six independently downloadable GGUFs). Recommended: 2.6B Q5_K_M.
  1.2B rows show a warn tooltip (weak at kinds/relations even at higher quants).
- Embeddings: `all-MiniLM-L6-v2` ONNX (quantized ~23 MB + vocab) via ONNX Runtime (not choosable).
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

See `NoosphereBuiltinCatalog.ExtractVariants` for full URL + SHA256 pins.

| Id | File | Approx | UI |
|---|---|---|---|
| `lfm25-1.2b-q4km` | `LFM2.5-1.2B-Instruct-Q4_K_M.gguf` | ~731 MB | default (existing installs); warn |
| `lfm25-1.2b-q5km` | `LFM2.5-1.2B-Instruct-Q5_K_M.gguf` | ~843 MB | warn |
| `lfm25-1.2b-q6k` | `LFM2.5-1.2B-Instruct-Q6_K.gguf` | ~963 MB | warn |
| `lfm2-2.6b-q4km` | `LFM2-2.6B-Q4_K_M.gguf` | ~1.56 GB | |
| `lfm2-2.6b-q5km` | `LFM2-2.6B-Q5_K_M.gguf` | ~1.83 GB | **recommended** |
| `lfm2-2.6b-q6k` | `LFM2-2.6B-Q6_K.gguf` | ~2.11 GB | highest quality |
| embed | `all-MiniLM-L6-v2-quantized.onnx` + `vocab.txt` | ~23 MB | |

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
- `BuiltinExtractModelId` TEXT NULL (catalog id; null → `lfm25-1.2b-q4km`)

### Runtime API
- `Status(enabled, license, selectedExtractId)` — `extractVariants[]` + embed role, ready for selection
- `StartDownload(role, license, extractModelId?)` — background; extract requires license accepted
- `DeleteModel(role, extractModelId?)` — unload + delete files for that variant
- `EmbedBatchAsync` / `CompleteChatAsync(..., extractModelId)` — lazy-load ONNX / selected GGUF

### Endpoints (local-origin mutate; **not** tunnel-reachable)
| Route | Notes |
|---|---|
| `GET /memory/builtin/status` | Poll UI (`extractVariants`, `selectedExtractModelId`, embed `roles`) |
| `POST /memory/builtin/download?role=&model=` | extract (+ model id) \| embed |
| `POST /memory/builtin/unload?role=` | free RAM (role optional → both); files stay |
| `DELETE /memory/builtin/model?role=&model=` | reclaim disk for one variant |
| `PUT /memory/builtin/config` | `{ enabled, acceptLicense?, extractModelId? }` local-origin |

`TunnelAllowlist`: deny `/memory/builtin` (and subpaths) even though `/memory/` is a prefix.

### UI
Bridge Memory tab card **// Built-in models**: enable toggle, license checkbox, six extract rows
(radio = active, warn tip on 1.2B, recommended on 2.6B Q5), MiniLM embed row, per-row
download/progress/delete/unload.

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