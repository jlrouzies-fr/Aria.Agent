using System.Text.Json;

namespace Aria.Bridge.Services.Noosphere;

// Extracts a human-readable message from an OpenAI-style error body ({"error":{"message":"..."}} or
// {"error":"..."}), falling back to a truncated raw body when the shape doesn't match. Mirrors
// UniversalReasoningHandler's upstream-error parsing (Aria.Agent isn't referenced from Aria.Bridge) —
// this is what lets a removed/renamed model's real server response (e.g. LM Studio's "model not
// found") reach the Memory tool's failure notes instead of a generic "something went wrong".
internal static class NoosphereHttpError
{
    public static string ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString() ?? Truncate(body);
                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString() ?? Truncate(body);
            }
        }
        catch { /* not JSON — fall through to the raw head */ }
        return Truncate(body);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
