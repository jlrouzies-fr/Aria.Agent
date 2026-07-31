using System.Text.Json;
using Aria.Harness.Bridge;
using Aria.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aria.Harness.Core;

public sealed partial class Harness
{
    // ── Bridge tool loading ───────────────────────────────────────────────────

    private async Task<IList<AITool>> LoadBridgeToolsAsync(
        McpServerConfig server,
        HarnessContext context,
        string? nodeId = null)
    {
        var listBody = JsonSerializer.Serialize(new
        {
            command     = server.Command,
            arguments   = server.Arguments,
            environment = server.Environment,
            serverName  = server.Name,
            policy      = server.AllowedPaths?.Length > 0 || server.BlockedCommands?.Length > 0
                ? new { allowedPaths = server.AllowedPaths ?? [], blockedCommands = server.BlockedCommands ?? [] }
                : (object?)null,
        });

        string responseJson;
        try
        {
            responseJson = await _runtime.BridgePostAsync("http://localhost:5741/tools/list", listBody, context, context.CancellationToken, nodeId: nodeId);
        }
        catch (Exception ex)
        {
            _logger.LogInformation("LocalBridge: tool list unavailable for '{Server}': {Message}", server.Name, ex.Message);
            return [];
        }

        if (!responseJson.TrimStart().StartsWith('['))
        {
            _logger.LogWarning("LocalBridge: tool list error for '{Server}': {Response}", server.Name, responseJson);
            return [];
        }

        try
        {
            var toolInfos = JsonSerializer.Deserialize<List<BridgeToolInfo>>(responseJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (toolInfos == null) return [];

            return toolInfos
                .Select(t => (AITool)new BridgeMcpTool(
                    t.Name, t.Description, t.JsonSchema, server, _runtime, context, nodeId))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalBridge: failed to parse tool list for '{Server}'", server.Name);
            return [];
        }
    }
}
