namespace Aria.Web.Components.Pages;

public partial class Hive
{
    // Clicking "COGITATE" opens the normal Chat UI on a fresh cogitation bound to this collective —
    // no separate prompt modal. The user's first message there kicks off the Overmind's
    // plan/dispatch/synthesise pipeline (see Chat.Messaging.razor.cs: StartHiveOrchestrationAsync).
    public async Task StartHiveChatAsync()
    {
        if (_collective == null || _startingHiveChat) return;
        _startingHiveChat = true;
        StateHasChanged();
        try
        {
            var cogId = await Orchestrator.CreateHiveCogitationAsync(_collective.Id);
            if (cogId.HasValue)
                Nav.NavigateTo($"/chat/{cogId.Value}");
        }
        finally
        {
            _startingHiveChat = false;
            StateHasChanged();
        }
    }
}
