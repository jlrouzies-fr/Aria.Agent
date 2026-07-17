using Aria.Web.Services.Chat;
using Aria.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Aria.Web.Components.Pages;

public partial class Chat
{
    private int?   _pendingGateCogId;
    private string _gateNotes = "";
    private List<(int CogId, int MemberId, string DroneName)> _pendingMemberGates = [];

    private void OnHiveGatePending(int cogId) => _ = InvokeAsync(() =>
    {
        if (cogId == _cogitationId)
        {
            _pendingGateCogId = cogId;
            _gateNotes = "";
            StateHasChanged();
        }
    });

    private void OnHiveGateResolved(int cogId) => _ = InvokeAsync(() =>
    {
        if (_pendingGateCogId == cogId)
        {
            _pendingGateCogId = null;
            _gateNotes = "";
            StateHasChanged();
        }
    });

    private void ApproveGate()
    {
        if (_pendingGateCogId == null) return;
        Orchestrator.ApproveHumanGate(_pendingGateCogId.Value, _gateNotes);
    }

    private void OnHiveMemberGatePending(int cogId, int memberId, string droneName, string? content) => _ = InvokeAsync(() =>
    {
        if (cogId == _cogitationId)
        {
            _pendingMemberGates.Add((cogId, memberId, droneName));
            StateHasChanged();
        }
    });

    private void OnHiveMemberGateResolved(int cogId, int memberId) => _ = InvokeAsync(() =>
    {
        _pendingMemberGates.RemoveAll(g => g.CogId == cogId && g.MemberId == memberId);
        StateHasChanged();
    });

    private void ApproveMemberGate(int cogId, int memberId)
        => Orchestrator.ApproveHiveMemberGate(cogId, memberId);

    // Drives the composer's busy indicator while the Overmind plans/dispatches/synthesises — mirrors
    // the phase-driven animation on the Hive canvas, but for the chat shell.
    private void OnHiveRunStateChanged(int collectiveId) => _ = InvokeAsync(() =>
    {
        if (!_isHiveCogitation || _hiveCollectiveId != collectiveId) return;

        var phase = Orchestrator.GetPhase(collectiveId);
        if (string.IsNullOrEmpty(phase))
        {
            if (!_isStreaming) return;
            _isStreaming    = false;
            _statusOverride = null;
            HideHiveTyping();   // run over — no more messages coming, drop the placeholder for good
        }
        else
        {
            _isStreaming    = true;
            _statusOverride = $"OVERMIND: {phase.ToUpperInvariant()}…";
        }
        StateHasChanged();
    });

    // A transient "typing" bubble carrying the Hive's avatar/name/accent — shown immediately so the
    // avatar doesn't wait for the (possibly slow) LLM call to land before the user sees who's replying.
    private MessageEntry? _hiveTyping;

    private void ShowHiveTyping()
    {
        if (_hiveTyping != null) return;
        _hiveTyping = new MessageEntry("assistant", "")
        {
            SpriteKey   = _historyAvatarKey,
            AccentColor = _historyAccentColor,
            AgentName   = _historyAgentName,
            IsSoul      = false,
        };
        _messages.Add(_hiveTyping);
        _streamingMsg       = _hiveTyping;
        _smartScrollPending = true;
    }

    private void HideHiveTyping()
    {
        if (_hiveTyping == null) return;
        _messages.Remove(_hiveTyping);
        if (_streamingMsg == _hiveTyping) _streamingMsg = null;
        _hiveTyping = null;
    }

    // Fired once (guarded by _hiveGreetingSent) when a fresh, still-empty Hive cogitation is opened.
    // OnHiveCogitationUpdated (below) picks up the persisted greeting and appends it once it lands.
    private async Task SendHiveGreetingAsync(int collectiveId, int cogitationId)
    {
        _isStreaming    = true;
        _statusOverride = "OVERMIND: PRESENTING…";
        ShowHiveTyping();
        StateHasChanged();

        // The greeting doesn't set a run phase, so OnHiveRunStateChanged never fires to clear this —
        // clear it here once the call returns (message already appended via onMessageAdded on success).
        await Orchestrator.SendOvermindGreetingAsync(
            collectiveId, cogitationId, onMessageAdded: SessionState.NotifyHiveCogitationUpdated);

        _isStreaming    = false;
        _statusOverride = null;
        HideHiveTyping();
        StateHasChanged();
    }

    private void OnHiveCogitationUpdated(int cogId) =>
        _ = InvokeAsync(async () =>
        {
            if (cogId != _cogitationId) return;

            // Drop the placeholder before counting/appending — the real persisted message(s) replace it.
            HideHiveTyping();

            var allMsgs    = await CogitationService.GetMessagesAsync(cogId);
            var shownCount = _messages.Count(m => m.Role is "user" or "assistant");
            if (allMsgs.Count <= shownCount)
            {
                if (_isStreaming) ShowHiveTyping();   // more phases still to come
                StateHasChanged();
                return;
            }

            foreach (var m in allMsgs.Skip(shownCount))
            {
                var entry = new MessageEntry(m.Role, m.Content)
                {
                    IsSoul       = m.Role == "user",
                    SpriteKey    = m.Role == "assistant" ? _historyAvatarKey : null,
                    AccentColor  = m.Role == "assistant" ? _historyAccentColor : null,
                    AgentName    = m.Role == "assistant" ? _historyAgentName : null,
                    Timestamp    = m.CreatedAt.ToLocalTime(),
                };
                if (!string.IsNullOrEmpty(m.ThinkingContent))
                {
                    entry.ThinkingContent = m.ThinkingContent;
                    CollapseThinking(entry);   // history thinking starts folded
                }
                _messages.Add(entry);
            }

            // The background run may still have more phases to write (e.g. more drones, then
            // synthesis) — re-show the placeholder for the next one. OnHiveRunStateChanged hides it
            // for good once the run actually ends.
            if (_isStreaming) ShowHiveTyping();

            _smartScrollPending = true;
            StateHasChanged();
            await ScrollToBottomAsync();
        });
}
