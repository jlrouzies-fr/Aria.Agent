# // IDEAS — planned feature designs

Plans for accepted feature ideas, grounded in the current code. Each doc states current state
(with file references), design, implementation steps, and open questions.

## Hive — programmable edges

| Plan | One-liner |
|---|---|
| [Servitor edge node](hive-servitor-edge-node.md) | Deterministic tool step on an edge (`bash_exec`, MCP…) — build/test/validate drone output mechanically, plus a `servitor` condition mode |
| [Lexmechanic edge node](hive-lexmechanic-edge-node.md) | Return-path distillation: cheap-model digest of drone replies so Overmind context stops ballooning |

Suggested order: Servitor introduces the `EdgePhase` (Dispatch/Return) column that Lexmechanic
also needs — build Servitor first, or land the column with whichever goes first.
