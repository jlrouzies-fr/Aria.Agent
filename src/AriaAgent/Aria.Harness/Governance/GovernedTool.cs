using System.Text.Encodings.Web;
using System.Text.Json;
using Aria.Harness.Bridge;
using Aria.Shared;
using Microsoft.Extensions.AI;

namespace Aria.Harness.Governance;

/// <summary>
/// Decorates an <see cref="AIFunction"/> with governance: every tool call is classified before it
/// runs and may be blocked (synthetic refusal the model self-corrects on), gated behind an in-chat
/// approval, or escalated to a node-signed Seal. The model sees an identical tool — name, description
/// and schema all delegate to the inner function — so the governance layer is invisible to it until a
/// call is actually refused or paused.
/// </summary>
public sealed class GovernedTool : AIFunction
{
    private readonly AIFunction _inner;
    private readonly GovernanceContext _ctx;
    private readonly Func<ActionDescriptor, CancellationToken, Task<bool>>? _onApproval;
    private readonly Action<string, string>? _onToolStart;
    private readonly Action<string, string, string?, string?, string?>? _onToolComplete;

    // Relaxed encoder so the args preview shows readable text (apostrophes, <, >, &) instead of
    // '-style escapes in the approval bar.
    private static readonly JsonSerializerOptions DisplayJson =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public GovernedTool(
        AIFunction inner,
        GovernanceContext ctx,
        Func<ActionDescriptor, CancellationToken, Task<bool>>? onApproval,
        Action<string, string>? onToolStart,
        Action<string, string, string?, string?, string?>? onToolComplete)
    {
        _inner          = inner;
        _ctx            = ctx;
        _onApproval     = onApproval;
        _onToolStart    = onToolStart;
        _onToolComplete = onToolComplete;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var dict = arguments.ToDictionary(
            kv => kv.Key,
            kv => kv.Value is JsonElement je ? je : JsonSerializer.SerializeToElement(kv.Value));
        var argsJson = JsonSerializer.Serialize(dict, DisplayJson);
        var preview  = Preview(argsJson);

        var verdict = ToolClassifier.Classify(_ctx, _inner.Name, dict, preview);

        // Fleet routing gate: the merged multi-node dispatcher resolved this call to a bridge
        // OTHER than the session's default node. With the user's fleet-approval mode on, the
        // routing decision itself needs a human sign-off — the agent chose that machine from
        // fleet_status assumptions the user may want to veto.
        if (verdict.Severity == ToolSeverity.Allowed &&
            _ctx.Policy.Mode != GovernanceMode.Off &&
            _ctx.Policy.ApproveCrossNodeCalls &&
            _inner is PathRoutedTerminalTool routed &&
            routed.ResolveTargetNodeId(dict) is { } targetNode &&
            !string.Equals(targetNode, routed.DefaultNodeId, StringComparison.Ordinal))
        {
            verdict = new ActionDescriptor(_inner.Name, preview,
                $"fleet routing — this call will execute on a different bridge ({routed.DescribeNode(targetNode)}) " +
                "than this session's default node; approve the cross-machine decision",
                null, ToolSeverity.NeedsApproval);
        }

        switch (verdict.Severity)
        {
            case ToolSeverity.Blocked:
            {
                // Surface as a refused tool block; return a synthetic result so the model adapts
                // instead of the call throwing and tearing down the circuit.
                var refusal = $"REFUSED BY GOVERNANCE: {verdict.Reason}. This action was not " +
                              "explicitly requested by the user. Do not retry it — stop and ask the user first.";
                _onToolStart?.Invoke(_inner.Name, argsJson);
                _onToolComplete?.Invoke(_inner.Name, refusal, null, null, null);
                return refusal;
            }

            case ToolSeverity.NeedsApproval:
            case ToolSeverity.NeedsSeal:
            {
                if (_onApproval != null)
                {
                    // Prospective diff: a paused file mutation can show the human exactly what it
                    // would change — fetched read-only from the bridge (short timeout inside the
                    // tool). Fail-open: a preview problem must never stall the approval pause.
                    // Seal-gated ops and non-file mutations keep the args blob.
                    if (verdict.Severity == ToolSeverity.NeedsApproval &&
                        ToolCategories.IsDiffPreviewable(_inner.Name) &&
                        _inner is IDiffPreviewTool previewable)
                    {
                        try
                        {
                            if (await previewable.FetchDiffPreviewAsync(dict, cancellationToken) is { } diff)
                                verdict = verdict with { Diff = diff };
                        }
                        catch { /* no diff — the approval card falls back to the args preview */ }
                    }

                    var approved = await _onApproval(verdict, cancellationToken);
                    if (!approved)
                    {
                        var denied = $"DENIED BY THE USER: {verdict.Reason}. The action was not performed. " +
                                     "Acknowledge this and ask how to proceed.";
                        _onToolStart?.Invoke(_inner.Name, argsJson);
                        _onToolComplete?.Invoke(_inner.Name, denied, null, null, null);
                        return denied;
                    }
                }
                break;
            }
        }

        // Allowed (or approved): run the real tool and fire start/complete callbacks so the UI
        // renders a tool block for in-process tools as well as bridge-backed ones.
        _onToolStart?.Invoke(_inner.Name, argsJson);
        try
        {
            var result = await _inner.InvokeAsync(arguments, cancellationToken);

            string resultText;
            string? imageBase64 = null;
            string? imageMediaType = null;
            string? metadataJson = null;

            // A file mutation returns structured UI metadata alongside the model-facing text.
            if (result is FileMutationToolResult fm)
            {
                resultText  = fm.Text;
                metadataJson = fm.MetadataJson;
                resultText  = ApplyVerifyNudge(resultText, dict, succeeded: !fm.IsError);
                _onToolComplete?.Invoke(_inner.Name, resultText, null, null, metadataJson);
                return resultText;
            }

            // A tool that produced an image (e.g. TakeScreenshot) returns a MultimodalToolResult. The
            // image always goes to the UI callback so the user sees it; it is only re-attached to the
            // model-facing return value as a vision block when the tool flagged the active model as
            // vision-capable — a text-only model must not receive raw image bytes.
            if (result is MultimodalToolResult mm)
            {
                resultText     = mm.Text;
                imageBase64    = mm.ImageBase64;
                imageMediaType = mm.ImageMediaType;
                _onToolComplete?.Invoke(_inner.Name, resultText, imageBase64, imageMediaType, null);

                if (mm.IncludeImageForModel && imageBase64 is { Length: > 0 })
                    return new List<AIContent>
                    {
                        new TextContent(mm.Text),
                        new DataContent(Convert.FromBase64String(imageBase64), imageMediaType ?? "image/jpeg"),
                    };
                return mm.Text;
            }

            // Any other bridge tool result arrives wrapped with the bridge's own failure flag;
            // unwrap it so the model and the UI see the same plain text as before.
            if (result is BridgeToolResult br)
            {
                resultText = ApplyVerifyNudge(br.Text, dict, succeeded: !br.IsError);
                _onToolComplete?.Invoke(_inner.Name, resultText, null, null, null);
                return resultText;
            }

            resultText = result as string ?? JsonSerializer.Serialize(result, DisplayJson);
            resultText = ApplyVerifyNudge(resultText, dict, succeeded: true);
            _onToolComplete?.Invoke(_inner.Name, resultText, null, null, null);
            return result is string ? resultText : result;
        }
        catch (Exception ex) when (TryGetApprovalSessionId(ex, out var sessionId))
        {
            // Layer B: the node gate refused this sensitive call — no live 8h seal for this session.
            // We must NOT rethrow: Microsoft.Extensions.AI's FunctionInvokingChatClient catches tool
            // exceptions and feeds them back to the model as an error result, which it just retries.
            // Instead stash the signal and terminate the function-calling loop cleanly; the Harness
            // re-raises it as ContextApprovalRequiredException after the stream (above the framework),
            // where the in-chat approval ceremony halts the turn and auto-retries it once approved.
            _ctx.FlagContextApproval(sessionId);
            if (FunctionInvokingChatClient.CurrentContext is { } fic) fic.Terminate = true;
            // Wording pairs with CogitationRunRegistry.ContextRetryNudge: the retry turn opens with
            // "[NODE SEAL GRANTED] … retry it immediately", which is exactly the confirmation promised
            // here. A blunter "do not retry it now" (the old text) stuck in the session history and
            // made the model refuse to retry even AFTER approval was granted.
            const string paused = "PAUSED - this action needs a one-time node seal, and an approval " +
                "request has been opened on the user's node. Do not retry it in this turn. When the " +
                "user grants the seal you will receive a [NODE SEAL GRANTED] confirmation message - " +
                "retry the action at that point. If you see this refusal AGAIN after a seal was " +
                "granted, the executing node is not honoring the session grant: retrying the same " +
                "call is pointless - stop and tell the user the target node refused the approved " +
                "grant (cross-node trust or grant replication is broken).";
            _onToolComplete?.Invoke(_inner.Name, paused, null, null, null);
            return paused;
        }
        catch (Exception ex)
        {
            _onToolComplete?.Invoke(_inner.Name, $"ERROR: {ex.Message}", null, null, null);
            throw;
        }
    }

