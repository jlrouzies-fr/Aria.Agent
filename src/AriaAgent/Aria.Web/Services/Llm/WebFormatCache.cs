using Aria.Agent;
using Aria.Harness.Formats;
using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;

using ThinkingFormat = Aria.Harness.Formats.ThinkingFormat;

namespace Aria.Web.Services.Llm;

/// <summary>
/// SQLite-backed implementation of <see cref="IFormatCache"/>.
/// Mirrors the original in-memory + DB caching from AgentService.
/// </summary>
public sealed class WebFormatCache : IFormatCache
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly Dictionary<string, ThinkingFormat> _thinking = new();
    private readonly Dictionary<string, ToolCallFormat> _toolCalls = new();
    private readonly Dictionary<string, VisionSupport> _vision = new();
    private readonly HashSet<string> _confirmed = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _loaded;

    public WebFormatCache(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<ThinkingFormat?> GetThinkingFormatAsync(string sourceUrl, string modelId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var key = Key(sourceUrl, modelId);
        return _thinking.TryGetValue(key, out var value) ? value : null;
    }

    public async Task SetThinkingFormatAsync(string sourceUrl, string modelId, ThinkingFormat format, CancellationToken ct = default)
    {
        var key = Key(sourceUrl, modelId);
        await _lock.WaitAsync(ct);
        try
        {
            _thinking[key] = format;
            await PersistAsync(sourceUrl, modelId, format, null, null, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<VisionSupport?> GetVisionSupportAsync(string sourceUrl, string modelId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var key = Key(sourceUrl, modelId);
        return _vision.TryGetValue(key, out var value) ? value : null;
    }

    public async Task SetVisionSupportAsync(string sourceUrl, string modelId, VisionSupport support, CancellationToken ct = default)
    {
        var key = Key(sourceUrl, modelId);
        await _lock.WaitAsync(ct);
        try
        {
            _vision[key] = support;
            await PersistAsync(sourceUrl, modelId, null, null, support, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<ToolCallFormat?> GetToolCallFormatAsync(string sourceUrl, string modelId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var key = Key(sourceUrl, modelId);
        return _toolCalls.TryGetValue(key, out var value) ? value : null;
    }

    public async Task SetToolCallFormatAsync(string sourceUrl, string modelId, ToolCallFormat format, CancellationToken ct = default)
    {
        var key = Key(sourceUrl, modelId);
        await _lock.WaitAsync(ct);
        try
        {
            _toolCalls[key] = format;
            await PersistAsync(sourceUrl, modelId, null, format, null, ct);
        }
        finally { _lock.Release(); }
    }

    private static string Key(string sourceUrl, string modelId) => $"{sourceUrl}::{modelId}";

    /// <summary>Deletes ALL cached format detections (DB + memory) so every model re-probes on its
    /// next session. Cheap recovery hammer for any stale/misrouted detection.</summary>
    public async Task<int> PurgeAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var count = await db.ModelFormatCaches.ExecuteDeleteAsync(ct);
            _thinking.Clear();
            _toolCalls.Clear();
            _vision.Clear();
            return count;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Deletes every cached format (DB + memory) whose model id contains the given fragment.
    /// Used to recover from a wrong detection that was persisted (cached verdicts are never re-probed).</summary>
    public async Task<int> PurgeAsync(string modelIdContains, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var rows = await db.ModelFormatCaches
                .Where(c => EF.Functions.Like(c.ModelId, $"%{modelIdContains}%"))
                .ToListAsync(ct);
            db.ModelFormatCaches.RemoveRange(rows);
            await db.SaveChangesAsync(ct);

            foreach (var key in _thinking.Keys
                         .Where(k => k.Contains(modelIdContains, StringComparison.OrdinalIgnoreCase)).ToList())
                _thinking.Remove(key);
            foreach (var key in _toolCalls.Keys
                         .Where(k => k.Contains(modelIdContains, StringComparison.OrdinalIgnoreCase)).ToList())
                _toolCalls.Remove(key);
            foreach (var key in _vision.Keys
                         .Where(k => k.Contains(modelIdContains, StringComparison.OrdinalIgnoreCase)).ToList())
                _vision.Remove(key);

            return rows.Count;
        }
        finally { _lock.Release(); }
    }

    public async Task ConfirmFormatsAsync(string sourceUrl, string modelId,
        ThinkingFormat thinking, ToolCallFormat toolCall, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        // Map "couldn't detect" to its safe runtime default so the stored decision is concrete:
        // no thinking handling, and native tool calls. Both are then respected without re-probing.
        if (thinking == ThinkingFormat.Unknown) thinking = ThinkingFormat.None;
        if (toolCall == ToolCallFormat.Unknown) toolCall = ToolCallFormat.None;

        var key = Key(sourceUrl, modelId);
        await _lock.WaitAsync(ct);
        try
        {
            _thinking[key]  = thinking;
            _toolCalls[key] = toolCall;
            _confirmed.Add(key);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entry = await db.ModelFormatCaches
                .FirstOrDefaultAsync(c => c.EndpointUrl == sourceUrl && c.ModelId == modelId, ct);
            if (entry == null)
            {
                entry = new ModelFormatCache { EndpointUrl = sourceUrl, ModelId = modelId };
                db.ModelFormatCaches.Add(entry);
            }
            entry.ThinkingFormat = thinking.ToString();
            entry.ToolCallFormat = toolCall.ToString();
            entry.Confirmed      = true;
            entry.DetectedAt     = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> IsConfirmedAsync(string sourceUrl, string modelId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _confirmed.Contains(Key(sourceUrl, modelId));
    }

    public async Task ClearAsync(string sourceUrl, string modelId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var key = Key(sourceUrl, modelId);
        await _lock.WaitAsync(ct);
        try
        {
            _thinking.Remove(key);
            _toolCalls.Remove(key);
            _vision.Remove(key);
            _confirmed.Remove(key);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await db.ModelFormatCaches
                .Where(c => c.EndpointUrl == sourceUrl && c.ModelId == modelId)
                .ExecuteDeleteAsync(ct);
        }
        finally { _lock.Release(); }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entries = await db.ModelFormatCaches.ToListAsync(ct);

            foreach (var entry in entries)
            {
                var key = Key(entry.EndpointUrl, entry.ModelId);
                if (entry.Confirmed) _confirmed.Add(key);

                // A confirmed row is authoritative even when it stores None (human said "no thinking"),
                // so it must be loaded and returned to stop re-probing. An unconfirmed row is only a
                // cached positive — None/Unknown there means "not yet decided", so it is not loaded.
                if (Enum.TryParse<ThinkingFormat>(entry.ThinkingFormat, out var tf)
                    && tf != ThinkingFormat.Unknown
                    && (entry.Confirmed || tf != ThinkingFormat.None))
                    _thinking[key] = tf;

                if (Enum.TryParse<ToolCallFormat>(entry.ToolCallFormat, out var tcf)
                    && tcf != ToolCallFormat.Unknown)
                    _toolCalls[key] = tcf;

                if (Enum.TryParse<VisionSupport>(entry.VisionSupport, out var vs)
                    && vs != VisionSupport.Unknown)
                    _vision[key] = vs;
            }

            _loaded = true;
        }
        finally { _lock.Release(); }
    }

    private async Task PersistAsync(string sourceUrl, string modelId,
        ThinkingFormat? thinking, ToolCallFormat? toolCall, VisionSupport? vision, CancellationToken ct)
    {
        bool hasThinking = thinking.HasValue && thinking != ThinkingFormat.None && thinking != ThinkingFormat.Unknown;
        bool hasToolCall = toolCall.HasValue && toolCall != ToolCallFormat.Unknown;
        bool hasVision   = vision.HasValue && vision != VisionSupport.Unknown;
        if (!hasThinking && !hasToolCall && !hasVision) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entry = await db.ModelFormatCaches
                .FirstOrDefaultAsync(c => c.EndpointUrl == sourceUrl && c.ModelId == modelId, ct);

            if (entry == null)
            {
                entry = new ModelFormatCache { EndpointUrl = sourceUrl, ModelId = modelId };
                db.ModelFormatCaches.Add(entry);
            }

            if (hasThinking) entry.ThinkingFormat = thinking!.Value.ToString();
            if (hasToolCall) entry.ToolCallFormat = toolCall!.Value.ToString();
            if (hasVision)   entry.VisionSupport  = vision!.Value.ToString();
            entry.DetectedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
        }
        catch { /* best-effort cache persistence */ }
    }
}
