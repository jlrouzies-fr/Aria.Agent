# // IDEA — Mid-turn steering via MessageInjectingChatClient

**Status: implemented (UI + Harness wiring).** While a cogitation turn is streaming, the soul can redirect the agent at the
next tool-round boundary without stopping the turn or waiting for a full post-turn FIFO drain.
Uses the native Microsoft Agents AI `MessageInjectingChatClient` (package 1.6.2; Experimental MAAI001 suppressed at call sites).
Builds on the multi-message queue (Ctrl+Enter FIFO); steering is a separate mid-turn path.

## Current state

- Post-turn queue: `_queuedMessages` FIFO in Chat; Ctrl+Enter while streaming appends; drained one
  item per completed run (`DrainQueuedAsync`). Per-row ✕ and click/↑ recall.
  (`Chat.Messaging.razor.cs`, `Chat.razor`, `input.css`)
- Stream path: `Harness.StreamAsync` → `agent.RunStreamingAsync` — tool loop is inside the
  framework; Aria has no seam to inject user text between rounds today.
- Session build: `chatClient.AsAIAgent(name/instructions/tools)` in `Harness.cs` — does **not**
  set `EnableMessageInjection` (default false).
- STOP cancels the CTS and ends the turn; separate from steer.

## Design

### Locked UX

- **Ctrl+Enter** — queue (post-turn FIFO). Unchanged.
- **STEER** (queue header) / **Ctrl+Up** with empty composer — merge the **entire** queue into one
  mid-turn inject (joined with blank lines), then clear the FIFO.
- **Ctrl+Up** with composer text — steer that text alone (queue left intact).
- Submitted steers sit in a bottom **STEERING** strip (waiting for agent) — not mid-transcript —
  until `MessageInjectingChatClient` drains them; then they promote to normal user messages and the
  assistant bubble seals/rotates. ✕ on the strip cancels before drain when still pending.
- Per-row queue ✕ still discards one slot; plain ↑ on empty input still recalls one row for editing.
- Do **not** start a new `CogitationRun` or cancel the live stream.

### Architecture

```mermaid
sequenceDiagram
    participant UI as Chat_UI
    participant Run as CogitationRun
    participant Inj as MessageInjectingChatClient
    participant Loop as FunctionInvokingChatClient

    UI->>Run: Steer(text)
    Run->>Inj: EnqueueMessages(session, userMsg)
    Note over Loop: Current tool round finishes
    Loop->>Inj: Next model call
    Inj->>Loop: Drain pending into request
    Loop->>UI: Stream continues (same turn)
```

Not a model tool. Host enqueues; framework drains between function-loop iterations.

## Implementation

### 1. Enable injection at session build

In `Aria.Harness/Core/Harness.cs`, switch to the `AsAIAgent(ChatClientAgentOptions)` overload:

```csharp
var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    ChatOptions = new ChatOptions
    {
        Instructions = baseInstructions,
        Tools = tools,
    },
    EnableMessageInjection = true,
    RequirePerServiceCallChatHistoryPersistence = true,
});
```

Resolve `MessageInjectingChatClient` via `GetService` and hold it on the run (not only in Blazor).

### 2. Run-level steer API

On `CogitationRun` (wired from `CogitationRunRegistry`):

- Store injector (or resolve lazily from `Agent`).
- `bool TrySteer(string text)` → `EnqueueMessages(Session, [user message])` while `Streaming`;
  false if missing injector / not streaming / empty.
- v1 need not mirror `GetPendingMessages` in the UI.

### 3. Chat UX + keyboard

- `Chat.razor` queue rows: STEER button next to ✕.
- `Chat.Messaging.razor.cs`: `SteerQueued(index)`, `SteerComposerOrHead()`, Ctrl/Meta+↑ in
  `HandleKeyDown`.
- `input.css`: `.queue-steer` (gold) beside `.queue-cancel`.
- Terse streaming tip: Ctrl+Up steers / Ctrl+Enter queues.

### 4. Transcript + persistence

On successful steer:

1. Append `MessageEntry("user", text) { IsSoul = true }` to `_messages`.
2. Persist via existing `BridgeCogitation.AddMessageAsync` / `CogitationService.AddMessageAsync`
   (same origin-node rules as `SendAsync`) — no new run.
3. Leave streaming assistant bubble and CTS alone.

On failure: leave text in queue/composer; short status fault.

### 5. Tests

- Harness: after `CreateSessionAsync`, `GetService<MessageInjectingChatClient>()` is non-null.
- `TrySteer`: succeeds when streaming + injector present; no-op otherwise.

## Out of scope

- Remapping Ctrl+Enter away from queue.
- Hard interrupt + resend (STOP stays separate).
- Drag-reorder of the FIFO.
- Hive-specific UX (same injector if the run already uses `CogitationRunRegistry`).

## Open questions

None locked for v1. Optional later: show framework-pending injects before drain; promote STEER from
composer without queueing first (Ctrl+Up already covers composer text).
