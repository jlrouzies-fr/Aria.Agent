# // IDEA — Servitor edge node (deterministic tool step on a Hive edge)

**Status: planned.** A third `EdgeNodeType` that runs a *non-LLM* tool — a bridge builtin
(`bash_exec`, `read_file`, `glob`…) or an MCP tool — as a step on the Overmind↔drone edge.
Drone writes a patch → Servitor runs `dotnet build` → the result gates or annotates what
flows onward. Reliable in a way LLM-judged conditions never are.

## Current state

- `MemberEdgeNode` (`Aria.Web/Data/Collectives/MemberEdgeNode.cs`) supports `Transform` (template
  rewrite of the instruction) and `Condition` (skip the drone unless contains/any/all/regex/llm passes).
- All edge nodes act on the **dispatch path only**: `RunDroneTaskAsync`
  (`CollectiveOrchestrator.Phases.cs:336-379`) evaluates conditions, then `ApplyTransforms` on the
  instruction. Nothing processes the drone's **result** on the way back.
- The orchestrator already calls bridge-side tools indirectly: the bridge exposes `/tools/call`
  (`Aria.Bridge/Endpoints/ToolEndpoints.cs`), which dispatches both MCP sessions and the builtins
  (`BuiltinTools.cs:36-55`: `bash_exec`, `read_file`, `write_file`, `edit_file`, `glob`…), reachable
  from the server via `ModelBridgeRegistry.SendLocalRestAsync`.

## Design

### Data model

```csharp
public enum EdgeNodeType { Transform = 0, Condition = 1, Servitor = 2 }

public enum EdgePhase { Dispatch = 0, Return = 1 }   // new column on MemberEdgeNode, default Dispatch
```

`Phase` makes the return path explicit instead of overloading `Position`. Existing rows migrate
to `Dispatch` (matches current behaviour). The canvas renders Return nodes on the drone side of
the gate marker (gate renders at position 500 today).

Servitor `Config` JSON:

```json
{
  "tool":   "bash_exec",
  "args":   { "command": "dotnet build -clp:ErrorsOnly", "cwd": "/path/to/project" },
  "inject": "{{output}}",
  "onFail": "annotate"
}
```

- `{{output}}` — drone result (Return phase); `{{instruction}}` — dispatched instruction (Dispatch phase).
  Substituted into any string arg value before the call.
- `onFail` (tool errored or `bash_exec` non-zero exit):
  - `"annotate"` (default) — append the tool output to what flows onward, clearly labelled
    (`◈ SERVITOR [dotnet build] FAILED:\n…`). The Overmind sees the failure and can re-plan.
  - `"fail"` — mark the task `Failed` with the tool output as the reason.
  - `"skip"` — Dispatch phase only: behave like a failed Condition (task `Skipped`).
- On success the tool output is appended the same way (`◈ SERVITOR [x] PASSED`), truncated to a
  configurable cap (default 2,000 chars) so the Overmind gets evidence, not a log dump.

### New Condition mode: `servitor`

`ParseCondition` gains `"mode":"servitor"` — passes iff the **most recent Servitor node on the same
edge and phase** succeeded. Lets users separate "run the check" from "what to do about it" using
the existing Condition machinery (including `negate`).

### Orchestration changes

1. `RunDroneTaskAsync` — after conditions/transforms, run Dispatch-phase Servitors in `Position`
   order (they can rewrite/annotate the instruction); after `RunHeadlessAsync` returns and **before**
   `MarkTaskCompletedAsync` and the post-response gate, run Return-phase Servitors on the result.
2. New helper `RunServitorAsync(collective, member, node, payload, ct)`:
   - resolve the owning node: builtins and MCP calls route through
     `SendLocalRestAsync(userId, "POST", "/tools/call", …, nodeId: collective.OriginNodeId)`;
   - substitute placeholders, invoke, classify success (tool `ok` flag + `exitCode == 0` for
     `bash_exec`), apply `onFail`;
   - `AppendEventAsync(…, CollectiveEventType.Info, "Servitor [dotnet build] PASSED/FAILED …")` so
     the timeline shows every mechanical step; `FireChanged`.
3. Timeout per Servitor call (default 120 s) so a hung command can't stall the round; a timeout
   counts as failure.

### Canvas / UI

- New node glyph on the edge (cog/skull icon, distinct colour) in `HiveCanvas.razor`; the node
  editor (in `Hive.Canvas.razor.cs`) gets a Servitor form: tool picker (builtins + connected MCP
  tools via `/tools/list`), args editor (key/value with placeholder hint), phase toggle, onFail select.
- Node tooltip shows last run: PASSED/FAILED + first line of output (persist last result on the
  edge node row or in memory keyed by collective run).

## Implementation steps

1. Enum + `Phase` column + SQLite migration in `BridgeDatabaseInitializer` counterpart
   (`Aria.Web` DB: `DatabaseInitializer.cs` incremental migration).
2. `RunServitorAsync` + wiring in `RunDroneTaskAsync` (both phases) + timeline events.
3. `ParseCondition` `servitor` mode.
4. Canvas glyph + editor form + tooltip.
5. CRUD plumbing in `CollectiveService` (node create/update already generic over `NodeType`/`Config`).

## Open questions

- Should Servitor output feed `{{servitor}}` as a placeholder for later Transform nodes on the same
  edge? (Cheap to add; deferred until a concrete need.)
- Multi-node collectives: for now always route to `collective.OriginNodeId`; per-node routing by
  `cwd` path (like terminal tools do) is a follow-up.
- Governance: Hive drones already run headless; Servitor `bash_exec` should still respect the
  bridge `SecurityPolicy` blocklist (it does — dispatch goes through the same builtin path).
