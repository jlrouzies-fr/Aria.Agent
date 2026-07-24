using Aria.Harness.Governance;

namespace Aria.Web.Components.Pages;

/// <summary>
/// The "/governance" chat command: bare shows the active mode + effective budgets, a mode name
/// switches mode (persisted, like the Tools panel chips), and "budget tools=N reads=N" sets
/// per-session overrides ("budget reset" clears them). Runs locally — never reaches the agent.
/// </summary>
public partial class Chat
{
    private async Task HandleGovernanceCommandAsync(string args)
    {
        var cmd = GovernanceCommand.Parse(args);
        string note;

        switch (cmd.Kind)
        {
            case GovernanceCommandKind.SwitchMode:
                SessionState.Governance = cmd.Mode!.Value;
                if (SessionState.CurrentUser != null)
                    await ToolService.SaveGovernanceModeAsync(SessionState.CurrentUser.Id, cmd.Mode.Value);
                note = GovernanceCommand.Describe(SessionState.EffectiveGovernancePolicy(), SessionState.HasGovernanceBudgetOverrides);
                break;

            case GovernanceCommandKind.SetBudget:
                if (cmd.Tools != null) SessionState.GovernanceBudgetToolCalls = cmd.Tools;
                if (cmd.Reads != null) SessionState.GovernanceBudgetFileReads = cmd.Reads;
                note = GovernanceCommand.Describe(SessionState.EffectiveGovernancePolicy(), SessionState.HasGovernanceBudgetOverrides);
                break;

            case GovernanceCommandKind.ResetBudget:
                SessionState.GovernanceBudgetToolCalls = null;
                SessionState.GovernanceBudgetFileReads = null;
                note = GovernanceCommand.Describe(SessionState.EffectiveGovernancePolicy(), hasOverrides: false);
                break;

            case GovernanceCommandKind.Invalid:
                note = $"// GOVERNANCE: {cmd.Error} //";
                break;

            default: // Status
                note = GovernanceCommand.Describe(SessionState.EffectiveGovernancePolicy(), SessionState.HasGovernanceBudgetOverrides);
                break;
        }

        _messages.Add(new MessageEntry("system", note));
        await InvokeAsync(StateHasChanged);
    }
}
