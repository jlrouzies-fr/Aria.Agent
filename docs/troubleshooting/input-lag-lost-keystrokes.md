# Lag / lost keystrokes in text inputs on deployed Blazor Server

## Symptom

Typing fast in any text box or textarea feels sluggish, characters appear and disappear,
or backspace/delete behaves erratically. The problem is much worse on the deployed Fly.io
instance than when running `Aria.Web` locally.

## Root cause

Blazor Server sends **every input event to the server** and waits for a render diff before
updating the DOM. With real network latency the server and client can disagree about what
the field should contain; the framework resolves this with a "last write wins" merge, which
causes the visible flickering and lost characters.

The worst pattern is the manual one-way binding:

```razor
<input value="@_text" @oninput="e => _text = e.Value?.ToString()" />
```

`@bind:event="oninput"` is better because Blazor can suppress redundant DOM writes, but it
still round-trips on every keystroke.

## Fix

All text inputs and textareas use the shared debounced components:

- `Aria.Web/Components/Shared/DebouncedInput.razor`
- `Aria.Web/Components/Shared/DebouncedTextArea.razor`
- `Aria.Web/Components/Shared/DebouncedInputBase.cs`
- `Aria.Web/wwwroot/aria-interop.js` (`ariaInterop.debouncedInput`)

The JS helper buffers `input` events on the client and only notifies .NET after the user
pauses (default 150 ms), or immediately on blur / Enter. This removes the per-keystroke
SignalR round-trip and the merge conflict entirely.

### Second root cause: the rendered `value` attribute (fixed)

Debouncing alone was not enough. The components originally rendered `value="@Value"`, so when a
debounce commit round-tripped while the user *kept typing*, the re-render diff rewrote the DOM
input with the older committed text — eating the characters typed since (worst on deployed apps
with real latency). The markup now renders **no value attribute at all**: after JS init the DOM
owns the text, the initial value is seeded through `ariaInterop.debouncedInput.setValue`, and all
.NET-driven changes (clearing after send, picker selection) flow through that same JS path, which
also cancels pending debounces. Blazor re-renders can still update `disabled`, `class`, etc. —
they just can never touch the text.

> Do not "fix" this with `ShouldRender() => false`: that also freezes attribute updates, e.g. the
> chat composer's `disabled` flag never re-enables after streaming. This was tried and reverted.

### Correct usage

```razor
<DebouncedInput class="modal-input"
                Value="@Menu._name"
                ValueChanged="v => Menu._name = v"
                Placeholder="Name" />

<DebouncedTextArea class="modal-textarea" rows="4"
                   Value="@Menu._notes"
                   ValueChanged="v => Menu._notes = v"
                   Placeholder="Notes" />
```

Any standard HTML attribute (`type`, `rows`, `disabled`, `maxlength`, `inputmode`, `style`,
etc.) can be placed directly on the component tag; it is captured by
`AdditionalAttributes` and rendered on the underlying `<input>` or `<textarea>`.

### What to avoid

Do **not** add new `value` + `@oninput` text fields. The only remaining `@oninput` should be
on non-text controls such as `type="color"`.

If you need a value to update immediately on a specific key (e.g. the chat composer must
flush before sending on Enter), the component already flushes the pending debounce before
invoking `OnKeyDown`, so the parent always sees the latest text.

## Verification

1. Build and restart `Aria.Web`.
2. Type rapidly in the chat composer, skill editor, agent directives, Hive config, and
   channel source fields.
3. Characters should appear smoothly; pressing Enter should send/commit the exact current
   text; blur should persist the final value.

## Related files

- `Aria.Web/Components/Shared/DebouncedInput.razor`
- `Aria.Web/Components/Shared/DebouncedTextArea.razor`
- `Aria.Web/Components/Shared/DebouncedInputBase.cs`
- `Aria.Web/wwwroot/aria-interop.js`
