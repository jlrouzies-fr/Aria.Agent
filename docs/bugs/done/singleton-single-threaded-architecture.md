# Singleton / single-threaded hotspots block concurrent work

## Status

**Open** — observed during Bridge Telemetry work; needs broader architectural review.

## Symptom

Long-running operations serialize with unrelated work because several key paths are either singleton-by-design or tied to a single dispatcher/thread:

- The Aria.Bridge SignalR client receive loop processes server-to-client invocations one at a time. A long `HandleRequest` (LLM streaming) blocked `HandleLocalRest` (metrics) until the handler was made fire-and-forget.
- The Blazor Server circuit dispatcher is single-threaded. Per-token `StateHasChanged()` during streaming can delay or drown unrelated UI updates such as the telemetry panel.
- Many core services are registered as `Singleton` and may become implicit bottlenecks when they run synchronous or long-running work on behalf of requests/components.

## Observed manifestations

1. Telemetry values stopped updating while the agent was streaming/cogitating.
2. Metrics requests via the direct tunnel were queued behind the active LLM stream.
3. UI renders only happened when token traffic produced them, leaving the panel frozen during silent cogitation.

## Current workarounds

- `DirectTunnel` handlers now return immediately and run their work fire-and-forget, freeing the SignalR client receive loop.
- `BridgeMetricsHostedService` collects metrics on a background loop; `/metrics` returns a cached snapshot.
- Telemetry panel uses a coalesced `StateHasChanged()` (single pending flag) so it refreshes during cogitation pauses without flooding the Blazor dispatcher.
- `AddSignalR` on the server raised `MaximumParallelInvocationsPerClient` so bridge control messages can interleave.

## Why this is likely a bigger architecture problem

- Any singleton service that performs CPU-bound, synchronous, or long-running I/O on the calling thread can stall unrelated callers.
- Fire-and-forget fixes the receive loop but pushes concurrency into the handler; if the handler itself relies on singleton resources, contention just moves.
- The Blazor dispatcher pattern repeats across components: high-frequency updates (streaming tokens, progress ticks, animations) compete with low-frequency but important updates (telemetry, status, errors).

## Areas to audit

1. **Bridge direct tunnel**: review all `hub.On` handlers for synchronous or long-running work; consider a bounded worker pool for LLM requests.
2. **Singleton services with mutable state**: `ModelBridgeRegistry`, `BridgeMetricsCollector`, `SessionStore`, `AgentService`, `CollectiveOrchestrator`, etc. Ensure long operations do not run inside locks or on the calling thread.
3. **Blazor components with high-frequency renders**: streaming loops, timers, progress indicators. Prefer batching/coalescing renders and moving heavy work off the dispatcher.
4. **SignalR server hub options**: review `MaximumParallelInvocationsPerClient`, `MaximumReceiveMessageSize`, and hub method design for server-side hubs (`ModelBridgeHub`, `ComponentHub`, etc.).
5. **Database / subprocess access in singletons**: e.g. `BridgeMetricsCollector` runs `top`, `vm_stat`, `ioreg`. Consider whether these should be isolated to a dedicated thread or process.

## Suggested direction

Adopt an explicit "offload and cache" pattern for cross-cutting telemetry/heartbeat data, and a "render coalescing" pattern for UI components. Evaluate whether some singletons should be partitioned per user/circuit or replaced with actor/channel-based concurrency.

## Files involved

- `src/AriaAgent/Aria.Bridge/DirectTunnel.cs`
- `src/AriaAgent/Aria.Bridge/Services/BridgeMetricsCollector.cs`
- `src/AriaAgent/Aria.Bridge/Services/BridgeMetricsHostedService.cs`
- `src/AriaAgent/Aria.Web/ServiceCollectionExtensions.cs`
- `src/AriaAgent/Aria.Web/Components/Pages/Chat.razor`
- `src/AriaAgent/Aria.Web/Components/Pages/Chat.razor.cs`
