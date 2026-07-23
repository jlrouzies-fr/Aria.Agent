using Aria.Harness.Governance;

namespace Aria.Web.Components.Pages;

public partial class Chat
{
    // The tool call currently awaiting the user's authorisation (null when none). Tool calls are
    // sequential within a turn, so a single pending slot + TCS is sufficient — mirrors the HiveGate
    // pause-for-human pattern but stays component-local (same circuit, same streaming call).
    private ActionDescriptor? _pendingApproval;
    private TaskCompletionSource<bool>? _approvalTcs;
    private CancellationTokenSource? _sealCts;

    // The ask_user question currently awaiting the user's answer (null when none) — a variant of
    // the approval pause above, except the resolution is a payload (option label or typed text)
    // rather than a boolean. Same single-slot reasoning; timeout/skip resolve to null.
    private AskUserPrompt? _pendingAskUser;
    private TaskCompletionSource<string?>? _askUserTcs;
    private string _askUserInput = "";

    // Reactive context approval: set when a halted run is waiting for a node-signed session grant.
    private string? _awaitingContextApprovalSessionId;
    private CancellationTokenSource? _contextApprovalCts;
    // Guards against the ceremony being driven twice (auto-start on halt + a manual banner click).
    private bool _contextApprovalInFlight;

    /// <summary>
    /// Passed to the harness as <c>onApprovalRequested</c>. For an in-chat approval it parks until the
    /// user clicks Approve/Deny; for a <see cref="ToolSeverity.NeedsSeal"/> it drives the node-side
    /// Inquisitorial Seal (the terminal cannot grant it — only the node can). Times out / cancellation
    /// both deny.
    /// </summary>
    private async Task<bool> RequestToolApprovalAsync(ActionDescriptor descriptor, CancellationToken ct)
    {
        if (descriptor.Severity == ToolSeverity.NeedsSeal)
            return await RequestSealAsync(descriptor, ct);

        return await RequestInChatApprovalAsync(descriptor, ct);
    }

