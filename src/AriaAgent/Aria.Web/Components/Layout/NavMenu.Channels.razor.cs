using System.Text.Json;
using Aria.Agent;
using Aria.Web.Data;
using Aria.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Layout;

public partial class NavMenu
{
    // Channels are node-authoritative: this list mirrors the bridge's GET /channels (read-only).
    internal List<UserLocalSource> _userLocalDbSources = [];
    internal List<ModelSource>     _userSources        = [];

    // Vox config state
    internal UserVoxSettings? _voxSettings;
    internal bool    _voxModal;
    internal string? _voxTranscriptionSelect;
    internal string? _voxFixingSelect;
    internal bool    _voxSaved;

    // Names of channels that have a key stored on some node of this soul (union across nodes).
    internal HashSet<string> _configuredProviders = [];

    // Only OpenAI and Groq support /audio/transcriptions
    internal IEnumerable<ModelSource> TranscriptionChannels => _userSources
        .Where(s => (s.Name == "OpenAI" || s.Name == "Groq") && _configuredProviders.Contains(s.Name));

    internal IEnumerable<ModelSource> FixingChannels => _userSources
        .Where(s => s.IsPublicProvider
            ? _configuredProviders.Contains(s.Name)
            : s.Models.Count > 0);

    internal (string name, string? model) GetDefaultSource()
    {
        var mac = _userSources.FirstOrDefault(s => s.Name == "Local LLM - Mac (localhost)");
        if (mac != null)
            return (mac.Name, mac.Models.FirstOrDefault(m => m.Contains("gemma")) ?? mac.Models.FirstOrDefault());
        var first = _userSources.FirstOrDefault();
        return first != null ? (first.Name, first.Models.FirstOrDefault()) : ("", null);
    }

    internal List<(string Value, string Label)> TranscriptionOptions()
    {
        // Empty value = browser built-in speech recognition (the default, no setup needed).
        // Make it an explicit, selectable entry so the dropdown is never blank when no
        // cloud Whisper channel (OpenAI/Groq) has a key configured.
        var list = new List<(string, string)> { ("", "Browser (built-in, no setup)") };
        // Local Whisper runs entirely on the node — pick a size, download once, then fully offline.
        // Encoded as "Local:<size>" so the single settings string carries the chosen model.
        foreach (var sz in LocalWhisperSizes)
            list.Add(($"Local:{sz.Size}", $"Local Whisper — {sz.Label} (offline)"));
        list.AddRange(TranscriptionChannels.Select(s => (s.Name, $"{s.Name} (Whisper)")));
        return list;
    }

    // Friendly label for a stored transcription channel value (handles the "Local:<size>" encoding).
    internal static string VoxChannelLabel(string channel)
    {
        if (channel.StartsWith("Local:"))
        {
            var sz = channel["Local:".Length..];
            var lbl = LocalWhisperSizes.FirstOrDefault(s => s.Size == sz).Label ?? sz;
            return $"Local · {lbl}";
        }
        return channel;
    }

    // ── Local (on-device) Whisper models ──────────────────────────────────────
    // Offered sizes mirror the bridge's LocalWhisperService catalog (kept in sync by hand).
    internal static readonly (string Size, string Label)[] LocalWhisperSizes =
        [("tiny", "Tiny"), ("base", "Base"), ("small", "Small"), ("medium", "Medium")];

    internal sealed record VoxLocalModel(
        string Size, string Label, long ApproxBytes, bool Downloaded, bool Downloading, int Progress, string? Error);

    internal List<VoxLocalModel> _voxLocalModels = [];
    internal string? _voxLocalError;
    private bool _voxLocalPolling;

    // The model size the user has currently selected in the dropdown ("Local:base" → "base"), or null.
    internal string? SelectedLocalSize =>
        _voxTranscriptionSelect?.StartsWith("Local:") == true ? _voxTranscriptionSelect["Local:".Length..] : null;

    internal VoxLocalModel? SelectedLocalModel =>
        SelectedLocalSize is { } s ? _voxLocalModels.FirstOrDefault(m => m.Size == s) : null;

