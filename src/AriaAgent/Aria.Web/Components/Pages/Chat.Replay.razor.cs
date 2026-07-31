using Aria.Web.Services.Chat;
using Aria.Web.Services.Cogitations;

namespace Aria.Web.Components.Pages;

/// <summary>
/// Edit-and-replay of a previously sent user prompt: arms the composer with the old text, then on
/// send truncates the transcript from that turn onward (UI + persistence), resets the live agent
/// thread, and lets the normal <c>SendAsync</c> path append the edited turn.
/// </summary>
public partial class Chat
{
    // Index into _messages of the user turn being edited. Null when not in edit-and-replay mode.
    private int? _replayFromIndex;

    private bool CanEditPrompt(int index) =>
        !_isStreaming
        && !_rebuilding
        && !_cogitationOffline
        && !_isHiveCogitation
        && _agent != null
        && _cogitationId.HasValue
        && index >= 0
        && index < _messages.Count
        && _messages[index].Role == "user";

    /// <summary>How many messages after the armed edit index would be discarded on send.</summary>
    private int ReplayDiscardCount =>
        _replayFromIndex is int i && i < _messages.Count
            ? _messages.Count - i
            : 0;

    private bool ReplayHasAttachmentNote =>
        _replayFromIndex is int i
        && i < _messages.Count
        && _messages[i].Content.StartsWith("[Attached:", StringComparison.Ordinal);

    private void BeginEditPrompt(int index)
    {
        if (!CanEditPrompt(index)) return;

        _replayFromIndex = index;
        _input           = _messages[index].Content;
        _queuedMessages.Clear();
        ClearAttachment();
        StateHasChanged();
        _ = FocusInputAsync();
    }

    private void CancelEditPrompt()
    {
        if (_replayFromIndex == null) return;
        _replayFromIndex = null;
        _input           = "";
        StateHasChanged();
    }

    private void ClearReplayState()
    {
        _replayFromIndex = null;
    }

    /// <summary>
    /// When edit-and-replay is armed, truncate the UI transcript and persisted messages to the
    /// prefix before the edited turn, then reset the agent thread so the upcoming send reinjects
    /// history. Returns false if the prepare step failed (send must abort).
    /// </summary>
    private async Task<bool> PrepareReplayBeforeSendAsync()
    {
        if (_replayFromIndex is not int index) return true;
        if (!CanEditPrompt(index))
        {
            _attachError = "// REPLAY: cannot edit this turn right now.";
            ClearReplayState();
            StateHasChanged();
            return false;
        }

        if (!_cogitationId.HasValue || _agent == null)
        {
            ClearReplayState();
            return false;
        }

        var kept = _messages.Take(index).ToList();
        // Persist only roles the storage layer owns. Skip UI-only system notes and the cosmetic
        // greeting (assistant bubble before any user turn, never written to the cogitation).
        var payload = kept
            .Select((m, i) => (m, i))
            .Where(t => IsPersistedTranscriptEntry(t.m, kept, t.i))
            .Select(t => ToTranscriptWrite(t.m))
            .ToList();

        var cogId = _cogitationId.Value;
        bool ok;
        if (_cogitationOriginNodeId != null)
        {
            var uid = BridgeUserId();
            if (uid == null)
            {
                _attachError = "// REPLAY: bridge unavailable — cannot rewrite transcript.";
                StateHasChanged();
                return false;
            }

            ok = await BridgeCogitation.ReplaceMessagesAsync(
                uid, cogId, payload, _cogitationOriginNodeId);
            if (ok) _ = CogitationService.TouchAsync(cogId);
        }
        else
        {
            await CogitationService.ReplaceMessagesAsync(cogId, payload);
            ok = true;
        }

        if (!ok)
        {
            _attachError = "// REPLAY FAULT: could not rewrite transcript on your node.";
            StateHasChanged();
            return false;
        }

        _messages.Clear();
        _messages.AddRange(kept);
        ClearReplayState();
        await ResetAgentThreadAfterTranscriptChangeAsync();
        return true;
    }

    private static bool IsPersistedTranscriptEntry(
        MessageEntry m, IReadOnlyList<MessageEntry> prefix, int indexInPrefix)
    {
        if (m.Role is not ("user" or "assistant" or "screenshot")) return false;
        if (m.IsCompactSummary) return true;
        if (m.DbMessageId != null || m.BridgeMessageId != null) return true;
        if (m.Role == "user") return true;
        // In-session assistant/screenshot without a backing id: keep only when a user turn
        // precedes it (excludes the throwaway greeting streamed before the first directive).
        return prefix.Take(indexInPrefix).Any(x => x.Role == "user");
    }

    private static TranscriptMessageWrite ToTranscriptWrite(MessageEntry m)
    {
        string? sectionsJson = null;
        var needsSections = m.Sections.Any(s =>
            s.Type is MessageSection.SectionType.ToolActivity
                or MessageSection.SectionType.Thinking
                or MessageSection.SectionType.TodoList)
            || m.Sections.Count(s => s.Type == MessageSection.SectionType.Content) > 1;

        if (needsSections && m.Sections.Count > 0)
            sectionsJson = System.Text.Json.JsonSerializer.Serialize(m.Sections, SectionJsonOptions);

        return new TranscriptMessageWrite(
            m.Role,
            m.Content,
            string.IsNullOrEmpty(m.ThinkingContent) ? null : m.ThinkingContent,
            sectionsJson,
            m.ImageBase64,
            m.ImageMediaType);
    }
}
