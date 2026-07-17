# Memory graph — clustered "topic" layout (replace single radial circle)

## Context

The `/memory` Noosphere graph currently lays out **all** entities on one big circle (`MemoryGraphLayout.ComputeRadialPositions`). With ~26+ entities the circle is convoluted: edges criss-cross the middle, related entities sit far apart, and nothing communicates topics. The user wants the graph to **organize in space, grouped by topic/entity**, so related memories visually cluster together.

Design: deterministic cluster layout (no physics sim — keeps the existing "no persisted coordinates" philosophy). Topics = connected components computed over **explicit relations + co-mention** (entities appearing in the same engram belong to the same topic even without a named relation). Each cluster is drawn as a hub-and-rings arrangement inside a translucent labeled hull; clusters are packed on a spiral around the canvas center.

## Changes

### 1. Bridge — emit topic group per node
`Aria.Bridge/Services/Noosphere/NoosphereService.cs` — `GetGraphAsync` (line ~486):
- Also load co-mention pairs: `EngramEntities` for the soul/bank grouped by `EngramId` (union first entity of each engram with the rest — linear, avoids N² on entity-heavy engrams).
- Union-find over (a) `EntityLinks` endpoints and (b) co-mention pairs.
- `GraphNode` record gains `int Group` — 0-based index, clusters ordered by size desc (largest topic = group 0).
- **Bump `BridgeLogger.Version` 0.9.7-beta → 0.9.8-beta** (minor — graph endpoint capability change).

### 2. Web DTO
`Aria.Web/Services/Memory/BridgeMemoryClient.cs` — `MemoryGraphNodeDto` gains `int Group`.

### 3. Layout — `Aria.Web/Services/Memory/MemoryGraphLayout.cs`
New `ComputeClusteredLayout(nodes)` replacing `ComputeRadialPositions` (single caller), returning positions **plus** cluster metadata and world bounds:

- **Within a cluster**: hub = member with highest `EngramCount`, placed at cluster center; remaining members on concentric rings (~9 per ring, radii 130, 250, 370…). Cluster radius = outermost ring + 90 label margin; single-node cluster radius ≈ 70.
- **Singletons**: all size-1 clusters merged into one "UNLINKED" pseudo-cluster laid out as a grid, placed last (prevents debris scatter).
- **Cluster packing**: greedy circle packing on an Archimedean spiral from (0,0) — walk the spiral until the candidate circle doesn't overlap any placed cluster (+60 gap). Deterministic, largest first.
- **Bounds**: translate everything so min-x/min-y = 150 padding; return `(Width, Height, CenterX, CenterY)` for the SVG size and initial pan.
- Keep `ArcControlPoint`, `KindColor`, `KindGlyph` unchanged.
- Cluster metadata record: `MemoryCluster(double Cx, double Cy, double R, string Label, int Group)` — label = hub entity name (the "topic" name).

### 4. Rendering — `Aria.Web/Components/Pages/MemoryCanvas.razor`
- SVG `width`/`height` become dynamic from layout bounds (replace fixed 2800×2200).
- Before edges, render per cluster: `<circle>` hull (translucent fill, dashed dim stroke) + a topic label above the hull (**must use the `<g transform>`-wrapper pattern — Razor forbids attributes on literal `<text>` tags, RZ1023**).
- Node/edge rendering unchanged.

### 5. Page state — `Aria.Web/Components/Pages/Memory.razor.cs`
- `RefreshAsync` calls the new layout; store `_clusters` and world bounds (drop the `CenterX/CenterY` consts).
- Pass computed center to JS: `initMemoryCanvas(".mem-canvas-wrap", cx, cy)`.

### 6. JS — `Aria.Web/wwwroot/aria-interop.js`
`initMemoryCanvas(canvasEl, centerX, centerY)` — use params for the initial pan (default 1400/1100 if omitted).

### 7. CSS — `Aria.Web/wwwroot/css/memory/canvas.css`
`.mem-hull` (translucent fill, dashed dim stroke) and `.mem-hull-label` (small caps, `--text-dead`, letter-spacing) styles, matching the existing mem/hive visual language.

## Verification

1. Rebuild solution, restart both apps (per CLAUDE.md), curl both health endpoints (`0.9.8-beta`).
2. `curl -s http://127.0.0.1:5741/memory/graph | jq` — confirm `group` field present and grouping makes sense (Marcus/plasma/Lyon together; ports/projects together).
3. Playwright script in scratchpad: load `/memory`, screenshot — confirm distinct spatial groupings with hull labels instead of one circle; nodes readable; edges stay within/between clusters.
4. Click a node → drawer still opens with engrams; pan/zoom still works; search unaffected.