    internal async Task RefreshVoxLocalStatusAsync()
    {
        var userId = SessionState.CurrentUser?.Id;
        if (string.IsNullOrEmpty(userId)) return;
        try
        {
            // Route through the bridge tunnel (server→node), not a cross-origin browser fetch —
            // the node's LocalOriginMiddleware rejects direct browser POSTs; the tunnel is exempt.
            var resp = await BridgeRegistry.SendLocalRestAsync(userId, "GET", "/transcribe/local/status");
            var json = resp?.Body;
            if (string.IsNullOrEmpty(json)) return;
            using var doc = JsonDocument.Parse(json);
            var models = new List<VoxLocalModel>();
            foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
                models.Add(new VoxLocalModel(
                    m.GetProperty("size").GetString() ?? "",
                    m.GetProperty("label").GetString() ?? "",
                    m.GetProperty("approxBytes").GetInt64(),
                    m.GetProperty("downloaded").GetBoolean(),
                    m.GetProperty("downloading").GetBoolean(),
                    m.GetProperty("progress").GetInt32(),
                    m.TryGetProperty("error", out var e) ? e.GetString() : null));
            _voxLocalModels = models;
            StateHasChanged();
        }
        catch { /* bridge unreachable — leave list as-is */ }
    }

    internal async Task DownloadLocalModelAsync(string size)
    {
        var userId = SessionState.CurrentUser?.Id;
        if (string.IsNullOrEmpty(userId)) return;

        _voxLocalError = null;
        // Optimistic feedback: flip to a "downloading" state immediately so the click always shows.
        _voxLocalModels = _voxLocalModels
            .Select(m => m.Size == size ? m with { Downloading = true, Progress = 0, Error = null } : m)
            .ToList();
        StateHasChanged();

        // Trigger the download on the node via the tunnel (the node does the actual fetch from HF).
        var resp = await BridgeRegistry.SendLocalRestAsync(
            userId, "POST", $"/transcribe/local/download?size={Uri.EscapeDataString(size)}");
        if (resp is not { StatusCode: >= 200 and < 300 })
        {
            _voxLocalError = "Could not reach your node to start the download. " +
                             "Make sure the bridge is connected (green soul light), then try again.";
            _voxLocalModels = _voxLocalModels
                .Select(m => m.Size == size ? m with { Downloading = false } : m).ToList();
            StateHasChanged();
            return;
        }
        _ = PollVoxLocalAsync();
    }

    // Poll the bridge while any model is downloading and the modal is open, so the progress bar moves.
    // Runs off the render sync-context (fire-and-forget), so every UI touch is marshalled via InvokeAsync.
    private async Task PollVoxLocalAsync()
    {
        if (_voxLocalPolling) return;
        _voxLocalPolling = true;
        try
        {
            do
            {
                await Task.Delay(1000);
                await InvokeAsync(RefreshVoxLocalStatusAsync);
            }
            while (_voxModal && _voxLocalModels.Any(m => m.Downloading));
        }
        finally { _voxLocalPolling = false; }
    }

    internal List<(string Value, string Label)> FixingOptions()
    {
        var list = new List<(string, string)> { ("", "None — use raw transcript") };
        list.AddRange(FixingChannels.Select(s => (s.Name, s.Name)));
        return list;
    }

    internal Task TrySelectSource(string name, int modelCount) =>
        modelCount > 0 ? SelectSource(name) : Task.CompletedTask;

    internal async Task SelectSource(string name)
    {
        SessionState.SelectedModelSource = name;
        var source = _userSources.FirstOrDefault(s => s.Name == name);
        string? savedModel = null;
        if (SessionState.CurrentUser != null)
            savedModel = await UserService.GetSourcePreferenceAsync(SessionState.CurrentUser.Id, name);
        SessionState.SelectedModel = savedModel ?? source?.Models.FirstOrDefault();
        if (SessionState.CurrentUser != null)
            await UserService.SaveLastModelSourceAsync(SessionState.CurrentUser.Id, name);
    }

    internal async Task SelectModel(string sourceName, string modelId)
    {
        if (SessionState.SelectedModelSource != sourceName)
            SessionState.SelectedModelSource = sourceName;
        SessionState.SelectedModel = modelId;
        if (SessionState.CurrentUser != null)
        {
            await UserService.SaveSourcePreferenceAsync(SessionState.CurrentUser.Id, sourceName, modelId);
            await UserService.SaveLastModelSourceAsync(SessionState.CurrentUser.Id, sourceName);
        }
    }

    // Channel names currently re-querying their endpoint for models (drives the ⟳ spinner state).
    internal HashSet<string> _rediscoveringChannels = [];

