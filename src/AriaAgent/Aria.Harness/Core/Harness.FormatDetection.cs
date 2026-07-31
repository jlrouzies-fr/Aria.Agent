using Aria.Agent;
using Aria.Harness.Context;
using Aria.Harness.Formats;
using Aria.Shared;
using Microsoft.Extensions.Logging;

namespace Aria.Harness.Core;

public sealed partial class Harness
{
    // ── Format detection (public surface) ─────────────────────────────────────

    public async Task<ToolCallFormat> DetectToolCallFormatAsync(
        string? selectedSourceName,
        string? modelId,
        HarnessContext context,
        CancellationToken ct = default)
    {
        var source = _runtime.FindSource(selectedSourceName, context);

        if (source?.IsPublicProvider == true)
            return ToolCallFormat.None;

        if (source != null)
        {
            var cached = await _runtime.FormatCache.GetToolCallFormatAsync(source.Url,
                modelId ?? source.Models.FirstOrDefault() ?? "", ct);
            if (cached.HasValue && cached.Value != ToolCallFormat.Unknown) return cached.Value;
        }

        if (source?.IsBridged == true)
        {
            // Bridge detection happens inside thinking detection; if not available, assume native.
            var thinkFmt = await DetectThinkingFormatAsync(selectedSourceName, modelId, context, ct);
            var cached = await _runtime.FormatCache.GetToolCallFormatAsync(source.Url,
                modelId ?? source.Models.FirstOrDefault() ?? "", ct);
            return cached ?? ToolCallFormat.None;
        }

        var format = await RunToolCallDetectionAsync(selectedSourceName, modelId, context, ct);
        _logger.LogInformation("Tool-call format for {Source}/{Model}: {Format}", selectedSourceName, modelId, format);

        if (source != null && format != ToolCallFormat.Unknown)
            await _runtime.FormatCache.SetToolCallFormatAsync(source.Url,
                modelId ?? source.Models.FirstOrDefault() ?? "", format, ct);

        return format;
    }

    public async Task<ThinkingFormat> DetectThinkingFormatAsync(
        string? selectedSourceName,
        string? modelId,
        HarnessContext context,
        CancellationToken ct = default)
    {
        var source = _runtime.FindSource(selectedSourceName, context);

        if (source?.IsPublicProvider == true)
            return ThinkingFormat.None;

        if (source != null)
        {
            var cached = await _runtime.FormatCache.GetThinkingFormatAsync(source.Url,
                modelId ?? source.Models.FirstOrDefault() ?? "", ct);
            if (cached.HasValue && cached.Value != ThinkingFormat.Unknown) return cached.Value;
        }

        ThinkingFormat format;
        if (source?.IsBridged == true)
        {
            format = await _runtime.IsBridgeAvailableAsync(context, ct)
                ? await RunBridgeDetectionAsync(source, modelId, context, ct)
                : ThinkingFormat.None;
        }
        else
        {
            format = await RunDetectionAsync(selectedSourceName, modelId, context, ct);
        }

        // NOTE: a model-name heuristic used to force StartsInThinkMode here when the probe returned
        // None. Removed: any probe failure (endpoint auth, server down) also yields no markers, and
        // forcing think-mode on a non-thinking stream swallows the ENTIRE answer into the thinking
        // block. The probe detects genuine start-in-think models via their closing tag, and
        // reasoning_content is handled dynamically regardless of the detected format.

        _logger.LogInformation("Thinking format for {Source}/{Model}: {Format}", selectedSourceName, modelId, format);

        if (source != null && format != ThinkingFormat.None && format != ThinkingFormat.Unknown)
            await _runtime.FormatCache.SetThinkingFormatAsync(source.Url,
                modelId ?? source.Models.FirstOrDefault() ?? "", format, ct);

        return format;
    }

