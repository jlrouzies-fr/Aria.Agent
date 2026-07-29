using Aria.Agent;
using Aria.Harness.Context;
using Aria.Harness.Formats;

namespace Aria.Console.Harness;

/// <summary>
/// In-memory format cache for the console host. Persists nothing between runs.
/// </summary>
public sealed class ConsoleFormatCache : IFormatCache
{
    private readonly Dictionary<string, ThinkingFormat> _thinking = new();
    private readonly Dictionary<string, ToolCallFormat> _toolCalls = new();
    private readonly Dictionary<string, VisionSupport> _vision = new();
    private readonly Dictionary<string, ContextWindow> _contextWindows = new();

    public Task<ThinkingFormat?> GetThinkingFormatAsync(string sourceUrl, string modelId, CancellationToken ct = default)
    {
        var key = Key(sourceUrl, modelId);
        return _thinking.TryGetValue(key, out var value)
            ? Task.FromResult<ThinkingFormat?>(value)
            : Task.FromResult<ThinkingFormat?>(null);
    }

    public Task SetThinkingFormatAsync(string sourceUrl, string modelId, ThinkingFormat format, CancellationToken ct = default)
    {
        _thinking[Key(sourceUrl, modelId)] = format;
        return Task.CompletedTask;
    }

    public Task<ToolCallFormat?> GetToolCallFormatAsync(string sourceUrl, string modelId, CancellationToken ct = default)
    {
        var key = Key(sourceUrl, modelId);
        return _toolCalls.TryGetValue(key, out var value)
            ? Task.FromResult<ToolCallFormat?>(value)
            : Task.FromResult<ToolCallFormat?>(null);
    }

    public Task SetToolCallFormatAsync(string sourceUrl, string modelId, ToolCallFormat format, CancellationToken ct = default)
    {
        _toolCalls[Key(sourceUrl, modelId)] = format;
        return Task.CompletedTask;
    }

    public Task<VisionSupport?> GetVisionSupportAsync(string sourceUrl, string modelId, CancellationToken ct = default)
    {
        var key = Key(sourceUrl, modelId);
        return Task.FromResult<VisionSupport?>(_vision.TryGetValue(key, out var value) ? value : null);
    }

    public Task SetVisionSupportAsync(string sourceUrl, string modelId, VisionSupport support, CancellationToken ct = default)
    {
        _vision[Key(sourceUrl, modelId)] = support;
        return Task.CompletedTask;
    }

    public Task<ContextWindow?> GetContextWindowAsync(string sourceUrl, string modelId, CancellationToken ct = default)
    {
        var key = Key(sourceUrl, modelId);
        return Task.FromResult<ContextWindow?>(_contextWindows.TryGetValue(key, out var value) ? value : null);
    }

    public Task SetContextWindowAsync(string sourceUrl, string modelId, ContextWindow window, CancellationToken ct = default)
    {
        _contextWindows[Key(sourceUrl, modelId)] = window;
        return Task.CompletedTask;
    }

    private readonly HashSet<string> _confirmed = new();

    public Task ConfirmFormatsAsync(string sourceUrl, string modelId, ThinkingFormat thinking, ToolCallFormat toolCall, CancellationToken ct = default)
    {
        if (thinking == ThinkingFormat.Unknown) thinking = ThinkingFormat.None;
        if (toolCall == ToolCallFormat.Unknown) toolCall = ToolCallFormat.None;
        var key = Key(sourceUrl, modelId);
        _thinking[key]  = thinking;
        _toolCalls[key] = toolCall;
        _confirmed.Add(key);
        return Task.CompletedTask;
    }

    public Task<bool> IsConfirmedAsync(string sourceUrl, string modelId, CancellationToken ct = default)
        => Task.FromResult(_confirmed.Contains(Key(sourceUrl, modelId)));

    public Task ClearAsync(string sourceUrl, string modelId, CancellationToken ct = default)
    {
        var key = Key(sourceUrl, modelId);
        _thinking.Remove(key);
        _toolCalls.Remove(key);
        _vision.Remove(key);
        _contextWindows.Remove(key);
        _confirmed.Remove(key);
        return Task.CompletedTask;
    }

    private static string Key(string sourceUrl, string modelId) => $"{sourceUrl}::{modelId}";
}
