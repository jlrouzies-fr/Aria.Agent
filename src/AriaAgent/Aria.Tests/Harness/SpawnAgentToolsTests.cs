using Aria.Harness.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Aria.Tests.HarnessCore;

/// <summary>
/// Covers the agent-callable delegation tools (<see cref="SpawnAgentTools"/>): names the model sees,
/// argument validation, wait-time clamping, and that invocation reaches the host's spawner.
/// </summary>
public class SpawnAgentToolsTests
{
    private sealed class FakeSpawner : ISubAgentSpawner
    {
        public Func<string, string, Task<SpawnAgentHandle>>? OnSpawn;
        public Func<string, int, Task<SpawnAgentPoll>>?     OnPoll;

        public Task<SpawnAgentHandle> SpawnAsync(string personaName, string task, CancellationToken ct)
            => OnSpawn?.Invoke(personaName, task)
               ?? Task.FromResult(new SpawnAgentHandle(true, "h-1", "spawned, handle h-1"));

        public Task<SpawnAgentPoll> PollAsync(string handle, int waitSeconds, CancellationToken ct)
            => OnPoll?.Invoke(handle, waitSeconds)
               ?? Task.FromResult(new SpawnAgentPoll(SpawnAgentPoll.Done, "final report"));
    }

    private static Task<object?> Invoke(AITool tool, IDictionary<string, object?> args) =>
        ((AIFunction)tool).InvokeAsync(new AIFunctionArguments(args)).AsTask();

    [Fact]
    public void Tools_ExposeExpectedNames()
    {
        var spawner = new FakeSpawner();
        Assert.Equal("spawn_agent",  SpawnAgentTools.CreateSpawnTool(spawner).Name);
        Assert.Equal("agent_result", SpawnAgentTools.CreateResultTool(spawner).Name);
    }

    [Fact]
    public async Task SpawnAgent_PassesPersonaAndTask_ReturnsHandleMessage()
    {
        string? gotPersona = null, gotTask = null;
        var spawner = new FakeSpawner
        {
            OnSpawn = (p, t) =>
            {
                gotPersona = p; gotTask = t;
                return Task.FromResult(new SpawnAgentHandle(true, "abc123", "Sub-agent 'Scout' spawned. Handle: abc123"));
            },
        };

        var result = await Invoke(SpawnAgentTools.CreateSpawnTool(spawner),
            new Dictionary<string, object?> { ["persona"] = " Scout ", ["task"] = "audit the repo" });

        Assert.Equal("Scout", gotPersona);          // trimmed before hitting the host
        Assert.Equal("audit the repo", gotTask);
        Assert.Contains("abc123", result?.ToString());
    }

    [Theory]
    [InlineData("",   "task")]
    [InlineData("  ", "task")]
    [InlineData("Scout", "")]
    public async Task SpawnAgent_RequiresPersonaAndTask(string persona, string task)
    {
        var called = false;
        var spawner = new FakeSpawner { OnSpawn = (_, _) => { called = true; return Task.FromResult(new SpawnAgentHandle(true, "x", "x")); } };

        var result = await Invoke(SpawnAgentTools.CreateSpawnTool(spawner),
            new Dictionary<string, object?> { ["persona"] = persona, ["task"] = task });

        Assert.False(called);
        Assert.NotNull(result?.ToString());
    }

    [Theory]
    [InlineData(9999, 120)]   // clamped to the cap
    [InlineData(-5,  0)]      // never negative
    [InlineData(30,  30)]     // in-range untouched
    public async Task AgentResult_ClampsWaitSeconds(int requested, int expected)
    {
        var gotWait = -1;
        var spawner = new FakeSpawner
        {
            OnPoll = (_, w) => { gotWait = w; return Task.FromResult(new SpawnAgentPoll(SpawnAgentPoll.Running, "still running")); },
        };

        await Invoke(SpawnAgentTools.CreateResultTool(spawner),
            new Dictionary<string, object?> { ["handle"] = "abc123", ["wait_seconds"] = requested });

        Assert.Equal(expected, gotWait);
    }

    [Fact]
    public async Task AgentResult_ReturnsPollMessage()
    {
        var spawner = new FakeSpawner
        {
            OnPoll = (h, _) => Task.FromResult(new SpawnAgentPoll(SpawnAgentPoll.Done, $"report for {h}")),
        };

        var result = await Invoke(SpawnAgentTools.CreateResultTool(spawner),
            new Dictionary<string, object?> { ["handle"] = "abc123", ["wait_seconds"] = 0 });

        Assert.Contains("report for abc123", result?.ToString());
    }

    [Fact]
    public async Task CreateSession_WithSpawner_Succeeds()
    {
        // Registration smoke test: a Harness session built with a spawner wired must not fault
        // (the tools themselves are covered above; the agent's tool list is not publicly inspectable).
        var runtime = new Fakes.FakeHarnessRuntime();
        runtime.AddSource(new Aria.Agent.ModelSource
        {
            Name = "OpenAI", Url = "https://api.openai.com/v1",
            IsPublicProvider = true, Models = ["gpt-4o"],
        });

        var harness = new Aria.Harness.Core.Harness(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Aria.Harness.Core.Harness>.Instance, runtime);
        var options = new Aria.Harness.Core.HarnessOptions
        {
            SelectedSourceName = "OpenAI",
            SelectedModel      = "gpt-4o",
            SubAgentSpawner    = new FakeSpawner(),
        };

        var (agent, session) = await harness.CreateSessionAsync(options, Aria.Harness.Core.HarnessContext.Empty);
        Assert.NotNull(agent);
        Assert.NotNull(session);
    }
}