    // Vision is probed for every source, including public/cloud providers — unlike thinking/tool-call
    // format, it varies per model within the same provider (e.g. gpt-4o vs a text-only variant), so it
    // can't be short-circuited on IsPublicProvider like the others.
    public async Task<VisionSupport> DetectVisionSupportAsync(
        string? selectedSourceName,
        string? modelId,
        HarnessContext context,
        CancellationToken ct = default)
    {
        var source = _runtime.FindSource(selectedSourceName, context);
        if (source == null) return VisionSupport.Unknown;

        var cached = await _runtime.FormatCache.GetVisionSupportAsync(source.Url,
            modelId ?? source.Models.FirstOrDefault() ?? "", ct);
        if (cached.HasValue && cached.Value != VisionSupport.Unknown) return cached.Value;

        VisionSupport support;
        if (source.IsBridged)
        {
            // RunBridgeDetectionAsync's /llm/detect-format round trip already probes + caches vision
            // alongside thinking/tool-call — reuse it instead of a second bridge call.
            if (await _runtime.IsBridgeAvailableAsync(context, ct))
            {
                await RunBridgeDetectionAsync(source, modelId, context, ct);
                var recached = await _runtime.FormatCache.GetVisionSupportAsync(source.Url,
                    modelId ?? source.Models.FirstOrDefault() ?? "", ct);
                support = recached ?? VisionSupport.Unknown;
            }
            else
            {
                support = VisionSupport.Unknown;
            }
        }
        else
        {
            support = await RunVisionDetectionAsync(selectedSourceName, modelId, context, ct);
        }

        _logger.LogInformation("Vision support for {Source}/{Model}: {Support}", selectedSourceName, modelId, support);

        if (support != VisionSupport.Unknown)
            await _runtime.FormatCache.SetVisionSupportAsync(source.Url,
                modelId ?? source.Models.FirstOrDefault() ?? "", support, ct);

        return support;
    }

    /// <summary>
    /// Resolves the effective context window for a source+model using the precedence order:
    /// 1) user override on the bridge channel configuration, 2) provider discovery cached from the
    /// format probe, 3) well-known cloud model catalog, 4) the 100k fallback marked assumed.
    /// </summary>
    public async Task<ContextWindow> ResolveContextWindowAsync(
        ModelSource? source, string? modelId, HarnessContext context, CancellationToken ct = default)
    {
        var model = modelId ?? source?.Models.FirstOrDefault() ?? "";
        if (source == null || string.IsNullOrEmpty(model))
            return ContextWindow.AssumedDefault;

        // 1. User override from the bridge channel configuration wins over everything.
        if (source.ContextWindow.HasValue)
        {
            _logger.LogInformation("Context window for {Source}/{Model}: {Tokens} (channel override)", source.Name, model, source.ContextWindow.Value);
            return new ContextWindow(source.ContextWindow.Value, false);
        }

        // 2. Cached provider discovery (populated by /llm/detect-format for bridged sources).
        var cached = await _runtime.FormatCache.GetContextWindowAsync(source.Url, model, ct);
        if (cached is { } cachedWindow)
        {
            _logger.LogInformation("Context window for {Source}/{Model}: {Tokens} (cached, assumed={Assumed})",
                source.Name, model, cachedWindow.Tokens, cachedWindow.Assumed);
            return cachedWindow;
        }

        // 3. For public/cloud providers, consult the static well-known model catalog.
        if (source.IsPublicProvider)
        {
            if (ContextWindowCatalog.TryGetKnownTokens(model) is { } knownTokens)
            {
                _logger.LogInformation("Context window for {Source}/{Model}: {Tokens} (catalog)", source.Name, model, knownTokens);
                return new ContextWindow(knownTokens, false);
            }
        }

        // 4. Fallback: today's behaviour, explicitly assumed.
        _logger.LogInformation("Context window for {Source}/{Model}: 100000 (assumed fallback)", source.Name, model);
        return ContextWindow.AssumedDefault;
    }

    public async Task<(ThinkingFormat Thinking, ToolCallFormat ToolCall)> ForceRedetectAsync(
        string sourceName,
        string modelId,
        HarnessContext context,
        CancellationToken ct = default)
    {
        var source = _runtime.FindSource(sourceName, context);
        if (source != null)
        {
            await _runtime.FormatCache.SetThinkingFormatAsync(source.Url, modelId, ThinkingFormat.Unknown, ct);
            await _runtime.FormatCache.SetToolCallFormatAsync(source.Url, modelId, ToolCallFormat.Unknown, ct);
        }

        var tf  = await DetectThinkingFormatAsync(sourceName, modelId, context, ct);
        var tcf = await DetectToolCallFormatAsync(sourceName, modelId, context, ct);
        return (tf, tcf);
    }
}
