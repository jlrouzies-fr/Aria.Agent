using Aria.Harness.Governance;
using Aria.Web.Services.Chat;
using Aria.Web.Services.Memory;
using Microsoft.Agents.AI;

namespace Aria.Web.Services.Cogitations;

/// <summary>Everything <see cref="CogitationRunRegistry.StartRun"/> needs to run one cogitation turn
/// detached from the Chat component that initiated it.</summary>
public sealed record CogitationRunRequest(
    int                    CogitationId,
    string                 UserId,
    string?                OriginNodeId,
    int?                   SubAgentId,
    string?                AgentSourceName,
    string?                AgentModel,
    AIAgent                Agent,
    AgentSession           Session,
    CogitationStreamRouter Router,
    MessageEntry           Reply,
    string                 AiMessage,
    string                 UserText,
    IReadOnlyList<string>  TurnScopePaths,
    GovernanceMode         GovernanceMode,
    bool                   MemoryToolEnabled,
    AutoMemoryMode         AutoMemoryMode,
    int                    AutoMemoryInterval,
    string?                SessionId = null,
    bool                   IsContextRetry = false,
    int?                   BudgetToolCalls = null,
    int?                   BudgetFileReads = null);
