using Aria.Web.Data;
using Aria.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Pages;

public partial class Chat
{
    private bool   _isVoxActive;
    private bool   _isVoxFixing;
    private string? _voxFixingChannel;
    private string  _voxTranscriptionChannel = "";
    private DotNetObjectReference<Chat>? _voxRef;

    private async Task ToggleVoxAsync()
    {
        if (_isVoxFixing) return;

        if (_isVoxActive)
        {
            _isVoxActive = false;
            // Show spinner only for MediaRecorder path (async API call after stop).
            // Web Speech API fires OnVoxTranscript on its own — no spinner needed.
            _isVoxFixing = !string.IsNullOrEmpty(_voxTranscriptionChannel);
            StateHasChanged();
            await JS.InvokeVoidAsync("ariaInterop.stopVox");
            return;
        }

        var userId = SessionState.CurrentUser?.Id;
        UserVoxSettings? settings = userId is { Length: > 0 }
            ? await VoxService.GetSettingsAsync(userId)
            : null;
        _voxFixingChannel        = settings?.FixingChannelName;
        _voxTranscriptionChannel = settings?.TranscriptionChannelName ?? "";

        // Cloud Whisper transcription runs server-side, but cloud keys now live on the bridge
        // (key-custody) and are never handed to the server — so we no longer auto-select a cloud
        // channel. With no explicit channel set, Vox uses the browser's built-in speech recognition.

        _voxRef      = DotNetObjectReference.Create(this);
        _isVoxActive = true;
        StateHasChanged();

        try
        {
            await JS.InvokeVoidAsync("ariaInterop.startVox", _voxRef,
                userId ?? "", _voxTranscriptionChannel);
        }
        catch
        {
            _isVoxActive = false;
            _voxRef?.Dispose();
            _voxRef = null;
        }
    }

    [JSInvokable]
    public async Task OnVoxTranscript(string rawText)
    {
        _isVoxActive = false;
        _isVoxFixing = false; // clear transcription spinner; re-enabled below if LLM fixing runs

        if (string.IsNullOrWhiteSpace(rawText)) { StateHasChanged(); return; }

        if (!string.IsNullOrEmpty(_voxFixingChannel) && SessionState.CurrentUser != null)
        {
            _isVoxFixing = true;
            StateHasChanged();
            try
            {
                rawText = await AgentService.FixTranscriptAsync(rawText, _voxFixingChannel, SessionState.CurrentUser.Id);
            }
            finally
            {
                _isVoxFixing = false;
            }
        }

        _input = rawText;
        _voxRef?.Dispose();
        _voxRef = null;
        StateHasChanged();
        await JS.InvokeVoidAsync("ariaInterop.focusElement", "chatInput");
    }

    [JSInvokable]
    public Task OnVoxError(string error)
    {
        _isVoxActive = false;
        _isVoxFixing = false;
        _voxRef?.Dispose();
        _voxRef = null;
        _attachError = $"// VOX FAULT: {error}";
        return InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public Task OnVoxEnd()
    {
        // Not used by MediaRecorder path (stop is explicit), kept for safety
        _isVoxActive = false;
        _voxRef?.Dispose();
        _voxRef = null;
        return InvokeAsync(StateHasChanged);
    }
}
