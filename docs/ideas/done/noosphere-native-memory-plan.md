# Noosphere — native memory system in Aria.Bridge (Hindsight replacement)

## Context

Aria currently integrates the external Hindsight memory server (Python, `localhost:8888`) via `Aria.Tools/HindsightTools.cs` (Retain/Recall/Reflect tools). Problems: it's an external dependency, and its HTTP **bypasses the bridge tunnel** — the tools hit raw `localhost:8888` from wherever the harness runs, so in Web mode memories would live on the *server*, violating the "server holds nothing" principle.

We rebuild an equivalent system natively inside **Aria.Bridge** (the cogitator node): LLM-extracted facts with an entity graph, hybrid retrieval (vector + BM25 + graph, RRF-merged), stored in the local SQLite vault. Theme: subsystem = **Noosphere**, individual memories = **Engrams** (both genuine AdMech terms, consistent with Cogitations/Souls/Hive).

**User decisions (fixed):**
- Embeddings via an OpenAI-compatible `/v1/embeddings` endpoint (LM Studio/Ollama local or cloud w/ vault key); recall degrades gracefully to FTS5-only when unavailable.
- Retain = LLM extraction of atomic facts + entities + entity-relations + optional time anchor. No world-fact/experience classification. Graph recall leg in scope.
- UI: nav flyout panel now (`NavMenuMemoryPanel`), via a `BridgeMemoryClient` service so a full `/memory` page can come later.
- Full replacement of Hindsight; keep the Retain/Recall/Reflect tool contract the agent knows.

