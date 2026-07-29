# // IDEA — Context-window discovery (per-model budgets from the probe cache)

**Status: shipped.** The harness assumes a fixed 100k-token context for every model and estimates
usage at chars/4. Field experience shows the failure mode: a small-context local model plus silent
history truncation produced "odd answers" (see
`docs/troubleshooting/empty-reply-or-thinking-only.md`). We already probe and cache per-model
*formats* once; extend that machinery to learn each model's *context window* and derive real
budgets from it.

## Current state

- Format/vision probing runs once per source+model and persists verdicts via `IFormatCache`
  (SQLite in `Aria.Web`); probes early-exit on first verdict — `Aria.Harness/Core/Harness.cs`
  (~:1002-1004, ~:1099-1101). Verdicts are human-confirmable.
- `AutoCompaction` (`Aria.Harness/Context/AutoCompaction.cs` ~:11-13): threshold fixed at 100k;
  token estimate = reported `update.Usage` when present, else chars/4. Compaction replaces the
  transcript with a summary and spins a fresh session
  (`Aria.Web/Components/Pages/Chat.Messaging.razor.cs` ~:576-580, :762-830).
- `context_status` (`Aria.Harness/Tools/ContextStatusTools.cs` + `ContextStatusReport.cs`) exposes
  the same numbers to the model.
- `read_file` reads whole files with no size cap; `bash_exec` output is effectively unbounded.

## Design

### New cached fact: `ContextWindow`

Resolution order per source+model (first hit wins, stored in the format cache like other verdicts):

1. **User override** — new optional "context window" number on the bridge channel configuration
   (per channel/model; wins over everything, human-editable like format verdicts).
2. **Provider discovery** at channel test/probe time:
   - Ollama: `/api/show` → `num_ctx` / model info context length.
   - LM Studio / OpenAI-compatible: model metadata endpoint where offered.
   - Cloud providers: small built-in table for the well-known models (best-effort, versioned).
3. **Fallback** — keep today's 100k assumption, recorded as `assumed: true`.

### Uses

- **AutoCompaction threshold** = `window × 0.8` (clamped to a sane floor, e.g. 4k) instead of the
  fixed 100k. Assumed windows keep the current threshold — no behaviour change until we know better.
- **`context_status`** reports the window, % used, and whether it is known or assumed — the model
  can pace itself, and debugging "odd answers" starts with one tool call.
- **User-facing warning**: when estimated usage exceeds a *known* window, the UI shows a notice
  ("context exceeded — replies may degrade; compact or switch model"). Not injected into the model.
- **`read_file` guard** (only when the window is known): if `chars/4 > 25%` of the window, return
  the first ~200 lines plus guidance to read ranges, instead of the whole file. With an assumed
  window, keep current behaviour.
- Tool-output caps (grep/glob/process) stay fixed — separate concern; see
  [tool-output-distillation-plan.md](tool-output-distillation-plan.md) for large outputs.

### Notes

- No real tokenizer is added (chars/4 stays the estimate) — the win is knowing the *denominator*.
  A tokenizer per model family is a possible v2 but heavy.
- Cache schema: one more nullable column + `assumed` flag on the format-cache row; existing rows
  migrate to `assumed: true` (== today's behaviour).

## Implementation steps

1. Cache schema migration + `ContextWindow` on the format-cache record (`Aria.Web` DB).
2. Bridge channel-config override field (number, optional) + status-page input on the channel form.
3. Discovery: Ollama `/api/show` first (most common local path), then LM Studio metadata; wire into
   the existing probe flow so it runs once per source+model.
4. `AutoCompaction` threshold derivation + clamp; `context_status` report fields; UI warning.
5. `read_file` known-window guard with range-read guidance.
6. Tests in `Aria.Tests`: resolution order (override > discovery > fallback), threshold derivation,
   read_file guard only when known. Docs: architecture.md memory/context section.

## Open questions

- Expose the estimate's confidence in `context_status` (chars/4 vs usage-reported)? Probably yes —
  cheap, one field.
- Quantisation/KV-cache effects on usable context are ignored — acceptable for budgeting purposes.
- Should compaction threshold be a user-tunable ratio per channel? Default 0.8; add only if asked.
