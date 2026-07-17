using System.Text.Json;

namespace Aria.Harness.Bridge;

public sealed record BridgeToolInfo(string Name, string? Description, JsonElement JsonSchema);
