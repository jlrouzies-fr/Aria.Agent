using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aria.Agent;

public partial class UniversalSSEStream : Stream
{
    // ── JSON rewriters ────────────────────────────────────────────────────────

    private static string RewriteContent(string json, string newContent)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node?["choices"]?[0]?["delta"] is JsonObject d) d["content"] = newContent;
            return node?.ToJsonString() ?? json;
        }
        catch { return json; }
    }

    private static string? RewriteFinishReason(string json, string newReason)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root?["choices"]?[0] is JsonObject c) c["finish_reason"] = newReason;
            return root?.ToJsonString();
        }
        catch { return null; }
    }

    private static string Truncate(string s, int max = 80) =>
        s.Length <= max ? s : s[..max] + $"…(+{s.Length - max})";
}
