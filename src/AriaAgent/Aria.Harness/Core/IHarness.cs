using Aria.Agent;
using Aria.Harness.Formats;
using Aria.Harness.Governance;
using Microsoft.Agents.AI;
using OpenAI.Chat;

namespace Aria.Harness.Core;

/// <summary>
/// The agent harness: builds chat clients, assembles tools, and runs agent sessions.
/// </summary>
public interface IHarness
{
    /// <summary>
    /// Create a new agent session for the given options and host runtime.
    /// </summary>
    Task<(AIAgent Agent, AgentSession Session)> CreateSessionAsync(
        HarnessOptions options,
        HarnessContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Stream a single user message through an existing agent session.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(
        string userMessage,
        AIAgent agent,
        AgentSession session,
        HarnessContext context,
        IReadOnlyList<string>? turnScopePaths = null,
        GovernancePolicy? turnPolicy = null,
        CancellationToken ct = default,
        Action<ChatTokenUsage>? onUsage = null);

    /// <summary>
    /// Detect the tool-call format used by the selected source/model.
    /// </summary>
    Task<ToolCallFormat> DetectToolCallFormatAsync(
        string? sourceName,
        string? modelId,
        HarnessContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Detect the thinking/reasoning format used by the selected source/model.
    /// </summary>
    Task<ThinkingFormat> DetectThinkingFormatAsync(
        string? sourceName,
        string? modelId,
        HarnessContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Force a fresh format detection and update the cache.
    /// </summary>
    Task<(ThinkingFormat Thinking, ToolCallFormat ToolCall)> ForceRedetectAsync(
        string sourceName,
        string modelId,
        HarnessContext context,
        CancellationToken ct = default);
}
