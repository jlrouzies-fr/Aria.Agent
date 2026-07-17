# Noosphere — archive-aware extraction (known entities + Terminal Project anchors) & entity merge

## Context

Extraction is context-free today: `NoosphereExtractor.ExtractAsync` receives only the raw text + current datetime, and entity merging is an exact canonical-name match (`NoosphereService.cs` ~106). Result: naming drift — the archive currently holds four separate Spectra entities ("Spectra", "Spectra project", "Spectra Web UI", "Spectra v0.3"), and the v0.3 sub-topic is disconnected from the rest.

Three-part fix:
1. **Known-entities injection** — feed existing entity names into the extraction prompt so the model reuses exact names instead of inventing variants. Converts open-ended naming into a constrained matching task, which small local models do well.
2. **Terminal Project anchors** — if the retained text concerns a configured Terminal project (e.g. "Spectra"), lead the model to include that project as an entity in the relevant facts. Co-mention weight then pulls those facts into the project's community — grouping by project falls out of the existing modularity clustering with zero layout changes.
3. **Entity merge** — a manual "merge into…" action to clean up duplicates already in the archive (prompt injection only prevents future drift).

Key constraint: extraction runs on the **bridge**, but Terminal projects live in the **Web DB** (`UserToolConfigs` → terminal tool → `TerminalProject(Name, Path, Description, NodeId, Platform)` parsed by `ProjectFilesClient.ParseProjects`). Anchors must therefore be synced Web → bridge.

## Changes

### 1. Bridge — known-entities injection (no new state)

`Aria.Bridge/Services/Noosphere/NoosphereExtractor.cs`:
- `ExtractAsync(content, ct)` → `ExtractAsync(content, knownEntities, anchors, ct)`; both lists may be empty.
- System prompt gains, directly above the schema line (instruction adjacent to the list — small models lose instructions placed far away):

```
KNOWN ENTITIES already in the archive (reuse the EXACT name string when a mention
refers to the same thing; only create a new entity for genuinely new things):
- Spectra project (thing)
- Marcus (person)
…
ACTIVE PROJECTS (if the input concerns one of these projects, include the project
itself as an entity of kind "project" in the relevant facts):
- Spectra — plasma spectroscopy dashboard
…
```

`Aria.Bridge/Services/Noosphere/NoosphereService.cs` (`ProcessIngestAsync`, before the extract call):
- Load known entities for the soul/bank. Cap for prompt budget: if ≤ 150 entities, send all (name + kind); above that, send entities whose name shares a token (≥3 chars, case-insensitive) with the ingest content, plus the top 50 by engram count.
- Load anchors (see §2) and pass both to the extractor.

### 2. Terminal Project anchors (Web → bridge sync)

**Bridge storage** — new table `MemoryAnchors` (Id, SoulId, Bank, Name, Description, Source `"terminal-project"`, UpdatedAt):
- `BridgeDbContext` entity + DbSet, raw DDL in `BridgeDatabaseInitializer` (same idempotent discipline as the other Noosphere tables — keep EF model and DDL byte-compatible).
- `PUT /memory/anchors` in `MemoryEndpoints.cs`: body `{anchors:[{name, description}]}` — replace-all semantics for the active soul + `Source='terminal-project'` (delete missing, upsert by name). `GET /memory/anchors` for the status page/debug.

**Web sync** — `BridgeMemoryClient.SyncAnchorsAsync(userId, List<(string Name, string Description)>)` calling the PUT via `SendLocalRestAsync`:
- Called from `NavMenu.razor.cs LoadUserDataAsync` and `NavMenu.Bridge.cs OnToolsChangedNav` (both already load tool config + auto-memory settings there), sourcing `ProjectFilesClient.ParseProjects(...)` on the terminal tool's ConfigJson. Fire-and-forget, ignore failure (bridge may be down).
- Also called after saving the Terminal tool config in the tools panel (config modal save path in `NavMenu.Tools.razor.cs`) so renames propagate immediately.

**New `project` entity kind**:
- Extractor prompt schema enum: `person|place|org|concept|thing|event|project|other`.
- `Aria.Web/Services/Memory/MemoryGraphLayout.cs` — `KindColor`: `"project" => "#e0a050"` (orange); `KindGlyph`: `"project" => "◉"`.
- Nothing else — kinds are free-form strings end to end.

**Why this groups correctly with no layout work**: the project entity gets co-mentioned with every fact-entity in the retain (weight 1 each) and usually accumulates relations over time. Greedy-modularity clustering then makes the project a community hub; the hull label becomes the project name (hub = highest engram count).

