using System.Runtime.CompilerServices;
using Aria.Harness.Context;
using Aria.Harness.Governance;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Aria.Harness.Core;

public sealed partial class Harness
{
    public async IAsyncEnumerable<string> StreamAsync(
        string userMessage,
        AIAgent agent,
        AgentSession session,
        HarnessContext context,
        IReadOnlyList<string>? turnScopePaths = null,
        GovernancePolicy? turnPolicy = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        Action<ChatTokenUsage>? onUsage = null)
    {
        var message = new UserChatMessage(userMessage);
        _reasoningHandlers.TryGetValue(agent, out var handler);

        // Reset per-turn governance budgets/loop history, set this turn's allowed scope, and refresh
        // the policy so a live mode change (from the Tools panel) applies without rebuilding the session.
        _governanceContexts.TryGetValue(agent, out var govCtx);
        govCtx?.BeginTurn(turnScopePaths, turnPolicy);

        await foreach (var update in agent.RunStreamingAsync([message], session, cancellationToken: ct).WithCancellation(ct))
        {
            if (update.ContentUpdate.Count > 0 && !string.IsNullOrEmpty(update.ContentUpdate[0].Text))
                yield return update.ContentUpdate[0].Text;
            if (update.Usage != null)
                onUsage?.Invoke(update.Usage);
        }

        // Layer B seal pause: a sensitive tool hit the node gate with no live grant and terminated the
        // function-calling loop (it couldn't throw — the framework would swallow it into a retry). Re-raise
        // here, above the function-invocation layer, so the turn halts and the approval ceremony runs; on
        // approval the whole turn is retried and the tool succeeds under the fresh 8h grant.
        if (govCtx?.ContextApprovalPending == true)
            throw new Aria.Shared.ContextApprovalRequiredException(
                govCtx.ContextApprovalSessionId,
                "Context approval required — approve sensitive operations at your node.");

        // Some thinking models (e.g. Qwen3 StartsInThinkMode) stop inside their think block on
        // tool-continuation turns, producing only internal monologue. The SSE layer discards that
        // monologue so it cannot poison history. Nudge the model once for a proper final answer.
        if (handler?.LastStreamHadUnresolvedThinking == true)
        {
            _logger.LogInformation("Stream ended with unresolved thinking; re-prompting for final answer");
            var nudge = new UserChatMessage("Provide your final answer to the user now.");
            await foreach (var update in agent.RunStreamingAsync([nudge], session, cancellationToken: ct).WithCancellation(ct))
            {
                if (update.ContentUpdate.Count > 0 && !string.IsNullOrEmpty(update.ContentUpdate[0].Text))
                    yield return update.ContentUpdate[0].Text;
                if (update.Usage != null)
                    onUsage?.Invoke(update.Usage);
            }
        }
    }
}