**Verified seams:**
- `Aria.Harness/Core/Harness.cs:65` — `BuiltinBridgeTool(name, description, schema, nodeId)` + `bridgeUp` flag: how datetime/SearchWeb run node-side with live tool blocks. Memory tools use this (fixes the tunnel bypass for free, works on Web + Console, gets governance wrapping).
- `Aria.Bridge/BuiltinTools/BuiltinTools.cs:35` — `InvokeAsync` name switch; tools dispatched here need **not** be in `GetToolInfos()` (so memory tools won't leak into terminal-tool listings).
- `ModelBridgeRegistry.Routing.cs:105` `SendLocalRestAsync` (15 s cap both sides) — UI panel transport; `BridgePostAsync` (10-min timeout) — agent tool transport.
- Bridge DB: EF `BridgeDbContext` + idempotent raw-SQL bootstrap in `BridgeDatabaseInitializer.cs` (vault `%AppData%/aria-bridge/aria-bridge.db`). `SQLitePCLRaw.bundle_e_sqlite3` ships FTS5.
- Key custody: `LlmKeys` table + `GetPlaintextKeyAsync` (private in `LlmKeyEndpoints.cs:362` — extract to shared helper).
- Bridge-local config precedent: `BuiltinTools.ConfigureWebSearch(app.Configuration)` in `Program.cs`.

---

## Phase 1 — Schema

**Modify `Aria.Bridge/Data/BridgeDbContext.cs`** — new entities + DbSets:
- `MemoryIngest` (Id, SoulId, Bank='default', Content, Status: pending|done|raw|error, Error?, CreatedAt, UpdatedAt) — raw Retain payloads: audit + retry queue.
- `Engram` (Id, SoulId, Bank, IngestId?, Content, TimeAnchor?, Embedding: byte[]? float32-LE blob, EmbeddingModel?, CreatedAt).
- `MemoryEntity` (Id, SoulId, Bank, Name, CanonicalName=lower(trim), Kind?, CreatedAt) — unique (SoulId, Bank, CanonicalName).
- `EngramEntity` (EngramId, EntityId) composite PK.
- `EntityLink` (Id, SoulId, Bank, FromEntityId, ToEntityId, Relation, EngramId?, CreatedAt).

**Modify `Aria.Bridge/Services/BridgeDatabaseInitializer.cs`** — append idempotent `CREATE TABLE IF NOT EXISTS` block matching the EF model exactly (same discipline as BridgeOAuthTokens), plus indexes, **plus FTS5** (raw SQL only — EF never models it):

```sql
CREATE VIRTUAL TABLE IF NOT EXISTS EngramsFts USING fts5(EngramId UNINDEXED, Content, tokenize='porter unicode61');
-- AFTER INSERT / AFTER DELETE / AFTER UPDATE OF Content triggers keep it in sync
```

Wrap the virtual-table creation in try/catch → static `NoosphereCapabilities.FtsAvailable` flag; recall falls back to `LIKE` if FTS5 is missing.

## Phase 2 — Bridge core services (new folder `Aria.Bridge/Services/Noosphere/`)

- **`NoosphereOptions.cs`** — bound from bridge `appsettings.json` section `Noosphere` with `Extraction: {Url, Model, KeyRef, ApiKeyFile}` and `Embeddings: {Enabled, Url, Model, KeyRef, ApiKeyFile}`. Fallback when Extraction.Url is empty: first `SyncedLocalSources` row (by SortOrder) that `IsBridged`, with its first model — freshly-linked souls get extraction for free. Reflect uses the same channel.
- **`LlmKeyStore.cs`** — extract `GetPlaintextKeyAsync` out of `LlmKeyEndpoints.cs` so Noosphere resolves `KeyRef` in-process (LlmKeyEndpoints calls the shared helper).
- **`NoosphereEmbedder.cs`** — `EmbedAsync(text)` + batch overload: POST `{Url}/embeddings` `{model, input}`, Bearer from KeyRef/ApiKeyFile. Returns null on any failure. Blob codec via `MemoryMarshal`. In-memory vector cache per (soul, bank), invalidated on ingest/delete. ~8 s timeout on query-time embed so panel recall stays under the 15 s LocalRest cap.
  *Vector storage design note — why SQLite blobs, not a vector DB*: SQLite is **durable storage only** (float32 `BLOB` column — survives restarts, lives inside the vault, deleted with the row); search never touches SQLite. Vectors are loaded once into an in-memory cache per (soul, bank) and recall scans it with SIMD `TensorPrimitives.CosineSimilarity` (`System.Numerics.Tensors` package). At personal scale this is exact and fast: 10k engrams × 1024 dims ≈ 40 MB and single-digit ms per query; even 100k engrams is tens of ms. A dedicated vector DB (Qdrant/Milvus/Chroma) only pays off at millions of vectors via ANN indexes, and would cost us a second external daemon (the dependency class we're eliminating by killing Hindsight), move memories outside the vault, and return approximate results with no measurable speed win.
  **Future option — sqlite-vec**: if the corpus ever grows past ~100k engrams, the vector leg's internals (fully contained inside `NoosphereService.RecallAsync`) can be swapped to the sqlite-vec loadable extension (`vec0` virtual tables) or an HNSW library (HNSW.Net) — no schema or API change. Not now: at current scale sqlite-vec is also brute-force underneath (same performance as the in-memory scan) while adding per-platform native extension loading through SQLitePCLRaw.
- **`NoosphereExtractor.cs`** — one non-streaming `chat/completions` call, temp 0.1, try `response_format: json_object`, retry without on 4xx; lenient parse (strip fences, first `{` to last `}`). Prompt (Noosphere-ingestion-cogitator themed) instructs: atomic self-contained facts, resolve pronouns and relative dates (current UTC datetime injected) into ISO `timeAnchor`, canonical entity names, strict JSON:
  ```json
  {"facts":[{"content":"...","entities":[{"name":"...","kind":"person|place|org|concept|thing|event|other"}],"relations":[{"from":"...","relation":"...","to":"..."}],"timeAnchor":"optional"}]}
  ```
- **`NoosphereService.cs`** — singleton facade (`IServiceScopeFactory` for DbContexts):
  - `EnqueueRetainAsync` → insert MemoryIngest(pending), push id to unbounded `Channel<string>`, return immediately (preserves fire-and-forget tool semantics).
  - `RecallAsync(query, bank, maxTokens=4096)` — three legs, top 32 each: **vector** (embed query → cosine over cache; skip if embedder down), **FTS** (tokenize → quoted `OR` match → `bm25()` order; `LIKE` fallback), **graph** (entities whose canonical name occurs in lowercased query → 1-hop via EntityLinks at half weight → engrams via EngramEntities, ranked by seed-match count + recency). Merge: RRF `Σ 1/(60+rank)`, then accumulate `content.Length/4` token estimate up to maxTokens.
  - `ReflectAsync` — internal recall → one chat completion ("answer only from recalled engrams; say when the archive is silent") → text.
  - `ListEngramsAsync(offset, limit, entityId?, q?)`, `DeleteEngramAsync` (cascade + orphaned entity/link cleanup), `ListEntitiesAsync` (top-N by engram count), `StatsAsync`.
- **`NoosphereIngestWorker.cs`** — `BackgroundService`: startup sweep (`pending`/`error` → channel; backfill `Embedding IS NULL` and mismatched `EmbeddingModel` rows when embedder up), then consume: extract → upsert entities by canonical name → insert engrams + links → batch embed → `done`. **Extraction failure/unconfigured → store raw content as one engram, status `raw`** (FTS-indexed via trigger) — never lose data.
- Register in `BridgeServiceRegistration.cs` (singletons + hosted service + options binding); `Program.cs`: `BuiltinTools.ConfigureMemory(...)` after build (mirror `ConfigureWebSearch`).

## Phase 3 — Bridge endpoints + builtin dispatch

**New `Aria.Bridge/Endpoints/MemoryEndpoints.cs`** (register in `EndpointsMapper.cs`; pattern per `CogitationEndpoints.cs`):

| Route | Contract |
|---|---|
| `POST /memory/retain` | `{content, bank?}` → 202 `{ok, ingestId}` |
| `POST /memory/recall` | `{query, bank?, maxTokens?}` → `{results:[{id,text,score,entities,createdAt}], legs:{vector,fts,graph}}` |
| `POST /memory/reflect` | `{query, bank?}` → `{text}` |
| `GET /memory/engrams?offset&limit&entityId&q` / `DELETE /memory/engrams/{id}` | UI list / delete |
| `GET /memory/entities?limit` | `[{id,name,kind,engramCount}]` |
| `GET /memory/stats`, `GET /memory/status` | counts + effective config/health |

**New `Aria.Bridge/BuiltinTools/BuiltinTools.Memory.cs`** — add `"Retain"`, `"Recall"`, `"Reflect"` cases to the `InvokeAsync` switch (BuiltinTools.cs:35), calling `NoosphereService`. Do **not** add to `GetToolInfos()`. Themed responses: Retain ack "Engram committed to the Noosphere…"; Recall joins `results[].text` with `\n\n` or `"// NO RECORDS FOUND in the Noosphere for that query."` (preserves old contract).

## Phase 4 — Tool layer + harness + server migration

- **Delete `Aria.Tools/HindsightTools.cs`** (carry its three 40K `[Description]` strings into the new registrations).
- **`Aria.Harness/Core/Harness.cs:146`** — replace `case "hindsight":` with `case "memory":` registering the three tools via `BuiltinBridgeTool(...)` guarded by `bridgeUp` (no config read). Same pattern as SearchWeb at line 87.
- **`Aria.Web/Services/Tool/ToolRegistry.cs:59-66`** — id `"memory"`, label "Memory (Noosphere)", **empty ConfigFields**, new setup guide (bridge-local `Noosphere` appsettings, embeddings how-to, degradation note). Delete `HindsightSetupSteps()`.
- **`Aria.Web/Data/DatabaseInitializer.cs`** — idempotent tool-id migration so enabled state survives:
  `UPDATE OR IGNORE UserToolConfigs SET ToolId='memory' WHERE ToolId='hindsight'; DELETE ... WHERE ToolId='hindsight';` (same for `SubAgentToolStates`).
- Cosmetic renames: `NavMenuToolsPanel.razor:76` aka-label → `(noosphere)`; `ChatCatalog.cs:32,74`; comments in `BridgeComponent.razor:179`.

## Phase 5 — Web UI (flyout panel)

- **New `Aria.Web/Services/Memory/BridgeMemoryClient.cs`** — modeled on `BridgeHiveClient` (incl. `TryNodesAsync` node failover): stats/list/entities/recall/delete via `SendLocalRestAsync(userId, ..., "/memory/…")`. DTOs: `EngramDto`, `MemoryEntityDto`, `MemoryStatsDto`. Register next to BridgeHiveClient in `ServiceCollectionExtensions.cs`.
- **New `Aria.Web/Components/Layout/NavMenuMemoryPanel.razor`** — `[CascadingParameter(Name="NavMenu")]`; stats header ("// NOOSPHERE — {n} ENGRAMS SEALED"), search box (recall w/ scores), top-entity chips (click filters list), recent engrams w/ delete, degraded notice when embeddings off ("// AUGUR ARRAY OFFLINE — lexical retrieval only"), empty state "// THE ARCHIVUM IS SILENT".
- **`NavMenu.razor`** — new sidebar item "// NOOSPHERE" toggling `"noosphere"` + `else if` branch **after** the `!SoulVerified` gate. No `.razor.cs` change needed.

## Phase 6 — Console

- **`Aria.Console/Program.cs`** — replace Hindsight init (≈lines 55-86) with `GET localhost:5741/memory/status` probe → `memoryAvailable`; replace auto-retain (line ~231) with fire-and-forget `POST localhost:5741/memory/retain` (console already mandates local bridge).
- **`Aria.Console/ConsoleHelper.cs`** — `"hindsight"` → `"memory"`, drop BaseURL/BankID mapping, 🧠 icon. **`appsettings.json`** — delete `Hindsight` section.

## Phase 7 — Version + status page + cleanup

- **`Aria.Bridge.csproj`**: `0.9.6-beta` → **`0.9.7-beta`** (minor bump — new endpoints/capabilities; `BridgeLogger.Version` reads informational version).
- **`Aria.Bridge/Frontend/BridgeStatusPage.cs`**: NOOSPHERE card (config health, engram count, pending queue) from `/memory/status` + `/memory/stats`.
- **Bridge `appsettings.json`**: default `Noosphere` section for the dev machine (LM Studio URLs).
- Keep RRF merge, FTS match builder, blob codec, lenient JSON parse in static/testable methods (note: `Aria.Tests.csproj` doesn't reference Aria.Bridge — add ProjectReference only if tests wanted).

## Build order

1 schema → 2 services → 3 endpoints/builtins (**curl-testable on bridge alone here**) → 4 tool/harness/migration → 5 web panel → 6 console → 7 version/cleanup. Rebuild + restart both apps per CLAUDE.md after changes.

## Verification

**Bridge-only (curl `localhost:5741`):**
1. `/memory/status` — config detected. 2. `POST /memory/retain` with "Alex met Marcus at the Lyon forge on 2026-07-01; Marcus now leads the plasma project." 3. After ~5 s, `/memory/stats` shows engrams + entities (Alex, Marcus, Lyon…). 4. `POST /memory/recall {"query":"who leads the plasma project?"}` — results + legs. 5. `POST /memory/reflect {"query":"what do we know about Marcus?"}`. 6. List + DELETE an engram, stats drop.
**Degradation:** blank `Embeddings:Url`, restart → recall still works (`legs.vector=false`); stop the extraction model, retain → ingest lands as `raw` engram, FTS-recallable.
**Agent path (Web):** enable "Memory (Noosphere)" in TOOLS; "Remember that my cat is named Horus" → live Retain tool block; new cogitation "What is my cat's name?" → Recall returns it. Bridge log shows built-in tool dispatch.
**UI panel:** // NOOSPHERE flyout — counts, search, entity chips, delete, soul-locked when bridge down.
**Console:** startup shows memory available; auto-retain rows land in MemoryIngests.
**Migration:** a DB that had hindsight enabled shows memory enabled after deploy; bridge banner prints 0.9.7-beta.

## Risks

- **Node-locality**: multi-node souls get per-node memory stores (tools write to LLM node; panel reads with node failover). Acceptable at personal scale; future work = DEK-encrypted sync like keys.
- **15 s LocalRest cap** on panel recall — mitigated by 8 s embed timeout; other legs are ms-scale. Reflect never rides LocalRest.
- **Extraction quality on small local models** — mitigated by strict schema, response_format attempt, lenient parse, raw-engram fallback.
- **Embedding model change** — engrams store `EmbeddingModel`; worker re-embeds mismatched rows.
- **EF model ↔ raw DDL drift** — must stay byte-compatible (existing repo discipline).
