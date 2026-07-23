using System.Text;
using System.Text.Json;

namespace Aria.Harness.Governance;

/// <summary>
/// Pure decision logic: given an attempted tool call and the session's <see cref="GovernanceContext"/>,
/// returns an <see cref="ActionDescriptor"/> with the governance verdict. Records the attempt against
/// the context's per-turn counters (so budget and loop checks include the current call).
/// </summary>
public static class ToolClassifier
{
    public static ActionDescriptor Classify(
        GovernanceContext ctx,
        string name,
        IReadOnlyDictionary<string, JsonElement> args,
        string argsPreview)
    {
        var p = ctx.Policy;

        var argsKey = BuildKey(args);
        var repeats = ctx.RecordAttempt(name, argsKey);

        if (p.Mode == GovernanceMode.Off)
            return Allow(name, argsPreview);

        // Budget — total tool calls, plus a tighter cap on file reads (the sprawl problem).
        if (ctx.ToolCallsThisTurn > p.MaxToolCallsPerTurn)
            return Block(name, argsPreview,
                $"per-turn tool budget exceeded ({p.MaxToolCallsPerTurn} calls)");
        if (ToolCategories.IsFileRead(name) && ctx.FileReadsThisTurn > p.MaxFileReadsPerTurn)
            return Block(name, argsPreview,
                $"per-turn file-read budget exceeded ({p.MaxFileReadsPerTurn} reads)");

        // Loop — same call repeated within the turn.
        if (repeats >= p.LoopThreshold)
            return Block(name, argsPreview, "repeated identical call (possible loop)");

        var path = ExtractPath(args);

        // Scope lock — path outside the turn's allowed roots (only when a scope is defined). The hint
        // points at the "/scope add" node-approval flow — the sanctioned way to widen scope (Wave 5).
        if (path != null && ctx.AllowedScope.Count > 0 && !InScope(path, ctx.AllowedScope)
            && p.Scope != ScopeEnforcement.Off)
        {
            var hint = $"path outside allowed scope: {path} — the user can authorise this directory for the session with /scope add {path}";
            if (p.Scope == ScopeEnforcement.Block)
                return Block(name, argsPreview, hint, path);

            var scopeSeverity = ToolCategories.IsHighStakes(name) && p.SealHighStakes
                ? ToolSeverity.NeedsSeal
                : ToolSeverity.NeedsApproval;
            return new ActionDescriptor(name, argsPreview, hint, path, scopeSeverity);
        }

        // Mutations — escalate per policy.
        if (ToolCategories.IsMutating(name))
        {
            if (p.BlockMutations)
                return Block(name, argsPreview,
                    "mutations are disabled in Plan mode — present the plan to the user and ask them " +
                    "to switch out of Plan mode (e.g. /governance coding) to execute it", path);
            if (ToolCategories.IsHighStakes(name) && p.SealHighStakes)
                return new ActionDescriptor(name, argsPreview, "high-stakes action", path, ToolSeverity.NeedsSeal);
            if (p.ApproveMutations)
                return new ActionDescriptor(name, argsPreview, "mutating action", path, ToolSeverity.NeedsApproval);
        }

        return Allow(name, argsPreview);
    }

    private static ActionDescriptor Allow(string name, string preview) =>
        new(name, preview, "", null, ToolSeverity.Allowed);

    private static ActionDescriptor Block(string name, string preview, string reason, string? path = null) =>
        new(name, preview, reason, path, ToolSeverity.Blocked);

    // Stable key over the argument set for loop detection.
    private static string BuildKey(IReadOnlyDictionary<string, JsonElement> args)
    {
        var sb = new StringBuilder();
        foreach (var kv in args.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('=').Append(kv.Value.GetRawText()).Append('&');
        return sb.ToString();
    }

    private static string? ExtractPath(IReadOnlyDictionary<string, JsonElement> args)
    {
        foreach (var key in new[] { "path", "file_path", "directory" })
            if (args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private static bool InScope(string path, IReadOnlyList<string> scope)
    {
        string full;
        try { full = Path.GetFullPath(Expand(path)); }
        catch { return true; } // unparseable — don't block on a path we can't reason about

        foreach (var root in scope)
        {
            string r;
            try { r = Path.GetFullPath(Expand(root)).TrimEnd(Path.DirectorySeparatorChar); }
            catch { continue; }
            if (string.Equals(full, r, StringComparison.Ordinal)) return true;
            if (full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string Expand(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/") || path.StartsWith("~\\"))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }
}
