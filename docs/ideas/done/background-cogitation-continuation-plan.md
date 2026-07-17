# Background cogitation continuation (CogitationRunRegistry)

> **Status: implemented.** See the README's *Cogitator Terminal* section for the user-facing summary (background cogitations, reattach, and sidebar indicators). Three bugs surfaced during rollout, all fixed in the same change set:
> 1. **Agent/router reuse crash** — the "new chat" fast path reused an agent whose tools were bound to a router still owned by the cogitation being left; if that cogitation's run was still active, sending in the new chat threw a `NullReferenceException` inside the same call stack as the keypress handler, crashing the whole Blazor circuit. Fixed by refusing the reuse path whenever the cogitation being left still has an active run, and correctly carrying the router reference through when reuse is safe.
> 2. **Stale-completion-callback race** — `OnRunCompleted`/`OnRunUpdated` read the component's *current* `_attachedRun` field instead of the specific run instance that raised the event; a late-arriving completion callback from a run you'd since left could tear down whichever run you'd since attached to. Fixed by having each subscription close over its own run instance and comparing `_attachedRun == thatRun` before acting.
> 3. **Section-list self-corruption** — on a fresh send, `_streamingMsg` (rendered) and `run.Reply` (written to by the background run) are the *same* `MessageEntry` object, so the "reattach mirror" sync logic's `Sections.Clear(); Sections.AddRange(run.Reply.Sections)` cleared and then copied from the very list it had just emptied — wiping out streamed content almost as fast as it arrived, while the underlying model kept generating unseen. Fixed by skipping the copy when the mirror and the run's `Reply` are reference-equal (no copy needed — they're already the same live object).

## Context

When a prompt is streaming in Chat and the user navigates away (Memory, Hive, a different cogitation, "new chat"), the response is killed: the whole run loop lives inside the `Chat` Blazor component, driven by `_cts` (`Chat.razor.cs:53`), which `Dispose()` cancels on navigation (`Chat.razor.cs:561`), and `CancelActiveStreaming()` is called explicitly on every cogitation/model/sub-agent switch. Worse, the assistant reply is only persisted in `SendAsync`'s `finally`, which then runs on a torn-down circuit — so the partial reply is often lost.

Goal (user-confirmed): runs survive in-app navigation **and** page refresh (singleton registry keyed by cogitationId, same pattern as `CollectiveOrchestrator`); **multiple concurrent runs** with a sidebar activity indicator; persistence stays **end-of-run only** (now reliable because it happens in the background run); reopening a cogitation with a live run **reattaches** to the stream; STOP still works.

Key verified constraint: `Harness.CreateSessionAsync` bakes the UI callbacks (`OnToolStart/OnToolComplete/OnTodoUpdate`, thinking, approval) into tool wrappers at agent-build time (`Aria.Harness/Core/Harness.cs:67,78`) — so the component's methods must never be captured directly; a retargetable router must be passed instead. `MessageEntry` is already service-side (`Aria.Web/Services/Chat/MessageEntry.cs`), so run state can reuse it. LLM traffic flows over the bridge's own SignalR connection, independent of the browser circuit — nothing inherently dies with the circuit.

## Reuse rationale (why a new registry, not CollectiveOrchestrator)

`CollectiveOrchestrator.RunCogitationAsync` and `AgentBackgroundExecutor.RunHeadlessAsync` both **build their own agent** (from drone config / in a fresh DI scope) — right for Hive and cron vigils, wrong here: the chat run must **adopt** the live agent/session built by the Chat component (tools, bridge routing, callback router). So this change reuses their *patterns* (singleton + CTS dictionary + fire-and-forget `Task.Run` + completion events + 30-min linked timeout), not their code.

Code that genuinely becomes shared and moves out of the component:
- `IsCancellation` (`Chat.Messaging.razor.cs:398`) and `FriendlyError` (`Chat.Session.razor.cs:208`) → a shared static helper (e.g. `Services/Cogitations/StreamingErrorHelper.cs`); the component uses the shared copies too.
- The auto-retain buffer (component `_autoMemoryBuffer` → per-user dictionary in the registry).

Because the runner is cogitation-generic (Hive produces cogitations too), it is named and located accordingly: `Services/Cogitations/CogitationRunRegistry` — not a `Services/Chat/ChatRunRegistry`.

## New files (all under `src/AriaAgent/Aria.Web/Services/Cogitations/`)

### 1. `CogitationStreamRouter.cs` — retargetable callback router
- `interface ICogitationStreamSink { void ThinkingToken(string); void ToolStart(string,string); void ToolComplete(string,string); void TodoUpdate(IReadOnlyList<TodoItem>); Task<bool> ApprovalRequestedAsync(ActionDescriptor, CancellationToken); }`
- `CogitationStreamRouter` holds `volatile ICogitationStreamSink? Target`; its forwarding members are what get passed into `AgentService.CreateSessionAsync`. Null target ⇒ drop silently.
- Component sets `Target = componentSink` after building the agent; `CogitationRunRegistry.StartRun` retargets it to the run (matches the existing `_thinkingTarget` late-token rationale, `Chat.razor.cs:44-49`).

### 2. `CogitationRun.cs` — per-run state, implements `ICogitationStreamSink`
- Identity: `CogitationId`, `UserId` (bridge uid), `OriginNodeId`, `SubAgentId`, source/model names (for reattach), `StartedUtc`.
- Handover: `AIAgent Agent`, `AgentSession Session`, `CogitationStreamRouter Router` — adopted by a reattaching component.
- Streaming state: `MessageEntry Reply` (shared type), `List<TodoItem> Manifest`, `string? StatusText`, `CogitationRunStatus Status` (`Streaming → Persisting → Completed/Faulted`), `WasInterrupted`.
- Control: `CancellationTokenSource Cts`, `object Sync` lock guarding `Reply.Sections`/manifest mutation.
- Approval: `ActionDescriptor? PendingApproval`, `TaskCompletionSource<bool>? ApprovalTcs`, `ResolveApproval(bool)`.
- Events: `Updated`, `Completed`, `ApprovalChanged` (raised from background threads; subscribers marshal via `InvokeAsync`).
- Sink methods mutate `Reply.Sections` under `Sync` exactly as `Chat.Rendering.razor.cs:196-264` does today, then raise `Updated`.

**Thread-safety contract:** the run is the single writer under `Sync`. The attached component never renders the run's `Reply` directly — it keeps a mirror `MessageEntry` in `_messages` and on each event lock-copies section refs (`mirror.Sections.Clear(); AddRange(run.Reply.Sections)`) + manifest + status. Section `Text`/`Result` writes are atomic reference swaps; only list enumeration during `Add` needs the lock. O(sections) per event.

### 3. `CogitationRunRegistry.cs` — singleton
- `ConcurrentDictionary<int, CogitationRun> _runs` keyed by cogitationId. Injects `AgentService`, `BridgeCogitationClient`, `BridgeMemoryClient`, `IServiceScopeFactory`, logger.
- API: `StartRun(CogitationRunRequest)` (rejects if cogId already active; sets `router.Target = run`; `Task.Run` the loop; raises `RunsChanged`), `TryGet(int)`, `IsActive(int)`, `Cancel(int)`, `event Action<string userId, int cogId> RunsChanged` (start/complete/remove — NavMenu filters by userId).
- `CogitationRunRequest` carries: cogId, userId/bridgeUserId, originNodeId, agent, session, router, the (history-injected) aiMessage, userText, pre-built reply `MessageEntry`, turnScopePaths, `GovernanceMode`, auto-memory mode/interval, memory-tool flag, source/model names.
- **Run loop** (moved from `SendAsync` try/catch/finally, `Chat.Messaging.razor.cs:314-393`):
  1. 30-min timeout CTS linked with `run.Cts` (pattern: `AgentBackgroundExecutor.cs:105-106`).
  2. `await foreach (token in AgentService.StreamAsync(aiMessage, agent, session, linked.Token, turnScopePaths, governanceMode))` — append under lock (same last-section logic incl. `CollapseThinking` on first content), raise `Updated`.
  3. Same catch arms: cancellation (move `IsCancellation` helper from `Chat.Messaging.razor.cs:398-409` to a shared static) appends `// TRANSMISSION INTERRUPTED //`; fault appends `// COGITATOR FAULT … //`.
  4. `finally` (wrapped in try/catch+log so a persist fault can't leak a stuck run): `CollapseThinking`; `Status = Persisting`; persist assistant reply — bridge path via singleton `BridgeCogitation.AddMessageAsync` + scoped `CogitationService.TouchAsync` (via `CreateAsyncScope`), legacy path via scoped `CogitationService.AddMessageAsync`; auto-retain; `Status = Completed`; raise `Completed`; remove from `_runs`; raise `RunsChanged`.
- **Auto-retain moves here**: per-user `ConcurrentDictionary<string,(List<string>,int)>` replaces the component's `_autoMemoryBuffer` (`Chat.Messaging.razor.cs:15-48`); uses singleton `BridgeMemoryClient`.
- **Approval**: run's `ApprovalRequestedAsync` parks descriptor+TCS in run state, raises `ApprovalChanged`, awaits with the existing 2h timeout; `NeedsSeal` calls singleton `SealService` directly.

DI: `services.AddSingleton<CogitationRunRegistry>()` in `DependencyInjection/ServiceCollectionExtensions.cs` (~line 54).

## Component changes (`Chat` partials)

### `Chat.razor.cs`
- Inject `CogitationRunRegistry`. New fields: `CogitationStreamRouter? _router`, `CogitationRun? _attachedRun`. Remove `_cts` entirely.
- `StopStreaming()` (:247) → `Registry.Cancel(_cogitationId ?? -1)` + keep greeting cancel.
- Split `CancelActiveStreaming()` (:235):
  - `DetachFromRun()` — unsubscribe run events, `_attachedRun = null`, `_isStreaming = false`, cancel `_greetingCts` only. **Never cancels the run.** Used by navigation/switch/new-chat (`OnNewChatRequestedAsync:487`, `OnCogitationSelected` Session:247) and `Dispose()` (:561-562 replaced).
  - Config changes (`OnSourceChanged:437`, `OnActiveSubAgentUpdated` Session:383, `OnToolSettingsChanged` Session:445) → `DetachFromRun()` **+** `Registry.Cancel(_cogitationId)` — cancels only the viewed cogitation's run (session is rebuilt underneath it; a background run for another cogitation owns its own snapshot and is untouched).
- `ResetChatState()` (:253): also detach + `_router = null`.

### `Chat.Messaging.razor.cs` — `SendAsync` (165-394)
- Keeps: input/attachments, user `MessageEntry`, cogitation creation, user-message persistence + titling (190-267), reply entry construction, `BuildHistoryContext` injection (302-307), turn-scope assembly.
- New busy guard: if `Registry.IsActive(_cogitationId)` → show `// COGITATION BUSY //` hint, return (one run per cogitation).
- Replace CTS + stream loop + catch + finally (284, 314-393) with `Registry.StartRun(...)` + `AttachToRun(run)`.
- `AttachToRun(CogitationRun run)`: subscribe `Updated` (InvokeAsync: lock-copy into `_streamingMsg` mirror, manifest, status, `_smartScrollPending`, `StateHasChanged`), `Completed` (final copy; `_isStreaming = false`; interrupted marker semantics from `run.WasInterrupted`; queued-input auto-send stays component-side — queued text typed then navigated away is intentionally dropped), `ApprovalChanged` (mirror into `_pendingApproval`).
- Delete `MaybeAutoRetainAsync` + buffer. `HandleKeyDown`/`_queuedInput` (447-471) untouched.

### `Chat.Session.razor.cs`
- All four `CreateSessionAsync` call sites (140, 345, 416, 471): create `_router`, set `Target` to the component sink (the existing `OnThinkingToken/OnToolStart/OnToolComplete/OnTodoUpdate/RequestToolApprovalAsync` bodies), pass the router's members instead of component methods. Greeting/idle behavior unchanged.
- `OnCogitationSelected` (244-377) — reattach after history load (:305), before channel-probe/rebuild (:319):
  - `Registry.TryGet(cogId)` with matching userId → adopt `run.Agent/Session/Router`, set source/model from run, `_historyInjected = true`, create mirror entry in `_messages`, `_isStreaming = true`, `AttachToRun(run)`, initial lock-copy, return (skip rebuild).
  - **Dedup during the `Persisting` window**: `Streaming` ⇒ reply not yet persisted, always append mirror; `Persisting` ⇒ append only if last loaded assistant message content differs from `run.Reply.Content`; run gone ⇒ normal path.

### `Chat.Approval.razor.cs`
- `ApproveToolCall`/`DenyToolCall`: `_attachedRun?.ResolveApproval(...)` else existing TCS. Approval bar renders from `_pendingApproval`, now also fed by `OnRunApprovalChanged` — a gate that opened while away is visible and resolvable on return.

### Sidebar indicator
- `NavMenu.razor.cs`: inject registry, subscribe `RunsChanged` (filter by current user, `InvokeAsync(StateHasChanged)`), unsubscribe in Dispose; `HasActiveRun(cogId)` helper.
- `NavMenuCogitationsPanel.razor`: pulse glyph on rows with an active run (next to the offline badge, ~line 32) + small CSS pulse class.

## Edge cases
- Run completes while away → `RunsChanged` clears spinner; reopen loads persisted reply; dedup rule covers the `Persisting` window.
- Refresh mid-stream → URL restore (`Chat.razor.cs:313-317`) → reattach path; no session rebuild, no duplicate history injection.
- Orphaned runs → 30-min linked timeout; finally always removes from `_runs`.
- Greeting stream stays component-bound (`_greetingCts`).

## Implementation order
1. `CogitationStreamRouter` + `ICogitationStreamSink` + `CogitationRun`.
2. `CogitationRunRegistry` (loop/persistence/auto-retain/Cancel/RunsChanged) + DI.
3. Router at the 4 `CreateSessionAsync` sites + component sink; build.
4. `SendAsync` delegation + `AttachToRun` + detach/cancel call sites + Dispose + StopStreaming + busy guard.
5. Reattach path in `OnCogitationSelected` + approval mirror.
6. NavMenu subscription + spinner + CSS.
7. Cleanup (`_cts`, `MaybeAutoRetainAsync`, dead code); bump Bridge version only if Bridge changes (none expected).

Then rebuild + restart both apps per CLAUDE.md.

## Verification (end-to-end, apps running on :5129/:5741)
1. Long prompt → open Memory mid-stream → return: full reply, persisted exactly once; spinner showed while away.
2. Return after ~2s: accumulated text appears instantly, live tokens/thinking/tool blocks continue; STOP visible.
3. F5 mid-stream: URL restores cogitation, stream reattaches; follow-up prompt has full context.
4. Two concurrent runs (cogitation A streaming, start prompt in new chat B): both sidebar rows pulse, each shows its own stream, both persist.
5. STOP while attached and after navigate-away-and-back: `// TRANSMISSION INTERRUPTED //`, partial reply persisted, spinner clears.
6. Busy guard: second send into a streaming cogitation → busy hint; Enter mid-stream still queues via existing `_queuedInput` UX.
7. Approval gate: trigger gated tool, navigate away, return → approval bar present; Approve resumes run.
8. Config change while A streams in background and B is open → A unaffected.
9. Regressions: greeting stream, `/clear`, legacy (server-stored) cogitation path.

## Follow-up (out of scope for this change)

Migrate `CollectiveOrchestrator.RunCogitationAsync` (and its `HiveCogitationUpdated` / `onMessageAdded` plumbing) onto `CogitationRunRegistry`, so Hive background cogitations get the same sidebar indicators, reattach behavior, and orphan timeout for free — one unified run path. Deferred because it drags the Hive approval-gate flow and overmind loop into the regression surface.
