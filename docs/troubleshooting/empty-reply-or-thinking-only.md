# Empty reply, or answer trapped in the thinking block

## Symptoms

- The agent "replies" but the message is empty (no content, maybe a thinking block).
- The **entire answer appears inside the thinking block**, content area empty.
- The chat shows nothing at all and no error, while the model server (LM Studio etc.) logs
  something like `Unexpected endpoint or method (POST /v1/chat/completions). Returning 200 anyway`.

These are three faces of two root-cause families. Work through them in order.

## Family A — a poisoned thinking-format verdict

**Mechanism.** If a channel's cached thinking format says `StartsInThinkMode` but the model
actually streams plain content, the SSE interceptor treats *everything* as think-text until a
`</think>` that never comes — the whole answer lands in the thinking block.

How verdicts got poisoned historically (all fixed, listed so the pattern is recognizable):

1. A **model-name heuristic** forced `StartsInThinkMode` for qwen3/gemma-4/… names whenever the
   probe returned `None` — including when the probe merely *failed* (endpoint down, LM Studio
   API-token auth enabled, key not yet on that node). Heuristic removed.
2. Format probes ran on the **default node** instead of the channel's bound node, interrogating a
   different machine's model server. Probes now follow the channel binding.
3. Probe **failures** returned `None` (cacheable) instead of `Unknown` (never cached). Fixed in
   `FormatProber`.

**Diagnosis / fix.**

```bash
# what's cached (fly)
fly ssh console -C "…"           # no sqlite3 in the image — use the endpoint instead:
curl -X DELETE "https://<host>/api/maintenance/format-cache?model=<fragment>"   # purge
```

or Channels panel → **// MAINTENANCE → CLEAR MODEL FORMAT CACHE**. Then start a **new
cogitation** (a session re-probes on creation). Verify the re-detected format in the session init
log line `// FORMAT: …` — a LM Studio-served reasoner should read `ReasoningContent`.

Note: models can think **conditionally** (Gemma 4 reasons only with system prompt + tools
attached). `None` from the bare probe is still correct — `reasoning_content` deltas are handled
dynamically whatever the verdict.

## Family B — the upstream endpoint failed and the failure was invisible

**Mechanism.** The bridge tunnel relays upstream responses as `200 text/event-stream` even for
failures (the transport can't change status mid-stream). An error JSON parsed as an empty SSE
stream → silent empty reply. Since the `UniversalReasoningHandler` head-peek fix, these surface in
the chat as:

```
// COGITATOR FAULT: The model endpoint rejected the request: <upstream message> //
```

Common upstream causes:

- **LM Studio API-token auth** (`invalid_api_key`): the executing bridge has no key in *its own*
  vault — keys are per-node; check distribution with
  `curl "https://<host>/api/maintenance/node-keys?userId=…"` and run
  `curl -X POST "https://<host>/api/maintenance/replicate-keys?userId=…"` if one vault is empty.
  LM Studio logs this as `Unexpected endpoint or method … Returning 200 anyway` — it's the auth
  interceptor, not a wrong URL.
- Model id not present on that machine (`model_not_found`) — model lists are per-node too.
- Server not running / wrong port.

**End-to-end check without touching the machine:**

```bash
curl -X POST "https://<host>/api/maintenance/test-channel?userId=<soulId>&source=<channel>"
curl "https://<host>/api/maintenance/node-llm-log?userId=<soulId>&nodeId=<node>"   # raw response heads
```

## Related

- [`docs/readme/reasoning.md`](../readme/reasoning.md) — format detection & probe semantics
- [`docs/readme/multi-node.md`](../readme/multi-node.md) — routing rules, key sync, diagnostics
- [`docs/Bugs/bug-thinking-block-render.md`](../Bugs/bug-thinking-block-render.md) — earlier rendering-side bug
