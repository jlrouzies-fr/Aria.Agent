# Fix: Vigil modal Bridge/Agent theme selects don't update on click

## Context

In the Vigil scheduler modal (full-screen `NavMenuVigilScheduler` → `CronSchedulerPanel` → `CronScheduleView`), the booking panel has two `ThemedSelect` dropdowns — `// DEVICE` (Bridge node) and `// AGENT` (sub-agent). Clicking their options appears to do nothing: the value never updates.

The Razor is **not** the bug. The `ThemedSelect` usage and the `OnBridgeNodeChanged` / `OnSubAgentChanged` handlers (`CronScheduleView.razor:193-207, 410-420`) are functionally identical to every working call site (e.g. `NavMenuAgentsPanel.razor:156-171`), and the timezone picker in the *same* modal updates fine via the same `Parent.Refresh()`.

The real cause is CSS clipping. `ThemedSelect`'s option list is `position: absolute; top: calc(100% + 2px)` — it opens **downward** (`app.css:4209-4221`, `.tsel-list`). The DEVICE/AGENT selects sit near the **bottom** of the modal, inside `.vigil-modal-body { overflow-y: auto }` (`app.css:3979-3984`). The downward-opening list overflows past the bottom of that scroll container and is clipped out of view, so the options can't be seen or clicked. The timezone picker escapes this only because it sits at the **top** of the modal with the whole calendar's height of room below it.

The codebase already solves this exact problem elsewhere: in the agent active bar, `ThemedSelect` is forced to open upward via `.agent-active-bar .tsel-list { top: auto; bottom: calc(100% + 2px); }` (`app.css:2989`).

## Change

Add one scoped CSS override so the booking-panel `ThemedSelect` lists open **upward** (into the visible region above the field) instead of downward into the clipped bottom. Mirror the existing `.agent-active-bar .tsel-list` pattern.

**File:** `Aria.Web/wwwroot/app.css` — near the other `.tsel-*` rules (~line 4221) or beside the `.vc-book-*` rules.

```css
/* Booking-panel selects sit at the bottom of the scrollable vigil modal —
   open the option list upward so it isn't clipped by .vigil-modal-body's overflow. */
.vc-book-panel .tsel-list {
    top: auto;
    bottom: calc(100% + 2px);
    z-index: 510;            /* above the in-modal tz dropdown (z-index 500) */
}
```

No `.razor` / C# changes are needed — the existing handlers already mutate `Parent._selectedBridgeNodeId` / `Parent._selectedSubAgentId` and call `Parent.Refresh()`.

## Verification

1. Rebuild + restart both apps per CLAUDE.md (`pkill … dotnet build … dotnet run` Bridge then Web).
2. Open the app at `http://localhost:5129`, open the Vigil scheduler, pick a free calendar slot to reveal the booking panel.
3. Click the `// DEVICE` select — the option list should appear (opening upward) fully on-screen; pick a node and confirm the control's label updates to the chosen node.
4. Repeat for `// AGENT` (requires at least one sub-agent so the field renders); confirm the label updates and the placeholder→value swap sticks.
5. Confirm the timezone picker at the top still works (no regression) and that `✦ INSCRIBE VIGIL` books with the selected device/agent.

## Notes

- This is `Aria.Web`-only (CSS); no `Aria.Bridge` change, so no bridge version bump.
- If, after this, the upward list is ever clipped at the top for a very short modal, the fallback is the same approach the tz picker uses (top placement / higher z-index) — but the booking panel always has the OPERATION textarea + calendar above it, so upward has room.
