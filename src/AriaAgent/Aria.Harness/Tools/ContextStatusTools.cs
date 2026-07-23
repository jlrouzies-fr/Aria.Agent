using Aria.Harness.Context;
using Microsoft.Extensions.AI;

namespace Aria.Harness.Tools;

/// <summary>
/// The <c>context_status</c> tool. Registered by the Harness only when the host wires a
/// <see cref="ContextStatusProvider"/> — it lets the agent see its own context pressure
/// (last reported usage, transcript estimate, auto-compact headroom) so it can decide to
/// wrap up, summarise, or compact before hitting the wall. Benign in every governance mode
/// including Plan and Off: not mutating, not a file read.
/// </summary>
public static class ContextStatusTools
{
    public static AITool Create(Func<ContextStatusSnapshot> snapshotProvider) =>
        AIFunctionFactory.Create(
            () => ContextStatusReport.Build(snapshotProvider()),
            name: "context_status",
            description:
                "Report your current context pressure: the last input-token count the model source "
                + "reported, an estimate of the transcript size, the auto-compaction threshold, and how "
                + "much headroom remains before it. Call it on long sessions to decide whether to wrap "
                + "up, summarise findings so far, or ask the user to /compact before continuing.");
}
