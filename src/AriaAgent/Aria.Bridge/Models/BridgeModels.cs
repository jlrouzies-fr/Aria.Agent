using System.Text.Json;

namespace Aria.Bridge;

// Sent by Aria.Web → POST /tools/list to discover what tools a local stdio MCP server exposes.
public record ToolsListRequest(
    string Command,
    string[] Arguments,
    Dictionary<string, string>? Environment,
    string? ServerName = null,
    SecurityPolicy? Policy = null);

// Sent by Aria.Web → POST /tools/call to invoke a specific tool on a local stdio MCP server.
// ToolArguments values are pre-serialised JsonElements so the bridge can forward them as-is.
public record ToolsCallRequest(
    string Command,
    string[] Arguments,
    Dictionary<string, string>? Environment,
    string ToolName,
    Dictionary<string, JsonElement>? ToolArguments,
    string? ServerName = null,
    SecurityPolicy? Policy = null);

// Returned by /tools/list — mirrors the SDK's McpClientTool fields needed by Aria.Web.
public record BridgeToolInfo(
    string Name,
    string? Description,
    JsonElement JsonSchema);   // raw JSON Schema object from the MCP server's tool definition

// Returned by /tools/call — all TextContentBlock texts are concatenated into a single string.
// ImageBase64/ImageMediaType are set only by tools that capture an image (e.g. TakeScreenshot).
// MetadataJson is an optional UI-only payload (e.g. diff cards) that the model never sees.
public record ToolCallResponse(
    string Text,
    bool IsError,
    string? ImageBase64 = null,
    string? ImageMediaType = null,
    string? MetadataJson = null);