    private async Task<bool> RequestInChatApprovalAsync(ActionDescriptor descriptor, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await InvokeAsync(() =>
        {
            _pendingApproval = descriptor;
            _approvalTcs     = tcs;
            _smartScrollPending = true;
            StateHasChanged();
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        bool approved;
        try   { approved = await tcs.Task.WaitAsync(linked.Token); }
        catch (OperationCanceledException) { approved = false; }

        await InvokeAsync(() =>
        {
            _pendingApproval = null;
            _approvalTcs     = null;
            StateHasChanged();
        });

        return approved;
    }

    // A gate can be hosted either by this component directly (greeting stream — same circuit, same
    // call) or by an attached CogitationRun (a normal turn, which may have opened the gate while the
    // user was away and reattached since). Route to whichever is actually holding it.
    private void ApproveToolCall()
    {
        if (_attachedRun != null) _attachedRun.ResolveApproval(true);
        else _approvalTcs?.TrySetResult(true);
    }

    private void DenyToolCall()
    {
        if (_attachedRun != null) _attachedRun.ResolveApproval(false);
        else
        {
            _approvalTcs?.TrySetResult(false);
            _sealCts?.Cancel();   // also cancels a pending node Seal wait
        }
    }

    /// <summary>
    /// Passed to the harness as <c>onAskUser</c>. Parks the ask_user tool call until the user
    /// answers (option button or free text), skips, or the 2h window elapses — same pause/resume
    /// machinery as the in-chat approval, except the resolution is the answer payload, not a
    /// boolean. Timeout/skip resolve to null, which the tool reports as "user did not answer —
    /// proceed with your best judgment" rather than failing the run.
    /// </summary>
    private async Task<string?> RequestAskUserAsync(string question, string[]? options, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await InvokeAsync(() =>
        {
            _pendingAskUser     = new AskUserPrompt(question, options);
            _askUserTcs         = tcs;
            _askUserInput       = "";
            _smartScrollPending = true;
            StateHasChanged();
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        string? answer;
        try   { answer = await tcs.Task.WaitAsync(linked.Token); }
        catch (OperationCanceledException) { answer = null; }

        await InvokeAsync(() =>
        {
            _pendingAskUser = null;
            _askUserTcs     = null;
            _askUserInput   = "";
            StateHasChanged();
        });

        return answer;
    }

    // Like the approval gate, the question can be hosted by this component directly or by an
    // attached CogitationRun — route to whichever is holding it.
    private void AnswerAskUser(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return;
        if (_attachedRun != null) _attachedRun.ResolveAskUser(answer);
        else _askUserTcs?.TrySetResult(answer);
    }

    private void SkipAskUser()
    {
        if (_attachedRun != null) _attachedRun.ResolveAskUser(null);
        else _askUserTcs?.TrySetResult(null);
    }

    private void OnAskUserKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key == "Enter") AnswerAskUser(_askUserInput);
    }

    // High-stakes path: the verdict comes from the node, not the terminal. Show a passive "awaiting
    // seal" bar (REFUSE cancels the wait) while SealService drives + verifies the node round-trip.
    private async Task<bool> RequestSealAsync(ActionDescriptor descriptor, CancellationToken ct)
    {
        var sealCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await InvokeAsync(() =>
        {
            _pendingApproval    = descriptor;
            _sealCts            = sealCts;
            _smartScrollPending = true;
            StateHasChanged();
        });

        bool approved;
        var userId = BridgeUserId() ?? SessionState.CurrentUser?.Id ?? "";
        try   { approved = await SealService.RequestSealAsync(userId, descriptor, sealCts.Token); }
        catch (OperationCanceledException) { approved = false; }
        finally
        {
            sealCts.Dispose();
            await InvokeAsync(() =>
            {
                _pendingApproval = null;
                _sealCts         = null;
                StateHasChanged();
            });
        }

        return approved;
    }

    /// <summary>
    /// Called when the user clicks the in-chat "Approve for this session (8h)" banner. Drives the
    /// node-side ceremony, replicates the resulting grant to siblings, and retries the halted turn.
    /// </summary>
    private async Task ApproveContextAsync(string sessionId)
    {
        if (_cogitationId == null || SessionState.CurrentUser == null) return;
        if (_contextApprovalInFlight) return;   // already driving the ceremony (auto-start or prior click)
        _contextApprovalInFlight = true;

        _contextApprovalCts?.Cancel();
        _contextApprovalCts?.Dispose();
        _contextApprovalCts = new CancellationTokenSource();

        var userId = SessionState.CurrentUser.Id;
        var cogId  = _cogitationId.Value;

        try
        {
            var approved = await ContextApproval.RequestApprovalAsync(
                userId, sessionId, cogId, _contextApprovalCts.Token);

            await InvokeAsync(() =>
            {
                // The ceremony polls the node for up to 3 minutes. If the user switched channel or moved to
                // another cogitation meanwhile, this view is no longer showing cogId — bail. The retried run
                // (already started by RequestApprovalAsync) streams and persists to its OWN cogitation in the
                // background; adopting it here would hijack the current view with the old cogitation's answer
                // (the two-answer-blocks + interleave bug). Its own reattach handles it when revisited.
                if (_cogitationId != cogId) return;

                _awaitingContextApprovalSessionId = null;
                if (!approved)
                {
                    _attachError = "// CONTEXT APPROVAL REFUSED OR TIMED OUT — the action was not authorised.";
                    _isStreaming = false;
                    StateHasChanged();
                    return;
                }

                // RetryContextApproval replaced the halted run with a fresh one but reused the same
                // reply message already in _messages. Detach from the old run and continue streaming
                // into that existing message instead of creating a duplicate bubble.
                var newRun = Registry.TryGet(cogId);
                if (newRun != null)
                {
                    DetachFromRun();
                    _streamingMsg       = newRun.Reply;
                    _thinkingTarget     = _streamingMsg;
                    _isStreaming        = true;
                    _statusOverride     = null;
                    _smartScrollPending = true;
                    AttachToRun(newRun);
                }
                else
                {
                    _attachError = "// CONTEXT APPROVED, BUT RETRY FAILED TO START.";
                    _isStreaming = false;
                }
                StateHasChanged();
            });
        }
        catch (OperationCanceledException)
        {
            await InvokeAsync(() =>
            {
                // Cancelled because the user left cogId (channel switch / cogitation change) — don't stamp
                // "cancelled" onto whatever view they're on now.
                if (_cogitationId != cogId) return;
                _awaitingContextApprovalSessionId = null;
                _attachError = "// CONTEXT APPROVAL CANCELLED.";
                _isStreaming = false;
                StateHasChanged();
            });
        }
        finally
        {
            _contextApprovalInFlight = false;
        }
    }
}
