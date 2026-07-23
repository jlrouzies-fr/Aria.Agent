namespace Aria.Web.Services.Chat;

/// <summary>What a parsed "/scope" command asks for.</summary>
public enum ScopeCommandKind { Status, Add, Remove, Invalid }

/// <summary>
/// Parsed form of the "/scope" chat command (Wave 5). Pure parsing — no IO — so it is unit-testable
/// like <c>Aria.Harness.Governance.GovernanceCommand</c>. The path is the rest of the line after the
/// verb, verbatim (paths may contain spaces); the node normalises and validates it at grant time.
/// </summary>
public readonly record struct ScopeCommand(ScopeCommandKind Kind, string? Path, string? Error)
{
    public static ScopeCommand Parse(string? args)
    {
        var rest = (args ?? "").Trim();
        if (rest.Length == 0) return new(ScopeCommandKind.Status, null, null);

        var sp   = rest.IndexOf(' ');
        var verb = sp < 0 ? rest : rest[..sp];
        var path = sp < 0 ? "" : rest[(sp + 1)..].Trim();

        if (verb.Equals("add", StringComparison.OrdinalIgnoreCase))
            return path.Length > 0
                ? new(ScopeCommandKind.Add, path, null)
                : new(ScopeCommandKind.Invalid, null, "usage: /scope add <path>");

        if (verb.Equals("remove", StringComparison.OrdinalIgnoreCase))
            return path.Length > 0
                ? new(ScopeCommandKind.Remove, path, null)
                : new(ScopeCommandKind.Invalid, null, "usage: /scope remove <path>");

        return new(ScopeCommandKind.Invalid, null,
            $"unknown sub-command '{verb}' — use /scope, /scope add <path>, or /scope remove <path>");
    }
}
