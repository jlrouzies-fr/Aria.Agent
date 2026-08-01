# // IDEA — Tool-output distillation ("Lexmechanic for the main loop")

**Status: planned.** The planned [Lexmechanic edge node](hive-lexmechanic-edge-node.md) distills
*drone replies* for the Overmind. The same trick is worth at least as much in the single-agent
loop: when a tool returns a huge output (build log, test log, fat grep), a small **local** model on
the bridge compresses it into a structured digest before it enters the main model's context. This
plays to Aria's unique asset — free local LLMs already sitting on the node — and no hosted
competitor can do it without shipping your data somewhere.

## Current state

- Tool results flow bridge → harness → model verbatim, bounded only by per-tool caps (grep 200
  matches, process_output 256 KB tail; `bash_exec` effectively unbounded; `read_file` unbounded).
  Unwrap point: `Aria.Harness/Bridge/BridgeMcpTool.cs` (:50-128).
- The bridge already runs LLM channels and already dedicates small models to chores: the Memory
  tab configures extraction/embedding channels for Noosphere.
- Lexmechanic (planned, Hive-only) introduces return-path distillation with a cheap model —
  `docs/ideas/hive-lexmechanic-edge-node.md`.
- Diffs appended to mutation results (edit-diff-feedback-plan.md) are small by design and should
  NOT be distilled.

## Design

### Bridge-side distillation service

- New bridge setting (Memory tab, next to extraction/embedding): **Distillation channel** +
  threshold (default 8,000 chars) + per-run budget (default: distill at most 3 outputs per turn).
  Off until a channel is chosen.
- Eligible tools v1: `bash_exec`, `run_tests`, `grep`, `read_file`, `http_request`,
  `process_output`. Not eligible: file mutations (diff feedback), memory tools, anything already ≤
  threshold, image results.
- When a result exceeds the threshold, the bridge calls the distillation channel with a fixed
  extraction prompt:

  > Distill this tool output for a coding agent. Preserve exactly: commands run, exit codes,
  > error messages, failing tests with file:line, file paths, identifiers. Drop repetition and
  > noise. ≤ 400 words. Output:

- The model receives:

  ```
  ◈ DISTILLED from 38,412 chars via <model> —
  <digest>
  (full output held on node as <id>; ask for tool_output <id> if you need the raw log)
  ```

- **Raw retention:** the original output is kept in a small in-memory ring (last ~20, keyed by id,
  plus 256 KB cap each) and a tiny builtin `tool_output {id, offset?, length?}` fetches slices of
  it — so distillation never destroys information the agent turns out to need.
- **Fail-open:** any distillation error or timeout (30 s) → return the original, hard-truncated at
  2× threshold with a truncation marker. Distillation must never break a tool call.
- **Loop safety:** the distillation call is a plain one-shot completion — no tools, no governance,
  no memory writes, never counted in budgets.
- **Privacy default:** the picker defaults to local channels; choosing a *cloud* channel shows a
  warning (tool output may contain code/secrets) — allowed, but a conscious act.

### Why bridge-side, not harness-side

The bridge is where the raw bytes already are, where the local model lives, and where the result
can be replaced before anything crosses the tunnel — the server never sees the 38 KB log at all.
Lexmechanic should later reuse this same service for drone replies.

## Implementation steps

1. `DistillationService` on the bridge: config, one-shot call helper, extraction prompt, ring
   buffer for raw outputs.
2. Hook into the tool-result path (after the builtin/MCP handler, before the response crosses the
   tunnel): eligibility check → threshold → distill or fail-open.
3. `tool_output` fetch builtin (ring-buffer slices) + manifest/dispatch + governance classification
   (read-class).
4. Memory-tab UI: channel picker + threshold + per-turn budget; cloud-channel warning.
5. Tests in `Aria.Tests` with a fake distillation channel: below-threshold passthrough, digest
   format, fail-open on timeout, `tool_output` slice fetch, mutation results never distilled.
6. Docs: README tools bullet + memory/setup docs; cross-link from hive-lexmechanic-edge-node.md.

## Open questions

- Cache digests by content hash (same failing build distilled twice)? Cheap win; add if traces
  show repeats.
- Distill *accumulated* turn context (not just single outputs) at compaction time? That's a
  different feature (smarter AutoCompaction) — note as synergy, don't build here.
- Per-tool thresholds (grep vs bash have very different signal density)? v1 uses one threshold;
  tune with data.
