using Aria.Agent;
using Aria.Harness.Formats;
using Aria.Harness.Models;
using Aria.Shared;
using OpenAI.Chat;

namespace Aria.Harness.Core;

public sealed partial class Harness
{
    // ── Chat client construction ──────────────────────────────────────────────

    private (ChatClient Client, UniversalReasoningHandler Handler)? BuildChatClient(HarnessOptions options, HarnessContext context, string? bridgeNodeId = null)
    {
        var source = _runtime.FindSource(options.SelectedSourceName, context);
        if (source == null) return null;

        var resolvedModel = options.SelectedModel ?? source.Models.FirstOrDefault() ?? "default";

        var routeViaBridge = source.IsBridged || source.IsPublicProvider;
        var keyRef         = routeViaBridge ? (source.ChannelName ?? source.Name) : null;
        var requireKey     = source.IsPublicProvider;

        HttpMessageHandler innerHandler = routeViaBridge
            ? new BridgeHttpHandler(_runtime, context, keyRef, requireKey, bridgeNodeId)
            : new HttpClientHandler();

        var handler = new UniversalReasoningHandler
        {
            InnerHandler       = innerHandler,
            OnReasoningContent = options.OnThinkingToken,
            StartsInThinkMode  = options.ThinkingFormat == ThinkingFormat.StartsInThinkMode,
            StreamThinkingLive = options.ThinkingFormat is ThinkingFormat.ReasoningContent
                                       or ThinkingFormat.ThinkTags
                                       or ThinkingFormat.StartsInThinkMode
                                       or ThinkingFormat.ChannelThought
                                       or ThinkingFormat.Harmony,
            // Only Functionary changes runtime tool parsing; every other format is marker-auto-detected.
            ForcedToolFormat   = options.ToolCallFormat
        };

        var client = ChatClientFactory.Build(source, resolvedModel, handler);
        return (client, handler);
    }
}
