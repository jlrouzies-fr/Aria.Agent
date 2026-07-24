using Aria.Web.Services.Chat;

namespace Aria.Web.Components.Pages;

/// <summary>
/// The "/scope" chat command (Wave 5): bare shows the effective filesystem scope (declared projects
/// plus this session's live node-approved expansions); "add &lt;path&gt;" runs the node approval
/// ceremony for a time-boxed, node-signed session path grant; "remove &lt;path&gt;" revokes one.
/// The server only ever RELAYS the ask — the grant is minted and stored by the node, never by us.
/// Runs locally — never reaches the agent.
/// </summary>
public partial class Chat
{
    private async Task HandleScopeCommandAsync(string args)
    {
        var cmd = ScopeCommand.Parse(args);

        switch (cmd.Kind)
        {
            case ScopeCommandKind.Add:
            {
                _messages.Add(new MessageEntry("system",
                    $"// SCOPE: asking your node to authorise '{cmd.Path}' for this session (8h) — approve on the node's page to proceed. //"));
                await InvokeAsync(StateHasChanged);

                var userId  = SessionState.CurrentUser?.Id.ToString();
                var granted = userId != null
                    && await ContextApproval.RequestPathGrantAsync(userId, SessionState.SessionToken, cmd.Path!);
                if (granted)
                {
                    if (!SessionState.SessionScopeExpansions.Contains(cmd.Path!))
                        SessionState.SessionScopeExpansions.Add(cmd.Path!);
                    _messages.Add(new MessageEntry("system",
                        $"// SCOPE GRANTED: '{cmd.Path}' — node-signed session expansion, valid 8h. The agent may now work there. //"));
                }
                else
                {
                    _messages.Add(new MessageEntry("system",
                        $"// SCOPE REFUSED OR UNREACHABLE: '{cmd.Path}' was not added — approve at your node (or check it is connected) and try again. //"));
                }
                break;
            }

            case ScopeCommandKind.Remove:
            {
                var userId = SessionState.CurrentUser?.Id.ToString();
                var ok = userId != null
                    && await ContextApproval.RevokePathGrantAsync(userId, SessionState.SessionToken, cmd.Path!);
                SessionState.SessionScopeExpansions.Remove(cmd.Path!);
                _messages.Add(new MessageEntry("system", ok
                    ? $"// SCOPE REVOKED: '{cmd.Path}' — this session's expansion was removed at the node. //"
                    : $"// SCOPE: could not reach your node to revoke '{cmd.Path}' — removed from the local copy; any node-side grant lapses on its own. //"));
                break;
            }

            case ScopeCommandKind.Invalid:
                _messages.Add(new MessageEntry("system", $"// SCOPE: {cmd.Error} //"));
                break;

            default: // Status
                _messages.Add(new MessageEntry("system", await DescribeScopeAsync()));
                break;
        }

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Renders the effective scope: declared projects (from the node's own config, mirrored
    /// server-side) plus this session's live expansions (read back from the node, so the display and
    /// the governance scope-lock's soft copy both track the node's truth).</summary>
    private async Task<string> DescribeScopeAsync()
    {
        var userId = SessionState.CurrentUser?.Id.ToString();
        IReadOnlyList<string> expansions = userId != null
            ? await ContextApproval.GetLivePathExpansionsAsync(userId, SessionState.SessionToken)
            : [];

        SessionState.SessionScopeExpansions.Clear();
        SessionState.SessionScopeExpansions.AddRange(expansions);

        var sb = new System.Text.StringBuilder();
        sb.Append("// SCOPE — declared projects:");
        if (SessionState.Projects.Count == 0)
            sb.Append(" none");
        foreach (var p in SessionState.Projects)
            sb.Append($"\n   • {p.Name} — {p.Path}{(SessionState.ActiveProject?.Path == p.Path ? "  (ACTIVE)" : "")}");

        sb.Append("\n session expansions (node-signed, time-boxed):");
        if (expansions.Count == 0)
            sb.Append(" none");
        foreach (var e in expansions)
            sb.Append($"\n   • {e}");

        sb.Append("\n /scope add <path> asks your node for a time-boxed expansion · /scope remove <path> revokes one. //");
        return sb.ToString();
    }
}