    internal async Task RediscoverModelsAsync(UserLocalSource src)
    {
        var userId = SessionState.CurrentUser?.Id.ToString();
        if (string.IsNullOrEmpty(userId) || src.BridgeNodeId == null) return;

        var key = src.BridgeNodeId + "|" + src.Name;
        if (!_rediscoveringChannels.Add(key)) return;
        StateHasChanged();
        try
        {
            var ok = await LocalSourceService.RediscoverModelsAsync(userId, src.BridgeNodeId, src.ChannelName ?? src.Name);
            ShowChannelNotice(ok ? $"Refreshed models for {src.Name}" : $"Could not discover models for {src.Name}");
            if (ok) _userLocalDbSources = LocalSourceService.GetCustomCached(userId);
        }
        finally
        {
            _rediscoveringChannels.Remove(key);
            StateHasChanged();
        }
    }

    // Transient feedback line shown in the Channels panel (auto-clears).
    internal string? _channelNotice;
    internal void ShowChannelNotice(string text)
    {
        _channelNotice = text;
        StateHasChanged();
        _ = InvokeAsync(async () =>
        {
            await Task.Delay(8000);
            if (_channelNotice == text) { _channelNotice = null; StateHasChanged(); }
        });
    }

    // ── Model format cache ────────────────────────────────────────────────────

    internal bool    _formatCacheClearing;
    internal string? _formatCacheMsg;

    internal async Task ClearModelFormatCacheAsync()
    {
        if (_formatCacheClearing) return;
        _formatCacheClearing = true;
        StateHasChanged();
        try
        {
            var n = await AgentService.PurgeAllModelFormatsAsync();
            _formatCacheMsg = $"Cleared {n} cached format verdict{(n == 1 ? "" : "s")} — channels re-probe on next session.";
        }
        catch { _formatCacheMsg = "Failed to clear the format cache."; }
        _formatCacheClearing = false;
        StateHasChanged();
        await Task.Delay(4000);
        _formatCacheMsg = null;
        StateHasChanged();
    }

    // ── Vox modal ─────────────────────────────────────────────────────────────

    internal void OpenVoxModal()
    {
        _voxTranscriptionSelect = _voxSettings?.TranscriptionChannelName ?? "";
        _voxFixingSelect        = _voxSettings?.FixingChannelName ?? "";
        _voxSaved               = false;
        _voxModal               = true;
        _voxLocalError          = null;
        // Pull current on-device model state (downloaded / in-flight) from the node.
        _ = InvokeAsync(async () =>
        {
            await RefreshVoxLocalStatusAsync();
            if (_voxLocalModels.Any(m => m.Downloading)) _ = PollVoxLocalAsync();
        });
    }

    internal void CloseVoxModal()
    {
        _voxModal = false;
        _voxSaved = false;
    }

    internal async Task SaveVoxSettingsAsync()
    {
        if (SessionState.CurrentUser == null) return;
        var transcription = string.IsNullOrEmpty(_voxTranscriptionSelect) ? null : _voxTranscriptionSelect;
        var fixing        = string.IsNullOrEmpty(_voxFixingSelect) ? null : _voxFixingSelect;
        await VoxService.SaveSettingsAsync(SessionState.CurrentUser.Id, transcription, fixing);
        _voxSettings = new UserVoxSettings
        {
            UserId = SessionState.CurrentUser.Id,
            TranscriptionChannelName = transcription,
            FixingChannelName        = fixing
        };
        _voxSaved = true;
        StateHasChanged();
        await Task.Delay(1200);
        CloseVoxModal();
        StateHasChanged();
    }

    // ── Provider icons ────────────────────────────────────────────────────────

    internal static string ProviderIcon(ModelSource source) => source.IsPublicProvider
        ? source.Name switch
        {
            "OpenAI"        => "🤖",
            "Anthropic"     => "🔶",
            "Google Gemini" => "💎",
            "Mistral"       => "💨",
            "Groq"          => "⚡",
            _               => "🔮"
        }
        : "🖥️";

    // Cloud-provider keys are stored on the local bridges, never on the server. The key icon means
    // "some bridge of this soul holds a key for this name" — union across the soul's online nodes.
    internal async Task RefreshConfiguredProvidersAsync()
    {
        var userId = SessionState.CurrentUser?.Id.ToString();
        if (userId == null) { _configuredProviders = []; return; }

        var set = new HashSet<string>();
        foreach (var node in BridgeRegistry.GetNodes(userId))
        {
            var resp = await BridgeRegistry.SendLocalRestAsync(userId, "GET", "/keys", null, node.NodeId);
            if (resp is not { StatusCode: 200, Body: { } body }) continue;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("providers", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var el in arr.EnumerateArray())
                        if (el.GetString() is { } s) set.Add(s);
            }
            catch { /* skip malformed node response */ }
        }
        _configuredProviders = set;
    }
}
