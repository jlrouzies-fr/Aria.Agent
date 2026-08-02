# Noosphere builtin extract — Qwen bakeoff notes

> **Status: bakeoff green (2026-08-02).** Parse tolerance fixed; short/medium/long Inscribe OK on
> `qwen25-3b-q4km`, `qwen25-3b-q5km`, and smoke-OK on `qwen25-1.5b-q4km`. **Recommended** badge is
> **3B Q4** (Q5 did not clearly beat Q4). Resume here for Windows node / leftover LFM cleanup.

Related: [noosphere-builtin-models-plan.md](done/noosphere-builtin-models-plan.md),
[setup.md — Memory](../readme/setup.md#memory-noosphere).

---

## Where we left off

### Decision
- LFM (1.2B / 2.6B) was **too weak** for structured Inscribe JSON (bare entities, truncated
  mid-object, sticky Memory warnings). Do not put a Recommended badge on LFM.
- Switched catalog to official **Qwen2.5-Instruct** GGUFs (Apache-2.0, ChatML — same template
  path as our runtime). MiniLM embeddings unchanged.
- After live Mac bakeoff: keep **Recommended = `qwen25-3b-q4km`**. Q5/Q6 remain optional higher
  quants; 1.5B keeps the warn tip (weaker kinds / noisier graph even when it does not go raw).

### Code shipped
| Area | State |
|---|---|
| `NoosphereBuiltinCatalog` | Six variants: `qwen25-1.5b-{q4km,q5km,q6k}` + `qwen25-3b-{q4km,q5km,q6k}` |
| Default / Recommended | Default `qwen25-1.5b-q4km`; **Recommended** `qwen25-3b-q4km` |
| License UI | Apache-2.0 acknowledge (no LFM Open License) |
| Runtime | 8k ctx, user truncate 6k chars, TruncateAndReprefill, salvage truncated `facts[]` |
| Extractor | Builtin **no brace-prefill**; SoftRepair for `{["facts":…`, `}{` missing commas, `{{"facts"` |
| ParseFacts | Unwraps root `[ {"facts":[…]} ]` (and multi-wrapper arrays) — was the Qwen medium raw path |
| Stale LFM ids in SQLite | `ResolveExtractId` falls back to default Qwen id |

### Local Mac debug bridge (`localhost:5741`)
- Downloaded under `~/Library/Application Support/aria-bridge/noosphere-models/`:
  - `qwen2.5-3b-instruct-q4_k_m.gguf` (~2.1 GB)
  - `qwen2.5-3b-instruct-q5_k_m.gguf` (~2.4 GB)
  - `qwen2.5-1.5b-instruct-q4_k_m.gguf` (~1.1 GB)
  - MiniLM ONNX + vocab
- Old LFM GGUFs may still sit in that folder unused — safe to delete manually (~2–3 GB).

---

## Bakeoff results (2026-08-02, after wrapper unwrap)

`rawIngests` stayed flat (7); `lastExtractionError` cleared after successful runs.

| Model | Short | Medium (Spectra / JL-Heretic) | Long (~11k) |
|---|---|---|---|
| `qwen25-3b-q4km` | OK · 1 engram | **OK · 4 engrams** | OK · 5 engrams |
| `qwen25-3b-q5km` | OK · 1 engram | OK · 3 engrams | OK · 4 engrams |
| `qwen25-1.5b-q4km` | OK · 3 engrams | OK · 3 engrams | OK · 6 engrams |

### Quality checks (graph)
- Medium entities typed (`Spectra.MLX` → project, `JL-Heretic` → person, `Alice` → person).
- Relation present: `JL-Heretic -[uses]-> Spectra.MLX`.
- Q5 did **not** clearly beat Q4 on medium/long fact yield → Recommended moved to Q4.

### Failure shapes fixed this session
1. **`[ {"facts":[…]} ]`** — unwrap nested `facts` when the array element has no `content`.
2. SoftRepair: missing `}{` commas; accidental `{{"facts":`.
3. TryParseFacts keeps the first useful fail reason instead of overwriting with SoftRepair noise.

Earlier (fixed previously): `{["facts":…` from brace-prefill — SoftRepair + prefill disabled.

---

## What remains

### Ops / multi-node
1. Windows node (`Windows-RTX2`): download Qwen there — multi-bridge processing pills + no LFM left
   as the story.
2. Delete leftover LFM GGUFs from Mac `noosphere-models/` to reclaim disk.
3. Optional: `ARIA_BUILTIN_LIVE=1` integration test against app-data with Qwen file present.

### Optional / later
- Compare Qwen vs channel extract (LM Studio instruct) on the same Inscribe text.
- If Qwen3 ever considered: **reject thinking variants** for builtin extract (same as channel
  guidance on the Memory tab).
- Revisit 1.5B warn tip if entity-kind quality improves; raw rate alone no longer justifies removal.

---

## How to resume quickly

```bash
# Debug bridge
dotnet run --project src/AriaAgent/Aria.Bridge --no-launch-profile

# Select + download (local origin)
curl -s -X PUT http://localhost:5741/memory/builtin/config \
  -H 'Content-Type: application/json' \
  -d '{"enabled":true,"acceptLicense":true,"extractModelId":"qwen25-3b-q4km"}'
curl -s -X POST 'http://localhost:5741/memory/builtin/download?role=extract&model=qwen25-3b-q4km'

# Inscribe + watch
curl -s -X POST http://localhost:5741/memory/inscribe -H 'Content-Type: application/json' \
  -d '{"content":"…"}'
curl -s http://localhost:5741/memory/stats
# Bridge log: ~/Library/Application Support/aria-bridge/aria-bridge.log
```

Unit gate: `dotnet test … --filter FullyQualifiedName~NoosphereBuiltin|FullyQualifiedName~NoosphereExtractionJson`

---

## Product call

**Recommended = 3B Q4** is bakeoff-backed for Mac (medium/long produce engrams, not raw). Builtin remains an
offline fallback relative to a strong channel instruct model, but the Memory tab Recommended badge
is no longer aspirational.
