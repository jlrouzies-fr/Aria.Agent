using System.Text.Json;
using Aria.Tools;
using Microsoft.Extensions.AI;

namespace Aria.Web.Services.ModelBridge;

/// <summary>
/// An AIFunction that calls a local stdio MCP server via the Aria.Bridge app,
/// tunneled through the WASM browser bridge.
/// </summary>
public sealed class BridgeToolFunction : AIFunction
{
    private static readonly JsonElement _emptySchema =
        JsonDocument.Parse("{}").RootElement;

    private readonly string     _name;
    private readonly string     _description;
    private readonly JsonElement _jsonSchema;
    private readonly McpServerConfig _server;
    private readonly Func<string, string, Task<string>> _postAsync;

    public BridgeToolFunction(
        string name,
        string? description,
        JsonElement jsonSchema,
        McpServerConfig server,
        Func<string, string, Task<string>> postAsync)
    {
        _name        = name;
        _description = description ?? "";
        _jsonSchema  = jsonSchema.ValueKind != JsonValueKind.Undefined ? jsonSchema : _emptySchema;
        _server      = server;
        _postAsync   = postAsync;
    }

    public override string   Name        => _name;
    public override string   Description => _description;
    public override JsonElement JsonSchema  => _jsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var toolArgs = arguments.ToDictionary(
            kv => kv.Key,
            kv => kv.Value is JsonElement je
                ? je
                : JsonSerializer.SerializeToElement(kv.Value));

        var body = JsonSerializer.Serialize(new
        {
            command       = _server.Command,
            arguments     = _server.Arguments,
            environment   = _server.Environment,
            toolName      = _name,
            toolArguments = toolArgs,
            serverName    = _server.Name,
            policy        = _server.AllowedPaths?.Length > 0 || _server.BlockedCommands?.Length > 0
                ? new { allowedPaths = _server.AllowedPaths ?? [], blockedCommands = _server.BlockedCommands ?? [] }
                : (object?)null,
        });

        var responseJson = await _postAsync("http://localhost:5741/tools/call", body);

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("text", out var textEl))
                return textEl.GetString() ?? "";
        }
        catch { }

        return responseJson;
    }
}
