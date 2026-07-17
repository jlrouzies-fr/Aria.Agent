# // IDEA — Lexmechanic edge node (distill drone output on the return path)

**Status: planned.** A return-path edge node that compresses a drone's reply into a short,
structured digest with a cheap model before it reaches the Overmind. Attacks the real Hive cost
problem: Overmind context grows with every full drone reply, every round.

## Current state

- Drone results flow back whole: `RunDroneTaskAsync` saves the full reply via
  `MarkTaskCompletedAsync(task.Id, result)` (`CollectiveOrchestrator.Phases.cs:400`), and the
  review/plan phases feed those full results back into the Overmind prompt.
- Edge nodes only act on the dispatch path (see `hive-servitor-edge-node.md` — this plan assumes
  the `EdgePhase` column from that plan lands first, or introduces it if built first).
- LLM-judged conditions already show the pattern for a headless side-call:
  `EvaluateConditionsAsync` → `_executor.RunHeadlessAsync(userId, subAgentId, prompt, sourceName,
  modelId, seedHistory, ct)` (`CollectiveOrchestrator.Conditions.cs:33-40`).

## Design

### Data model

```csharp
public enum EdgeNodeType { Transform = 0, Condition = 1, Servitor = 2, Distill = 3 }
```

Always `Phase = Return`. Config JSON:

```json
{
  "instruction": "Extract: files changed, key decisions, open problems. Bullet points only.",
  "maxChars":    1200,
  "sourceName":  null,
  "modelId":     null
}
```

- `sourceName`/`modelId` null → use the Overmind's channel/model. The point of the node is to pick
  a *cheaper* one, so the editor should nudge toward a local/small model.
- `CollectiveTask` gains `DistilledResult` (nullable string). **The raw result is never discarded** —
  drawers and gates keep showing it; only the Overmind's context uses the digest.

### Orchestration changes

1. In `RunDroneTaskAsync`, after the drone completes (and after any Return-phase Servitors): if the
   member has a `Distill` node, call `RunHeadlessAsync` with:
   - system: "You are a lexmechanic. Compress the DRONE OUTPUT per the INSTRUCTION.
     Hard limit {maxChars} characters. Never add information that is not in the output."
   - prompt: instruction + task title + raw output.
   Store in `task.DistilledResult`, truncate-enforce `maxChars` locally as a backstop.
2. Everywhere the Overmind consumes results — `BuildDroneInstructionAsync` dependency injection of
   prior task outputs, the review-phase prompt builder, and `LastFeedback`/plan context in
   `CollectiveOrchestrator.Loop.cs` — prefer `DistilledResult ?? Result`.
3. Skip distillation when the raw result is already shorter than `maxChars` (no-op, no spend).
4. Distillation failure (model error/timeout) → fall back to raw result + an `Info` timeline event;
   never fail the task because the summarizer hiccuped.
5. Timeline event on success: `"Lexmechanic distilled [task] 4,180 → 940 chars"` — makes the token
   savings visible, which is the feature's selling point.

### UI

- Canvas glyph (scroll/quill icon) on the return side of the edge; editor form: instruction
  textarea, maxChars, channel/model picker (reuse the Overmind model picker component).
- `HiveDroneDrawer.razor`: when a distilled result exists, show a small `DISTILLED n→m` badge and a
  toggle between raw and digest views.
- Post-response human gate (`GateAfterResponse`) shows the **raw** result — the human should review
  the truth, not the compression.

## Implementation steps

1. Enum value + `DistilledResult` column + incremental migration.
2. Distill step in `RunDroneTaskAsync` + fallbacks + timeline events.
3. Swap Overmind-side consumers to `DistilledResult ?? Result` (audit `Phases.cs`, `Loop.cs`,
   `DbHelpers.cs` prompt builders — the swap must be exhaustive or savings silently leak away).
4. Canvas glyph + editor + drawer badge/toggle.

## Open questions

- Should the digest be structured (forced JSON: `summary`, `artifacts`, `blockers`) rather than
  free text? Structured makes downstream Transform templates (`{{output}}`) more useful, but small
  local models fumble strict JSON — start free-text with a bullet-point convention.
- A collective-level default Distill config (apply to all drones) would avoid per-edge setup for
  big hives — worth adding once the per-edge version proves out.
