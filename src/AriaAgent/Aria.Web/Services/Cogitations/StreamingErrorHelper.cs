namespace Aria.Web.Services.Cogitations;

/// <summary>Shared between the Chat component (greeting stream) and CogitationRunRegistry (turn runs).</summary>
public static class StreamingErrorHelper
{
    // A cancellation can reach us either as OperationCanceledException or wrapped by the SDK /
    // HttpClient as a generic exception whose message is "The operation was canceled."
    public static bool IsCancellation(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is OperationCanceledException) return true;
            var m = e.Message;
            if (m.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                || m.Contains("aborted", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static string FriendlyError(string raw, string? sourceName)
    {
        if (raw.Contains("Failed to fetch", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("TypeError", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("NetworkError", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase))
        {
            var src = string.IsNullOrEmpty(sourceName) ? "the local LLM" : $"\"{sourceName}\"";
            return $"Cannot reach {src} — is the model server running?";
        }
        return raw;
    }
}
