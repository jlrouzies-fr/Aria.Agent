using System.Text.Json;
using Aria.Harness.Core;
using Aria.Shared;
using Aria.Tools;
using Microsoft.Extensions.AI;

namespace Aria.Harness.Bridge;

/// <summary>
/// An AIFunction that calls a tool hosted by Aria.Bridge (terminal tools or stdio MCP servers).
/// </summary>
public sealed class BridgeMcpTool : AIFunction, IDiffPreviewTool
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
            contextWindow = _context.ContextWindow is { Assumed: false } known ? (int?)known.Tokens : null,
            checkpoint    = HarnessContext.CurrentTurnCheckpoint,
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

            // The bridge's own failure flag (ToolCallResponse.IsError). Governance reads it to tell
            // a failed call from a successful one — the text alone can't (bridge errors have no
            // uniform prefix). Surfaced on the wrapper types below; never shown to the model.
            var isError = doc.RootElement.TryGetProperty("isError", out var errEl) &&
                          errEl.ValueKind == JsonValueKind.True;

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
                return new FileMutationToolResult { Text = text, MetadataJson = metadataJson, IsError = isError };
            }

            if (doc.RootElement.TryGetProperty("text", out _))
                return new BridgeToolResult { Text = text, IsError = isError };
        }
        catch { }

        return responseJson;
    }

    /// <summary>
    /// Fetches the prospective unified diff for a file-mutation call from the bridge's read-only
    /// /tools/preview endpoint — used by GovernedTool when the call pauses for approval, so the
    /// human sees what the edit would do instead of a truncated args blob. Short timeout and
    /// fail-open: any problem (old bridge, refusal, slow node) degrades to no diff.
    /// </summary>
    public async Task<string?> FetchDiffPreviewAsync(Dictionary<string, JsonElement> args, CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                command       = _server.Command,
                arguments     = _server.Arguments,
                environment   = _server.Environment,
                toolName      = _name,
                toolArguments = args,
                serverName    = _server.Name,
                sessionId     = _context.SessionId,
                policy        = _server.AllowedPaths?.Length > 0 || _server.BlockedCommands?.Length > 0
                    ? new { allowedPaths = _server.AllowedPaths ?? [], blockedCommands = _server.BlockedCommands ?? [] }
                    : (object?)null,
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            var responseJson = await _runtime.BridgePostAsync("http://localhost:5741/tools/preview", body, _context, timeout.Token, nodeId: _nodeId);

            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True &&
                doc.RootElement.TryGetProperty("diff", out var diffEl) && diffEl.GetString() is { Length: > 0 } diff)
                return diff;
        }
        catch { /* fail-open — the approval card falls back to the plain args preview */ }
        return null;
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
/// A bridge-backed tool that can produce a PROSPECTIVE unified diff for a file-mutation call
/// (bridge POST /tools/preview, read-only). GovernedTool fetches it when such a call pauses for
/// approval; a null return means the preview was unavailable and the approval card falls back to
/// the plain args preview.
/// </summary>
public interface IDiffPreviewTool
{
    Task<string?> FetchDiffPreviewAsync(Dictionary<string, JsonElement> args, CancellationToken ct);
}

/// <summary>
/// A plain-text bridge tool result carrying the bridge's own failure flag
/// (ToolCallResponse.IsError) so the governance layer can tell a failed call from a successful
/// one — the text alone can't (bridge errors have no uniform prefix). <c>GovernedTool</c> unwraps
/// it: the model and the UI see only <see cref="Text"/>, exactly as before.
/// </summary>
public sealed class BridgeToolResult
{
    public required string Text { get; init; }
    public bool IsError { get; init; }
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
