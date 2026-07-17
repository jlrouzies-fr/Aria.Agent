# ThemedSelect dropdown doesn't show the selected value

## Symptom

A `ThemedSelect` dropdown opens, you click an option, and **nothing appears to happen** — the control keeps showing the placeholder (or the first option's label) and never displays what you picked. It looks like "clicking doesn't select anything / doesn't update the value."

This was first seen on the **Vigil scheduler** modal's `// DEVICE` (bridge node) and `// AGENT` (sub-agent) selects (`Aria.Web/Components/Shared/CronScheduleView.razor`).

Note: the *backing field actually does get set* (e.g. `_selectedBridgeNodeId` becomes the chosen node), so a downstream action like booking would use the right value — but the UI never reflects the choice, which makes it look completely broken.

## Root cause

`ThemedSelect.Value` is a **string-typed** parameter:

```csharp
[Parameter] public string? Value { get; set; }
```

In Razor, a bare quoted attribute on a **string** parameter is passed as a **literal string**, *not* a C# expression. So this:

```razor
<ThemedSelect Value="Parent._selectedBridgeNodeId" ... />
```

passes the literal text `"Parent._selectedBridgeNodeId"` as the value — it never reads the field. Because that literal matches no option, `ThemedSelect.DisplayLabel` falls back to the placeholder. When you click an option, `ChooseAsync` fires `ValueChanged` (so the parent's backing field updates), but on the very next parent re-render the literal string is re-applied to `Value`, which again matches no option — so the displayed label never changes.

Non-string parameters are immune: Razor treats them as expressions automatically. That's why `Options="VigilDeviceOptions()"` (a `List<...>`) works without `@`, but `Value` (a `string`) does not.

## How to investigate

1. Confirm the data flow is fine but the display is stale: temporarily print the backing field next to the control, e.g. `<label>selected = '@Parent._selectedBridgeNodeId'</label>`. If the field updates on click but the control label doesn't, it's a `Value` binding problem, not an event/state problem.
2. Add a one-line render log to `ThemedSelect` to see what `Value` it actually receives:
   ```csharp
   protected override bool ShouldRender()
   {
       Console.WriteLine($"[TSEL] render Value={Value}");
       return true;
   }
   ```
   If the log prints the **property path text** (`Value=Parent._selectedBridgeNodeId`) instead of the field's value, the `@` prefix is missing.
3. Compare against a working call site — every correct usage binds with `@`:
   `Value="@(Menu._editSourceName ?? "")"` (NavMenuAgentsPanel), `Value="@(Parent._editOvermindSubAgentId?.ToString() ?? "")"` (HiveSidebar).

This is *not* a CSS / z-index / overflow-clipping issue. The dropdown rendering and click handling are fine; only the `Value` binding is wrong. (An isolated repro: drop a `ThemedSelect` on a throwaway dev page, bind `Value="_x"` vs `Value="@_x"`, and observe that only the `@` version displays the selection.)

## Fix

Add the `@` prefix so `Value` is bound to the field rather than a literal:

```razor
<ThemedSelect Value="@Parent._selectedBridgeNodeId"
              ValueChanged="OnBridgeNodeChanged"
              Options="VigilDeviceOptions()" Placeholder="ANY CONNECTED NODE"
              ControlClass="vc-book-select" />

<ThemedSelect Value="@Parent._selectedSubAgentId"
              ValueChanged="OnSubAgentChanged"
              Options="VigilAgentOptions()" Placeholder="BASE ARIA"
              ControlClass="vc-book-select" />
```

**Rule of thumb:** whenever you bind a value to a *string-typed* component parameter, use `@` (or `@(...)`). Without it Razor silently treats it as literal text.

## Verification

1. Build and restart `Aria.Web`.
2. Open the Vigil scheduler, select a free calendar slot to reveal the booking panel.
3. Open the `// DEVICE` select and pick a node — the control label should immediately change to that node.
4. Repeat for `// AGENT` (needs at least one sub-agent for the field to render).
5. The placeholder should only show when nothing is selected.
