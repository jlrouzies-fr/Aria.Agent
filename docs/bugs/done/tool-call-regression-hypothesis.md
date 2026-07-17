# Regression: "agent re-introduces itself / odd answers after tool calls" — investigation log

> Living handoff doc. Keep updated. Several issues found under one visible symptom. Read the
> **STATUS** table first, then the section for the open item (#4).

## STATUS
| # | Issue | State |
|---|-------|-------|
| 1 | Code-reply re-greet (ColorCode markdown circuit crash) | ✅ FIXED (`MarkdownHelper.ToHtml` guard) |
| 2 | Web-search oversized result | ✅ FIXED (`WebSearchTools.FormatSearchResults` caps) — was NOT the re-greet cause |
| 3 | **Tool re-greet** (model re-introduces itself after a tool call) | ✅ **ROOT-CAUSED + FIXED + user-confirmed** ("it seems better") |
| 4 | **Monologue answers** (terse, lowercase first word, no "I", "out of place") | ✅ **FIXED** — unresolved thinking no longer emitted as content; Harness re-prompts once for a final answer |
| 5 | Tool result double-JSON-encoded | minor, harmless, tidy later |
| 6 | SSE renders a no-`</think>` reply twice (duplicate) | ✅ **FIXED** — same change as #4; monologue is no longer re-emitted as content |
| 7 | WebSearch/DateTime ran server-side & didn't render as tool blocks | ✅ **DONE** — moved to bridge built-in tools (node-side execution, render as `⚙` blocks, same path for Aria.Console) |

> NOTE (context): the practical trigger for the visible "odd answers" (#4) turned out to be a very
> small context window set when loading the local model — once the chat grew, history truncation
> made the model lose the thread. Code hardening (#4/#6) still helps, but the environment was the
> main factor.

## Issue 7 — WebSearch + DateTime moved to bridge execution
`GetCurrentDateTime` and `SearchWeb` were in-process `AIFunctionFactory` tools, so (a) they didn't
fire the `OnToolStart/OnToolComplete` callbacks → no UI tool-activity block, and (b) web search ran
server-side with the Ollama key in `Aria.Web/appsettings.json` (against the node-holds-secrets model).
Now they are **bridge built-in tools**:
- `Aria.Bridge/BuiltinTools/BuiltinTools.Web.cs` — native `GetCurrentDateTime` + `SearchWeb`
  (bounded formatting), config via `BuiltinTools.ConfigureWebSearch(app.Configuration)` in `Program.cs`,
  reading `OllamaWebSearch` from `Aria.Bridge/appsettings.json`. Dispatcher + manifest updated in
  `Aria.Bridge/BuiltinTools.cs`.
- `Aria.Harness/Core/Harness.cs` — when a bridge is connected, both load as `BridgeMcpTool` (so they
  render as `⚙` blocks and run on the node); in-process fallback (`DateTimeTools`/`WebSearchTools`)
  remains when no bridge. Reuses the existing `__aria_builtin__` server path.
- Verified live: `/tools/call GetCurrentDateTime` → datetime; `/tools/call SearchWeb` → reaches Ollama
  with the node key (429 only from rate-limiting during testing).
- Benefit: works uniformly for Aria.Web and (future) Aria.Console; secret stays on the node.

---

## Issue 3 — Tool re-greet — FIXED (root cause proven)
**Root cause:** `SendGreetingAsync` streamed the greeting through the **live agent session**, so the
prompt `"Soul entering the chat is named X. Present yourself briefly…"` + the greeting reply stayed
in the model-visible history. After a later tool call, that history **primed the model to greet
again** instead of answering.

**Proof (deterministic, no browser):** captured the exact bridged continuation request via
`[REQ-CAPTURE]` in `Aria.Harness/Models/BridgeHttpHandler.cs`, replayed verbatim against the live
LM and bisected (`scratchpad/req_bisect.py`):
- with the 2 greeting messages in history → **GREET 4/5**
- remove them → **ANSWER 5/5**
- decoding the double-encoded tool result / single tool / compact args → still greet (not causes)

**Fix applied** (`Aria.Web/Components/Pages/Chat.Messaging.razor.cs`, `SendGreetingAsync`):
greet through a **throwaway session** (`var capturedSession = await capturedAgent.CreateSessionAsync();`)
instead of `_session`. Greeting still shows in the UI (`_messages`); it never enters model history.

---

## Issue 4 — Monologue answers — FIXED
After the re-greet fix the agent **answers** (good), but answers read as the model's **internal
monologue**: terse, lowercase first word, missing leading "I", e.g.
`"searched for the EUR/USD exchange rate. The current rates are…"`,
`"found the current EUR/USD rate is 1.1690…"`.

### What was happening
- The model is `qwen3.6-35b-a3b-claude-4.7-opus-distilled-mlx-oq4`, thinking format
  **`StartsInThinkMode`**. Normally it thinks, emits `</think>`, then a polished answer.
- On many tool-continuation turns it instead emitted **only internal monologue** and
  **stopped with `finish_reason:stop` and no `</think>`**. Every token was therefore treated as
  thinking; at `[DONE]` the implicit think→content conversion (`UniversalSSEStream.cs:114-147`)
  dumped that monologue out as the reply.
- The monologue was then recorded as the assistant message, priming later turns to reply in the
  same monologue style.

### Fix applied
- **`UniversalSSEStream`**: when `StartsInThinkMode` reaches `[DONE]` still inside a think block
  with `finish_reason=stop`, the buffered text is now recognised as unresolved internal monologue,
  **discarded rather than emitted as content**, and `EndedWithUnresolvedThinking` is set. The
  thinking has already been streamed live to the UI thinking panel, so it is not lost from the
  user's view, but it no longer pollutes the assistant's content/history.
- **`UniversalReasoningHandler`**: surfaces the stream's `EndedWithUnresolvedThinking` flag via
  `LastStreamHadUnresolvedThinking` so the orchestration layer can act on it.
- **`Harness.StreamAsync`**: after the primary stream, if `LastStreamHadUnresolvedThinking` is true,
  it sends a single follow-up user message (`"Provide your final answer to the user now."`) and
  streams the model's proper reply. This bounds re-prompting to one extra turn.
- **Tests**: added `StartsInThinkMode_NoCloseTag_Stop_DiscardsMonologueAndSetsFlag` and
  `StartsInThinkMode_NoCloseTag_ToolCallsFinish_DiscardsWithoutFlag` in
  `Aria.Tests/Agent/UniversalSSEStreamTests.cs`.

> **Validation note:** the unit tests cover the SSE path deterministically. Real-world validation
> with the live `qwen3.6-35b` model is recommended before closing the loop with end users.

### Follow-up: model still occasionally replies instead of using tools
After the SSE fix, the live log showed the model sometimes emitting verbose internal reasoning
(e.g. "The user wants to search for Pikachu…") and then answering ""I'm ready — what do you need?""
instead of calling `SearchWeb`. This is a separate failure mode: the model is choosing a
salutation/non-answer over tool use. The SSE layer correctly routes that reasoning to the thinking
panel and only the short non-answer to content, so it is not a continuation of the monologue bug.

**Additional fix applied to `Aria.Agent/Helpers/Statics.cs`**:
- Strengthened the system message to explicitly forbid greetings/readiness statements
  ("I'm ready — what do you need?", "How can I help?", etc.).
- Added explicit instruction to call tools immediately without describing capabilities or asking
  permission.
- Added instruction to never describe reasoning about the user's intent out loud.

This is a prompt-level guard; model inconsistency may still cause occasional misses, but the
instructions now directly target the observed non-answer pattern.

---

## Diagnostics removed
All temporary diagnostics tagged for removal once Issue 4 was resolved have been cleaned up:
- `⟐ DIAG:` system messages, `[REINIT-TRACE]` Console lines, the `DiagLog` helper, and the
  `Environment.StackTrace` dump in `SendGreetingAsync`.
- `[REQ-CAPTURE]` writes in `Aria.Harness/Models/BridgeHttpHandler.cs` and the dead probe in
  `Aria.Web/Services/ModelBridgeHandler.cs`.
- `[WebSearch]` Console lines in `Aria.Tools/WebSearchTools.cs` were left in place (optional).

## Reproduction assets (kept for reference)
- Local LM: LM Studio `http://127.0.0.1:1234/v1`; model id `qwen3.6-35b` prefix-resolves to the
  full distilled id.
- `scratchpad/sim_search.py` — builds a continuation by hand and hits the LM.
- `scratchpad/req_bisect.py` — was used to prove Issue 3; the captured request log path is no
  longer written automatically.
- Raw SSE per turn: `Aria.Web/universal-sse-debug.log` (still written for debugging). The log now
  shows `[DONE implicit think→DISCARD — unresolved stop] Nch` for the fixed path.

## Tests added (keep)
- `Aria.Tests/Tools/WebSearchToolsTests.cs` — 3 passing (web-search output bounding).
- `Aria.Tests/Agent/UniversalSSEStreamTests.cs` — added:
  - `StartsInThinkMode_NoCloseTag_Stop_DiscardsMonologueAndSetsFlag`
  - `StartsInThinkMode_NoCloseTag_ToolCallsFinish_DiscardsWithoutFlag`