    // ── Post-mutation verify nudge ────────────────────────────────────────────────────────────
    // After the agent edits files, nothing prompts it to check its work — the turn just continues
    // on faith. So while a turn accumulates successful file mutations with no build/test run, the
    // mutation's OWN result earns a one-line reminder (at 1, then every 5). Advisory only: never
    // blocks, never fails a call, never counts against budgets. The nudge rides the same result
    // text the bridge's diff feedback appends to — both are plain suffixes and compose.
    private string ApplyVerifyNudge(string resultText, Dictionary<string, JsonElement> args, bool succeeded)
    {
        // Verification marks apply to every completed call, nudge or not: a PASSED run_tests (any
        // kind — test/build/lint runs share the structured header) or a bash_exec build/test
        // command silences the nudge for the rest of the turn.
        if (_inner.Name == "run_tests" && IsPassedTestRun(resultText))
            _ctx.MarkVerificationRan();
        else if (_inner.Name == "bash_exec" &&
                 args.TryGetValue("command", out var cmd) &&
                 ToolCategories.IsVerificationCommand(cmd.ValueKind == JsonValueKind.String ? cmd.GetString() : null))
            _ctx.MarkVerificationRan();

        if (!succeeded || !ToolCategories.IsFileMutation(_inner.Name))
            return resultText;

        // The counter tracks every successful mutation even with the toggle off — only the nudge
        // append is gated. Thresholds: 1, then every 5 (1, 6, 11, …).
        var n = _ctx.RecordMutation();
        if (!_ctx.Policy.VerifyNudge || _ctx.VerificationRan || (n != 1 && (n - 1) % 5 != 0))
            return resultText;

        return resultText + $"\n\n◈ {n} file(s) mutated this turn, no build/test run yet — " +
            "consider verifying (run_tests, or project_info to infer the command).";
    }

