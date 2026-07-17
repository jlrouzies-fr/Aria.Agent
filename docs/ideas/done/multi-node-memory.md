# Multi-node Noosphere memory

## Context

Noosphere memory is **node-local**: one SQLite vault per bridge, scoped by `(SoulId, Bank)`, never
replicated between a soul's nodes. With more than one node connected (e.g. laptop + desktop), the stores
diverge and reads were non-deterministic ("first node that answers").

We deliberately chose **federated read + per-node browsing** over replication (see the security analysis
below), because replication would push the user's most sensitive content (extracted life/project facts)
through the server in cleartext and persist new copies on every node.

## Shipped (bridge 1.11.0-beta)

- **Per-node browsing UI** — the `/memory` page has a node switcher (shown when >1 node). Each panel
  (stats, graph, entities, engrams, probe, delete, merge) is scoped to the selected node via a `nodeId`
  threaded through `BridgeMemoryClient` → `SendLocalRestAsync`. No cross-node merge/overlap visuals — the
  fuzzy canonical-name reconciliation that "shared memory" visuals need was judged not worth the cost at
  personal scale.
- **Recall scope setting** — `RecallScope { ThisNode | AllNodes }`, per-soul, persisted via
  `UserToolService` (`__recallscope__`), default **ThisNode**. Surfaced in the memory tool modal.
- **Fan-out recall** — when `AllNodes`, `Harness` registers `FanOutMemoryTool` for Probe/Contemplate
  instead of the single-node bridge tool. Probe queries every connected node's `/memory/probe` in
  parallel, pools results by score, dedups identical texts, fills a token budget. Contemplate reuses the
  fan-out for the probe stage then synthesises once on the LLM node via the new `/memory/synthesize`
  endpoint. Node enumeration added to `IHarnessRuntime.GetBridgeNodeIdsAsync` (Web = registry nodes;
  Console/Fake = empty).

## Follow-up: bridge-side agent loop (server-blind recall)

**The E2E gap is not closed by fan-out recall — it predates it.** Today the agent loop, prompt assembly,
and tool-result marshalling all run in Aria.Web (server-side). So recalled memory text lands in the
server's RAM (`WebHarnessRuntime.BridgePostAsync` → `StringBuilder`) and is baked into the prompt on the
server, **even with a single node**. The bridge protects keys and originates the final LLM HTTP call, but
not the content.

Fan-out recall does **not widen** this (same category of data, query-matched + transient, no new
persisted copies) — but neither does it fix it. The only true fix is running the agent loop **on the
bridge**, so memory and prompt assembly never reach the server. That is a large architectural change,
orthogonal to node count (needed even with one node). Tracked here as its own item, not a rider on the
multi-node work.

Why replication (the rejected alternative) is worse than either: it copies the **whole corpus,
proactively, and persists it on other nodes' disks** — vs recall's query-matched, on-demand, transient
slice. If replication is ever revisited it must be E2E-encrypted node→node, Seal-gated (not merely
soul-verified), and tombstone-aware (deletes must propagate, or "Wipe Noosphere" silently resurrects).
