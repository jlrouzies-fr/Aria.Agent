using System.Text.Json;
using Aria.Harness.Core;
using Aria.Shared;
using Aria.Tools;
using Microsoft.Extensions.AI;

namespace Aria.Harness.Bridge;

/// <summary>
/// An AIFunction that calls a tool hosted by Aria.Bridge (terminal tools or stdio MCP servers).
/// </summary>
public sealed class BridgeMcpTool : AIFunction
{
    private static readonly JsonElement _emptySchema =
        JsonDocument.Parse("{}").RootElement;

    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _jsonSchema;
    private readonly McpServerConfig _server;
    private readonly IHarnessRuntime _runtime;
    private readonly HarnessContext _context;
    private readonly string? _nodeId;
    private readonly bool _supportsVisionResult;

    public BridgeMcpTool(
        string name,
        string? description,
        JsonElement jsonSchema,
        McpServerConfig server,
        IHarnessRuntime runtime,
        HarnessContext context,
        string? nodeId = null,
        bool supportsVisionResult = false)
    {
        _name                 = name;
        _description          = description ?? "";
        _jsonSchema           = jsonSchema.ValueKind != JsonValueKind.Undefined ? jsonSchema : _emptySchema;
        _server               = server;
        _runtime              = runtime;
        _context              = context;
        _nodeId               = nodeId;
        _supportsVisionResult = supportsVisionResult;
    }

    public override string Name => _name;
    public override string Description => _description;
    public override JsonElement JsonSchema => _jsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var toolArgs = arguments.ToDictionary(
            kv => kv.Key,
            kv => kv.Value is JsonElement je
                ? je
                : JsonSerializer.SerializeToElement(kv.Value));

        var argsJson = JsonSerializer.Serialize(toolArgs);
        var body = JsonSerializer.Serialize(new
        {
            command       = _server.Command,
            arguments     = _server.Arguments,
            environment   = _server.Environment,
            toolName      = _name,
            toolArguments = toolArgs,
            serverName    = _server.Name,
            sessionId     = _context.SessionId,
            policy        = _server.AllowedPaths?.Length > 0 || _server.BlockedCommands?.Length > 0
                ? new { allowedPaths = _server.AllowedPaths ?? [], blockedCommands = _server.BlockedCommands ?? [] }
                : (object?)null,
        });

        // Start/complete callbacks are handled by the GovernedTool wrapper so that all tools
        // (bridge-backed and in-process) render consistently in the UI.
        var responseJson = await _runtime.BridgePostAsync("http://localhost:5741/tools/call", body, _context, cancellationToken, nodeId: _nodeId);

        // Layer B: a blocked sensitive tool call comes back as a context-approval refusal in one of two
        // transport shapes — the 403 JSON body ({contextApprovalRequired:true, sessionId, error:"…"}) or,
        // depending on which tunnel path carried it, the raw gate string
        // ("[CONTEXT_APPROVAL_REQUIRED sessionId='…'] … (url)"). Both carry the same marker (the JSON body
        // embeds the raw string in its error field), so one check recognizes either. Surface it as a typed
        // exception so the chat drives an in-chat approval ceremony and AUTO-RETRIES the turn — instead of
        // handing the raw refusal to the model as a failed tool result it just gives up on and reports.
        if (responseJson.Contains("CONTEXT_APPROVAL_REQUIRED", StringComparison.Ordinal))
            throw new ContextApprovalRequiredException(
                ExtractApprovalSessionId(responseJson),
                "Context approval required — approve sensitive operations at your node.");

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var text = doc.RootElement.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";

            // A tool that produced an image (e.g. TakeScreenshot) carries it as base64 in the bridge
            // response. Surface it through MultimodalToolResult regardless of vision so the chat UI can
            // always render it to the user; whether the *model* also receives it as an image block is
            // gated separately (IncludeImageForModel) on whether vision was probed as supported —
            // sending raw image bytes to a text-only model would just waste context.
            if (doc.RootElement.TryGetProperty("imageBase64", out var imgEl) &&
                imgEl.GetString() is { Length: > 0 } imageBase64)
            {
                var mediaType = doc.RootElement.TryGetProperty("imageMediaType", out var mtEl)
                    ? mtEl.GetString() ?? "image/jpeg"
                    : "image/jpeg";

                return new MultimodalToolResult
                {
                    Text                 = text,
                    ImageBase64          = imageBase64,
                    ImageMediaType       = mediaType,
                    IncludeImageForModel = _supportsVisionResult,
                };
            }

            if (doc.RootElement.TryGetProperty("metadataJson", out var mdEl) &&
                mdEl.GetString() is { Length: > 0 } metadataJson)
            {
                return new FileMutationToolResult { Text = text, MetadataJson = metadataJson };
            }

            if (doc.RootElement.TryGetProperty("text", out _))
                return text;
        }
        catch { }

        return responseJson;
    }

    // Pull the session id out of a context-approval refusal, whether it arrived as the raw gate string
    // ("… sessionId='<id>' …") or the 403 JSON body ({ "sessionId": "<id>" }). Returns null when absent
    // (a soul-wide grant), which the approval UI handles.
    private static string? ExtractApprovalSessionId(string body)
    {
        const string marker = "sessionId='";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        if (start >= 0)
        {
            start += marker.Length;
            var end = body.IndexOf('\'', start);
            if (end > start) return body[start..end];
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("sessionId", out var sidEl))
                return sidEl.GetString();
        }
        catch { /* raw text without the sessionId=' marker → no session id */ }
        return null;
    }
}

/// <summary>
/// A tool result carrying an image alongside its text. The image is always shown to the user in the
/// chat UI; <see cref="IncludeImageForModel"/> controls whether it is additionally handed to the model
/// as a vision block (only when the active model was probed as vision-capable). Split into the model-
/// facing return value + the UI callback by <c>GovernedTool</c>.
/// </summary>
public sealed class MultimodalToolResult
{
    public required string Text { get; init; }
    public string? ImageBase64 { get; init; }
    public string? ImageMediaType { get; init; }
    public bool IncludeImageForModel { get; init; }
}
