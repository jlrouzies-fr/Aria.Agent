using Aria.Harness.Governance;

namespace Aria.Web.Services.Agent;

/// <summary>
/// Narrow seam over <see cref="AgentBackgroundExecutor"/> used by <see cref="SubAgentSpawnService"/>
/// to start a delegated child run. Keeps the spawn service unit-testable without standing up the
/// whole background-execution stack.
/// </summary>
public interface IHeadlessAgentRunner
{
    /// <summary>Starts (and awaits) a headless run of the given sub-agent persona. The caller does NOT
    /// await this to completion inline — it stores the task and polls it. The run executes under
    /// <paramref name="sessionId"/> (the parent chat session's token, so the child's sensitive bridge
    /// calls ride the session's existing Layer B context grant) and inherits the parent's governance
    /// mode with fresh per-session counters/budgets. <paramref name="allowBridgeTools"/> controls
    /// whether bridge/terminal tools survive the headless filter for this run.</summary>
    Task<string> SpawnChildRunAsync(
        string userId,
        int subAgentId,
        string prompt,
        string? sessionId,
        bool allowBridgeTools,
        GovernanceMode governanceMode,
        CancellationToken ct = default);
}
