using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Aria.Harness.Tools;

/// <summary>Outcome of a <c>spawn_agent</c> request: on success the <paramref name="Handle"/> identifies
/// the child run for later <c>agent_result</c> polls; <paramref name="Message"/> is the model-facing text
/// either way (confirmation with handle, or the refusal reason).</summary>
public sealed record SpawnAgentHandle(bool Ok, string? Handle, string Message);

/// <summary>Outcome of an <c>agent_result</c> poll. <paramref name="Status"/> is one of
/// <see cref="Running"/>, <see cref="Done"/>, <see cref="Failed"/>, <see cref="Unknown"/>;
/// <paramref name="Message"/> is the model-facing text (the child's final answer when done).</summary>
public sealed record SpawnAgentPoll(string Status, string Message)
{
    public const string Running = "running";
    public const string Done    = "done";
    public const string Failed  = "failed";
    public const string Unknown = "unknown";
}

/// <summary>
/// Host-provided bridge to sub-agent delegation machinery (Web: runs the persona headlessly via
/// the background executor). Session-bound: the host wires one per interactive chat session,
/// already scoped to the user, the session's Layer B grant context, and its governance mode.
/// Headless child runs never get a spawner, so delegation depth is capped at one level.
/// </summary>
public interface ISubAgentSpawner
{
    /// <summary>Starts a headless run of the named persona on <paramref name="task"/> and returns
    /// immediately with a poll handle (or a refusal — unknown persona, concurrency cap, depth limit).</summary>
    Task<SpawnAgentHandle> SpawnAsync(string personaName, string task, CancellationToken ct);

    /// <summary>Polls a spawned child, waiting up to <paramref name="waitSeconds"/> for completion.</summary>
    Task<SpawnAgentPoll> PollAsync(string handle, int waitSeconds, CancellationToken ct);
}

/// <summary>
/// The sub-agent delegation tools. Registered by the Harness only when the host wires an
/// <see cref="ISubAgentSpawner"/> (interactive Web chat sessions). Execution is in-process
/// coordination — the actual child run happens in the host's background executor.
/// </summary>
public static class SpawnAgentTools
{
    /// <summary>Upper bound on how long one <c>agent_result</c> call blocks the parent turn.</summary>
    public const int MaxWaitSeconds = 120;

    public static AITool CreateSpawnTool(ISubAgentSpawner spawner) =>
        AIFunctionFactory.Create(
            async ([Description("The name of an existing sub-agent persona to run the task.")] string persona,
                   [Description("The complete, self-contained task brief for the sub-agent — it sees none of this conversation, so include all needed context.")] string task,
                   CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(persona))
                    return "No persona named — spawn_agent requires the name of an existing sub-agent.";
                if (string.IsNullOrWhiteSpace(task))
                    return "No task given — spawn_agent requires a self-contained task brief.";
                var r = await spawner.SpawnAsync(persona.Trim(), task, ct);
                return r.Message;
            },
            name: "spawn_agent",
            description:
                "Delegate a self-contained subtask to one of your sub-agent personas, which runs it headlessly "
                + "in the background (with its own configured tools, under this session's governance mode and "
                + "context grant) and returns immediately with a handle. Use it to parallelise independent "
                + "workstreams — research, audits, focused edits — then collect the outcome with agent_result. "
                + "Do NOT use it for trivial steps you can do yourself, and do not spawn an agent to re-do "
                + "what another spawned agent is already doing. Spawned agents cannot themselves spawn agents.");

    public static AITool CreateResultTool(ISubAgentSpawner spawner) =>
        AIFunctionFactory.Create(
            async ([Description("The handle returned by spawn_agent.")] string handle,
                   [Description("Seconds to wait for the child to finish before answering (0–120). Use 0 for a non-blocking status check.")] int wait_seconds,
                   CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(handle))
                    return "No handle given — agent_result requires the handle returned by spawn_agent.";
                var wait = Math.Clamp(wait_seconds, 0, MaxWaitSeconds);
                var r = await spawner.PollAsync(handle.Trim(), wait, ct);
                return r.Message;
            },
            name: "agent_result",
            description:
                "Check on a spawned sub-agent. Returns its status — running, done (with its final report), "
                + "or failed — waiting up to wait_seconds for completion. Poll with wait_seconds=0 while you "
                + "continue other work, then with a longer wait when the result is all that remains.");
}