    // The bridge's run_tests renders a structured header ("◈ TEST RUN [cmd] — PASSED (exit 0, …)")
    // for every completed run; a timed-out suite converts to a background job and returns a JSON
    // note instead, so only a run that actually finished AND passed matches here.
    private static bool IsPassedTestRun(string text) =>
        text.StartsWith("◈ TEST RUN", StringComparison.Ordinal) &&
        text.Contains("— PASSED", StringComparison.Ordinal);

    // The Layer B seal refusal reaches a tool in one of two shapes: a typed
    // ContextApprovalRequiredException (the /tools/call LocalRest path) OR — on the LLM/BridgeRequest
    // transport — a PLAIN Exception whose message is the raw gate string, because the request channel
    // completes with `new Exception(error)` (ModelBridgeRegistry.Routing). Recognise both by the marker
    // and pull the session id out of "…sessionId='<id>'…" when present.
    private const string ApprovalMarker = "CONTEXT_APPROVAL_REQUIRED";

    private static bool TryGetApprovalSessionId(Exception ex, out string? sessionId)
    {
        if (ex is ContextApprovalRequiredException cap) { sessionId = cap.SessionId; return true; }

        sessionId = null;
        var msg = ex.Message;
        if (string.IsNullOrEmpty(msg) || !msg.Contains(ApprovalMarker, StringComparison.Ordinal))
            return false;

        const string tag = "sessionId='";
        var start = msg.IndexOf(tag, StringComparison.Ordinal);
        if (start >= 0)
        {
            start += tag.Length;
            var end = msg.IndexOf('\'', start);
            if (end > start) sessionId = msg[start..end];
        }
        return true;
    }

    private static string Preview(string argsJson) =>
        argsJson.Length > 160 ? argsJson[..160] + "…" : argsJson;
}
