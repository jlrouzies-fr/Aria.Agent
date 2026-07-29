using Aria.Agent;
using Aria.Harness.Context;

namespace Aria.Harness.Formats;

/// <summary>
/// Abstracts format detection caching so Web can use SQLite and Console can use memory.
/// </summary>
public interface IFormatCache
{
    Task<ThinkingFormat?> GetThinkingFormatAsync(string sourceUrl, string modelId, CancellationToken ct = default);
    Task SetThinkingFormatAsync(string sourceUrl, string modelId, ThinkingFormat format, CancellationToken ct = default);

    Task<ToolCallFormat?> GetToolCallFormatAsync(string sourceUrl, string modelId, CancellationToken ct = default);
    Task SetToolCallFormatAsync(string sourceUrl, string modelId, ToolCallFormat format, CancellationToken ct = default);

    Task<VisionSupport?> GetVisionSupportAsync(string sourceUrl, string modelId, CancellationToken ct = default);
    Task SetVisionSupportAsync(string sourceUrl, string modelId, VisionSupport support, CancellationToken ct = default);

    /// <summary>
    /// Discover and cache the model's context window, if known. Stored per source+model like the
    /// format verdicts; <see cref="ContextWindow.Assumed"/> true means the value is a fallback and
    /// must not change today's behaviour.
    /// </summary>
    Task<ContextWindow?> GetContextWindowAsync(string sourceUrl, string modelId, CancellationToken ct = default);
    Task SetContextWindowAsync(string sourceUrl, string modelId, ContextWindow window, CancellationToken ct = default);

    /// <summary>
    /// Persist a human-accepted detection as authoritative. Unlike the automatic setters this stores
    /// the decision even when it is <c>None</c>/<c>Unknown</c> (mapped to a safe runtime default), marks
    /// it confirmed, and thereby stops any further automatic re-probing for this source/model.
    /// </summary>
    Task ConfirmFormatsAsync(string sourceUrl, string modelId, ThinkingFormat thinking, ToolCallFormat toolCall, CancellationToken ct = default);

    /// <summary>True when a human has confirmed a detection for this source/model (see <see cref="ConfirmFormatsAsync"/>).</summary>
    Task<bool> IsConfirmedAsync(string sourceUrl, string modelId, CancellationToken ct = default);

    /// <summary>Forget any cached/confirmed detection for this source/model so the next session re-probes.</summary>
    Task ClearAsync(string sourceUrl, string modelId, CancellationToken ct = default);
}