### 3. Entity merge (duplicate cleanup)

**Bridge** — `NoosphereService.MergeEntityAsync(sourceId, targetId)`:
- Re-point `EngramEntities.EntityId` source→target, then dedupe (same EngramId+EntityId).
- Re-point `EntityLinks.FromEntityId`/`ToEntityId`, drop links that became self-loops, dedupe identical (From,To,Relation) triples.
- Delete the source `MemoryEntity`. Single transaction. Refuse `sourceId == targetId` and cross-soul/bank merges.
- Endpoint: `POST /memory/entities/merge` body `{sourceId, targetId}` in `MemoryEndpoints.cs`.

**Web** — in the `/memory` entity drawer (`Memory.razor` + `Memory.razor.cs`):
- A "⇄ MERGE INTO…" row: `ThemedSelect` over the other entities (from `_graph.Nodes`, sorted by name — **bind Value with the `@` prefix**, string param) + confirm button.
- On confirm: `BridgeMemoryClient.MergeEntityAsync(userId, sourceId, targetId)` → `CloseEntityDrawer()` → `RefreshAsync()`.

### 4. Bridge UI — "Wipe Noosphere" on the Data tab

Follow the existing wipe pattern exactly (`DELETE /db/cogitations` → danger button → `wipeCogitations()` JS with `ariaConfirm`):

- **`Aria.Bridge/Endpoints/DbAdminEndpoints.cs`** — `DELETE /db/noosphere`: delete all rows from `EntityLinks`, `EngramEntities`, `Engrams`, `MemoryEntities`, `MemoryIngests`, `MemoryAnchors` (child tables first; the `AFTER DELETE` trigger clears `EngramsFts`). Also invalidate the embedder's in-memory vector cache. Return deleted counts.
- **`Aria.Bridge/Frontend/BridgeStatusPage.cs`** — in the `// Data Management` card, a new block between "Wipe all cogitations" and "Reset soul identity":
  - Label "Wipe Noosphere memory", hint "Removes all engrams, entities, relations, and pending ingests. Soul identity and cogitations are preserved.", button `▶ WIPE NOOSPHERE` → `wipeNoosphere()` JS (ariaConfirm with the same cannot-be-undone warning, then refresh `refreshNoosphere()` + `refreshData()`).
- **`/db/soul` (full reset)** — extend the existing wipe to also purge the Noosphere tables, so "start completely fresh / handing over the machine" actually removes memories too (they are personal data in the vault).

### 5. Version + verification

- Bump bridge `0.9.9-beta` → **`0.9.10-beta`** (minor — new endpoint + retain capability).
- Rebuild + restart both apps per CLAUDE.md.

**Verify (curl on `127.0.0.1:5741`):**
1. `PUT /memory/anchors` with `[{"name":"Spectra","description":"plasma spectroscopy dashboard"}]`; `GET` echoes it.
2. Retain "The heatmap renderer in v0.3 now supports log scale." → after ingest, the fact's entities include the exact known entity names (no new "Spectra v0.3"-style variant) and the `Spectra` project entity of kind `project`.
3. Retain something unrelated ("Bought oat milk") → no project entity attached (the "else the usual" path).
4. `POST /memory/entities/merge` on two of the existing Spectra duplicates → `GET /memory/graph` shows one node with the union of relations, no self-loops.
5. `DELETE /db/noosphere` → `/memory/stats` all zeros, `/memory/recall` returns empty, retain afterwards works from a clean slate; bridge UI Data tab shows the wipe button with confirm dialog and the Noosphere card refreshes to zero counts.
**Verify (UI, Playwright):** /memory graph shows project-kind node (orange ◉) as hub of its community with the project name on the hull; merge action in the drawer collapses a duplicate and the graph refreshes.
**Local-LLM sanity:** run the retain tests against the LM Studio extraction channel — confirm the model actually reuses exact names; if it drifts, tighten the instruction wording before touching code.

## Risks

- **Over-merging by small models** — a genuinely new entity mapped onto a known name. Bounded damage (engram still recallable via FTS/vector); mitigated by the "only if it refers to the same thing" wording. The merge tool gives no un-merge — consider logging merges in the bridge log.
- **Prompt budget on large archives** — capped known-entities list (token-overlap filter + top-N) keeps the block a few hundred tokens.
- **Anchor staleness** — anchors sync on nav load and tool-config save; a project created on another device appears after the next circuit load. Acceptable.
- **`Source` column** future-proofs anchors: later sources (e.g. Hive collective names, calendar project tags) can lead grouping the same way.
