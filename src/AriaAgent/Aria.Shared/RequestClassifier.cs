using System.Text.Json;

namespace Aria.Shared;

/// <summary>How much local authority a server-relayed bridge request exercises (Layer B, §4).</summary>
public enum RequestSensitivity
{
    /// <summary>Read-only or control-plane; runs without a context grant (health, key presence, sync,
    /// seal, metrics, memory/cogitation reads, oauth token relay, …).</summary>
    Benign,

    /// <summary>Exercises real local authority the hosted server should not be able to drive on its
    /// own — provider-key spend, shell, filesystem writes, arbitrary tool execution. Requires a valid
    /// node-approved context grant before the bridge will run it.</summary>
    Sensitive,
}

/// <summary>
/// Classifies a server→bridge <c>HandleLocalRest</c> request by the local authority it exercises,
/// purely from method + path (no body). Shared so the bridge (enforcement) and the server/UI
/// (explanations) agree on the taxonomy. Deliberately conservative: only paths that exercise real
/// local authority are Sensitive — shell/exec, provider spend, tool execution, and the project
/// file/git surface (a server-driven read of the user's declared projects is exfiltration); the
/// rest of the control plane stays Benign so enabling enforcement can't silently wedge it.
/// Widen (e.g. body-aware <c>/tools/call</c> read-vs-write, key mutations) as needed.
/// </summary>
public static class RequestClassifier
{
    // Prefixes that drive real local authority. Matched case-insensitively against the path with any
    // query string stripped.
    private static readonly string[] SensitivePrefixes =
    [
        "/llm/proxy",       // injects a provider key and spends on the model — cost + exfiltration risk
        "/tools/call",      // executes an MCP or built-in tool (shell / file-write live here)
        "/terminal/exec",   // direct shell execution
        // Project file/git surface (Explorer panel, "#" picker, file viewer). Reads AND writes: the
        // AllowedPaths policy scopes WHERE, but a compromised server must not be able to browse and
        // exfiltrate the user's declared projects without a human-approved session grant — the same
        // grant the chat agent's sensitive ops already require (whichever surface asks first).
        "/project-files",
        "/project-git",
    ];

    // Built-in tools that only READ (local, no mutation, no spend). Everything else routed through
    // /tools/call — write/exec built-ins AND every MCP tool (unknown capability) — is Sensitive.
    private static readonly HashSet<string> ReadOnlyBuiltinTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "list_dir", "glob", "grep", "commands_index", "GetCurrentDateTime",
        "git_status", "git_diff", "git_log", "system_info",
        "process_list", "process_output", "read_image", "wait_for", "project_info",
    };

    public static RequestSensitivity Classify(string? method, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return RequestSensitivity.Benign;

        var q = path.IndexOf('?');
        var clean = (q >= 0 ? path[..q] : path).TrimEnd('/');
        if (clean.Length == 0) clean = "/";

        foreach (var prefix in SensitivePrefixes)
            if (clean.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return RequestSensitivity.Sensitive;

        return RequestSensitivity.Benign;
    }

    /// <summary>
    /// Body-aware classification. Identical to the path-only overload except for <c>/tools/call</c>,
    /// where the tool being invoked decides: a read-only built-in is Benign (so reads don't prompt),
    /// while a write/exec built-in or ANY MCP tool is Sensitive. Fail-safe — an unparseable or
    /// unrecognised body is treated as Sensitive.
    /// </summary>
    public static RequestSensitivity Classify(string? method, string? path, string? body)
    {
        if (string.IsNullOrWhiteSpace(path)) return RequestSensitivity.Benign;

        var q = path.IndexOf('?');
        var clean = (q >= 0 ? path[..q] : path).TrimEnd('/');

        if (clean.Equals("/tools/call", StringComparison.OrdinalIgnoreCase))
            return ClassifyToolCall(body);

        return Classify(method, path);
    }

    private static RequestSensitivity ClassifyToolCall(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return RequestSensitivity.Sensitive; // fail-safe
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // An MCP tool (ServerName present) has unknown capability → Sensitive.
            if (TryGetString(root, "serverName", "ServerName") is { Length: > 0 })
                return RequestSensitivity.Sensitive;

            var tool = TryGetString(root, "toolName", "ToolName");
            if (string.IsNullOrEmpty(tool)) return RequestSensitivity.Sensitive; // can't tell → fail-safe
            return ReadOnlyBuiltinTools.Contains(tool)
                ? RequestSensitivity.Benign
                : RequestSensitivity.Sensitive;
        }
        catch
        {
            return RequestSensitivity.Sensitive; // malformed → fail-safe
        }
    }

    // Case-tolerant lookup: the server may serialise with either camelCase or PascalCase names.
    private static string? TryGetString(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    public static bool IsSensitive(string? method, string? path) =>
        Classify(method, path) == RequestSensitivity.Sensitive;

    public static bool IsSensitive(string? method, string? path, string? body) =>
        Classify(method, path, body) == RequestSensitivity.Sensitive;
}
