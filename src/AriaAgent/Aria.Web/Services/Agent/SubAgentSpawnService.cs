using System.Collections.Concurrent;
using Aria.Harness.Governance;
using Aria.Harness.Tools;
using Aria.Web.Data;

namespace Aria.Web.Services.Agent;

/// <summary>
/// Backs the agent-callable <c>spawn_agent</c>/<c>agent_result</c> tools: starts headless runs of
/// sub-agent personas (via the existing background-execution machinery) and hands the parent agent
/// a poll handle. One instance per application (the handle registry is shared); each interactive
/// chat session gets a session-bound <see cref="ISubAgentSpawner"/> via <see cref="ForSession"/>.
///
/// Limits: delegation is one level deep (headless child runs are never given a spawner, and
/// <see cref="ForSession"/> refuses depth ≥ 1), and at most <see cref="MaxConcurrentChildren"/>
/// children run concurrently per chat session. Per-turn spawn volume is governed implicitly —
/// every spawn/poll is a tool call counted by the session's governance budget.
/// </summary>
public sealed class SubAgentSpawnService(
    IHeadlessAgentRunner runner,
    IServiceScopeFactory scopeFactory,
    ILogger<SubAgentSpawnService> logger)
{
    /// <summary>Max simultaneously-running spawned children per chat session.</summary>
    public const int MaxConcurrentChildren = 4;

    /// <summary>Delegation depth cap: a chat agent (depth 0) may spawn; its children may not.</summary>
    public const int MaxDepth = 1;

    // Completed runs stay pollable for a while (the parent may fetch the same result twice),
    // then get reaped on the next spawn so the registry can't grow unbounded.
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromMinutes(30);

    private sealed class ChildRun
    {
        public required string       SessionKey  { get; init; }
        public required string       PersonaName { get; init; }
        public required Task<string> Completion  { get; init; }
        public DateTime?             CompletedAtUtc { get; set; }
    }

    private readonly ConcurrentDictionary<string, ChildRun> _children = new();

    /// <summary>
    /// Builds the session-bound spawner the Harness registers <c>spawn_agent</c>/<c>agent_result</c>
    /// for. Returns null when delegation is not allowed — a spawned child (depth ≥ 1) gets no
    /// spawner, so the tools are simply absent from its session (the depth-limit mechanism).
    /// </summary>
    public ISubAgentSpawner? ForSession(string? userId, string? sessionId, GovernanceMode governanceMode, int depth = 0)
    {
        if (depth >= MaxDepth || string.IsNullOrEmpty(userId))
            return null;
        return new SessionSpawner(this, userId, sessionId, governanceMode);
    }

    private async Task<SpawnAgentHandle> SpawnCoreAsync(
        string userId, string? sessionId, GovernanceMode mode, string personaName, string task)
    {
        SweepCompleted();
        var sessionKey = $"{userId}|{sessionId ?? ""}";

        var running = _children.Values.Count(c => c.SessionKey == sessionKey && !c.Completion.IsCompleted);
        if (running >= MaxConcurrentChildren)
            return new SpawnAgentHandle(false, null,
                $"Refused — {MaxConcurrentChildren} sub-agents are already running for this session. " +
                "Collect a result with agent_result before spawning more.");

        SubAgent? persona;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var subAgents = scope.ServiceProvider.GetRequiredService<SubAgentService>();
            persona = await subAgents.FindByNameAsync(userId, personaName);
        }
        if (persona == null)
            return new SpawnAgentHandle(false, null,
                $"No sub-agent persona named '{personaName}'. Check the name, or create the persona first.");

        var handle = Guid.NewGuid().ToString("N");

        // Fire-and-collect: the run is NOT awaited here — spawn_agent returns at once and the parent
        // polls with agent_result. The child runs under the parent's session id, so its sensitive
        // bridge calls resolve to the same {soul}|{sessionId} Layer B context grant the chat session
        // already holds; it inherits the parent's governance mode with fresh per-session counters, and
        // keeps the persona's configured bridge/terminal tools (interactive-session children only).
        // Deliberately not tied to the tool call's cancellation — the child outlives the spawn call
        // (the executor's own 30-minute headless cap still applies).
        var completion = runner.SpawnChildRunAsync(
            userId, persona.Id, task, sessionId,
            allowBridgeTools: true, governanceMode: mode, ct: CancellationToken.None);

        _children[handle] = new ChildRun
        {
            SessionKey  = sessionKey,
            PersonaName = persona.DisplayName,
            Completion  = completion,
        };

        logger.LogInformation("Spawned sub-agent '{Persona}' (handle {Handle}) for user {User}",
            persona.DisplayName, handle[..8], userId);
        return new SpawnAgentHandle(true, handle,
            $"Sub-agent '{persona.DisplayName}' spawned and running in the background. Handle: {handle}\n" +
            "Collect its report with agent_result when you need it — you can keep working in the meantime.");
    }

    private async Task<SpawnAgentPoll> PollCoreAsync(string handle, int waitSeconds, CancellationToken ct)
    {
        if (!_children.TryGetValue(handle, out var child))
            return new SpawnAgentPoll(SpawnAgentPoll.Unknown,
                $"Unknown handle '{handle}' — it was not spawned by this session, or its result was already collected and reaped.");

        if (!child.Completion.IsCompleted && waitSeconds > 0)
            await Task.WhenAny(child.Completion, Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct));

        if (!child.Completion.IsCompleted)
            return new SpawnAgentPoll(SpawnAgentPoll.Running,
                $"Sub-agent '{child.PersonaName}' is still running (handle {handle}).");

        child.CompletedAtUtc ??= DateTime.UtcNow;

        if (child.Completion.IsCanceled)
            return new SpawnAgentPoll(SpawnAgentPoll.Failed,
                $"Sub-agent '{child.PersonaName}' was cancelled before finishing.");
        if (child.Completion.IsFaulted)
        {
            var err = child.Completion.Exception?.GetBaseException().Message ?? "unknown error";
            return new SpawnAgentPoll(SpawnAgentPoll.Failed,
                $"Sub-agent '{child.PersonaName}' failed: {err}");
        }

        return new SpawnAgentPoll(SpawnAgentPoll.Done,
            $"Sub-agent '{child.PersonaName}' finished. Final report:\n\n{child.Completion.Result}");
    }

    // Reap completed runs past the retention window so a long-lived server can't accumulate handles.
    private void SweepCompleted()
    {
        var cutoff = DateTime.UtcNow - CompletedRetention;
        foreach (var (handle, child) in _children)
            if (child.Completion.IsCompleted && (child.CompletedAtUtc ?? DateTime.UtcNow) < cutoff)
                _children.TryRemove(handle, out _);
    }

    /// <summary>The session-bound view handed to the Harness: everything below is already scoped to
    /// the owning user, the chat session's grant context, and its governance mode.</summary>
    private sealed class SessionSpawner(
        SubAgentSpawnService owner, string userId, string? sessionId, GovernanceMode mode) : ISubAgentSpawner
    {
        public Task<SpawnAgentHandle> SpawnAsync(string personaName, string task, CancellationToken ct)
            => owner.SpawnCoreAsync(userId, sessionId, mode, personaName, task);

        public Task<SpawnAgentPoll> PollAsync(string handle, int waitSeconds, CancellationToken ct)
            => owner.PollCoreAsync(handle, waitSeconds, ct);
    }
}
