using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Shared;

/// <summary>
/// Base for <see cref="DebouncedInput"/> and <see cref="DebouncedTextArea"/>.
/// Delays the <see cref="ValueChanged"/> callback until the user pauses typing,
/// eliminating the per-keystroke Blazor Server round-trip that causes lost characters
/// and flicker on deployed apps.
/// </summary>
public abstract class DebouncedInputBase : ComponentBase, IAsyncDisposable
{
    [Inject] protected IJSRuntime JS { get; set; } = null!;

    [Parameter] public string Value { get; set; } = "";
    [Parameter] public EventCallback<string> ValueChanged { get; set; }

    /// <summary>Milliseconds to wait after the last input event before notifying .NET.</summary>
    [Parameter] public int DebounceMs { get; set; } = 150;

    [Parameter] public string? Id { get; set; }
    [Parameter] public string? Class { get; set; }
    [Parameter] public string? Placeholder { get; set; }

    [Parameter] public EventCallback<KeyboardEventArgs> OnKeyDown { get; set; }
    [Parameter] public EventCallback<FocusEventArgs> OnBlur { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? AdditionalAttributes { get; set; }

    protected ElementReference InputElement { get; set; }

    private DotNetObjectReference<DebouncedInputBase>? _dotNetRef;
    private string? _currentValue;
    private bool _initialized;

    protected override void OnParametersSet()
    {
        var incoming = Value ?? "";
        if (_currentValue is null)
        {
            _currentValue = incoming;
        }
        else if (_currentValue != incoming)
        {
            // The value was changed from the .NET side (e.g. clearing after send, picker selection).
            // Cancel any pending debounce in JS and sync the DOM so a stale callback doesn't
            // overwrite the new value.
            _currentValue = incoming;
            if (_initialized)
            {
                _ = SafeJsVoidAsync("ariaInterop.debouncedInput.setValue", InputElement, incoming);
            }
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            _currentValue = Value ?? "";
            await JS.InvokeVoidAsync("ariaInterop.debouncedInput.init", InputElement, _dotNetRef, DebounceMs);
            _initialized = true;
            // The markup renders no value attribute (so Blazor diffs can never rewrite the text
            // mid-typing) — seed the initial value through the same JS path all later .NET-driven
            // changes use (OnParametersSet → setValue).
            if (!string.IsNullOrEmpty(_currentValue))
                await SafeJsVoidAsync("ariaInterop.debouncedInput.setValue", InputElement, _currentValue);
        }
    }

    /// <summary>Called from JS after the debounce timer fires or on blur.</summary>
    [JSInvokable("NotifyValue")]
    public async Task NotifyValueAsync(string? value)
    {
        var incoming = value ?? "";
        if (_currentValue == incoming) return;
        _currentValue = incoming;
        await ValueChanged.InvokeAsync(incoming);
    }

    protected async Task HandleKeyDown(KeyboardEventArgs e)
    {
        // Commit the current value immediately so the parent sees the latest text
        // before acting on the key (e.g. sending a message on Enter).
        if (e.Key is "Enter")
        {
            await SafeJsVoidAsync("ariaInterop.debouncedInput.flush", InputElement);
        }

        if (OnKeyDown.HasDelegate)
            await OnKeyDown.InvokeAsync(e);
    }

    protected async Task HandleBlur(FocusEventArgs e)
    {
        await SafeJsVoidAsync("ariaInterop.debouncedInput.flush", InputElement);

        if (OnBlur.HasDelegate)
            await OnBlur.InvokeAsync(e);
    }

    /// <summary>Forces the pending debounce (if any) to commit immediately. Call this from a Save
    /// button's handler before reading the bound value — relying on blur alone races a click that
    /// follows fast (e.g. paste-then-click), since blur's round trip may not finish before the
    /// click's handler runs.</summary>
    public Task FlushAsync() => SafeJsVoidAsync("ariaInterop.debouncedInput.flush", InputElement);

    public async ValueTask DisposeAsync()
    {
        if (_dotNetRef is not null)
        {
            await SafeJsVoidAsync("ariaInterop.debouncedInput.destroy", InputElement);
            _dotNetRef.Dispose();
            _dotNetRef = null;
            _initialized = false;
        }
    }

    private async Task SafeJsVoidAsync(string identifier, params object?[]? args)
    {
        try
        {
            await JS.InvokeVoidAsync(identifier, args ?? Array.Empty<object?>());
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone; nothing to sync.
        }
        catch (InvalidOperationException)
        {
            // JS runtime not ready or already torn down.
        }
    }
}
