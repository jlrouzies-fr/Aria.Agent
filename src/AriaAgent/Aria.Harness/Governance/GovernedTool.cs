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

            resultText = result as string ?? JsonSerializer.Serialize(result, DisplayJson);
            _onToolComplete?.Invoke(_inner.Name, resultText, null, null, null);
            return result;
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
                "retry the action at that point.";
            _onToolComplete?.Invoke(_inner.Name, paused, null, null, null);
            return paused;
        }
        catch (Exception ex)
        {
            _onToolComplete?.Invoke(_inner.Name, $"ERROR: {ex.Message}", null, null, null);
            throw;
        }
    }

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
