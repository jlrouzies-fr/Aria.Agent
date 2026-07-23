using Aria.Harness.Governance;
using Aria.Harness.Tools;
using Aria.Web.Data;
using Aria.Web.Data.Context;
using Aria.Web.Data.Agents;
using Aria.Web.Data.Users;
using Aria.Web.Services.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aria.Tests.Web;

/// <summary>
/// Covers the spawn/handle lifecycle of <see cref="SubAgentSpawnService"/>: spawn returns a handle,
/// polling completes/fails/runs, unknown handles, the one-level depth limit, the per-session
/// concurrency cap, and that the child run inherits the parent session's grant context, governance
/// mode, and (interactive-only) bridge tools.
/// </summary>
public class SubAgentSpawnServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ServiceProvider _provider;
    private readonly FakeRunner _runner = new();
    private readonly SubAgentSpawnService _svc;

    private const string UserId = "soul-1";
    private const string SessionId = "chat-session-token";

    public SubAgentSpawnServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-spawn-tests-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddScoped<SubAgentService>();
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var db = dbFactory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Set<User>().Add(new User { Id = UserId, Name = "Test Soul" });
        db.SaveChanges();
        SeedPersona(db, generatedName: "Scout", nickname: null);
        SeedPersona(db, generatedName: "LONG-FORMAL-NAME", nickname: "Doc");
        db.SaveChanges();

        _svc = new SubAgentSpawnService(
            _runner,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SubAgentSpawnService>.Instance);
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static void SeedPersona(AppDbContext db, string generatedName, string? nickname)
    {
        db.SubAgents.Add(new SubAgent
        {
            UserId = UserId, GeneratedName = generatedName, Nickname = nickname,
            ArchetypeName = "test", GeneratedPersonality = "test",
        });
    }

    private sealed class FakeRunner : IHeadlessAgentRunner
    {
        public Func<Task<string>> Next = () => Task.FromResult("REPORT");
        public List<(string UserId, int SubAgentId, string Prompt, string? SessionId, bool AllowBridgeTools, GovernanceMode Mode)> Calls = [];

        public Task<string> SpawnChildRunAsync(
            string userId, int subAgentId, string prompt, string? sessionId,
            bool allowBridgeTools, GovernanceMode governanceMode, CancellationToken ct = default)
        {
            Calls.Add((userId, subAgentId, prompt, sessionId, allowBridgeTools, governanceMode));
            return Next();
        }
    }

    private ISubAgentSpawner NewSessionSpawner(GovernanceMode mode = GovernanceMode.Coding, int depth = 0) =>
        _svc.ForSession(UserId, SessionId, mode, depth)!;

    // ── Depth limit ─────────────────────────────────────────────────────────

    [Fact]
    public void DepthZero_GetsSpawner_DepthOne_GetsNone()
    {
        Assert.NotNull(_svc.ForSession(UserId, SessionId, GovernanceMode.Coding, depth: 0));
        // The depth-limit mechanism: a spawned child gets no spawner, so spawn_agent/agent_result
        // are simply absent from its session — it can never fan out further.
        Assert.Null(_svc.ForSession(UserId, SessionId, GovernanceMode.Coding, depth: 1));
        Assert.Null(_svc.ForSession(UserId, SessionId, GovernanceMode.Coding, depth: 2));
    }

    // ── Spawn / poll lifecycle ──────────────────────────────────────────────

    [Fact]
    public async Task Spawn_ReturnsHandle_AndResultCompletes()
    {
        var spawner = NewSessionSpawner();

        var spawn = await spawner.SpawnAsync("Scout", "audit the repo", CancellationToken.None);
        Assert.True(spawn.Ok);
        Assert.False(string.IsNullOrEmpty(spawn.Handle));
        Assert.Contains(spawn.Handle!, spawn.Message);

        var poll = await spawner.PollAsync(spawn.Handle!, waitSeconds: 5, CancellationToken.None);
        Assert.Equal(SpawnAgentPoll.Done, poll.Status);
        Assert.Contains("REPORT", poll.Message);
    }

    [Fact]
    public async Task Spawn_ChildInheritsParentSessionGrantGovernance_AndBridgeTools()
    {
        var spawner = NewSessionSpawner(GovernanceMode.Coding);

        await spawner.SpawnAsync("Scout", "audit the repo", CancellationToken.None);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal(UserId, call.UserId);
        Assert.Equal(SessionId, call.SessionId);              // same {soul}|{sessionId} Layer B grant context
        Assert.Equal(GovernanceMode.Coding, call.Mode);       // parent's mode, fresh counters
        Assert.True(call.AllowBridgeTools);                   // interactive-session child keeps terminal tools
        Assert.Equal("audit the repo", call.Prompt);
    }

    [Fact]
    public async Task Spawn_ResolvesPersonaByGeneratedNameOrNickname()
    {
        var spawner = NewSessionSpawner();

        var byName = await spawner.SpawnAsync("scout", "t", CancellationToken.None);      // case-insensitive
        var byNick = await spawner.SpawnAsync("Doc", "t", CancellationToken.None);        // nickname match

        Assert.True(byName.Ok);
        Assert.True(byNick.Ok);
        Assert.Equal(2, _runner.Calls.Count);
        Assert.NotEqual(_runner.Calls[0].SubAgentId, _runner.Calls[1].SubAgentId);
    }

    [Fact]
    public async Task Spawn_UnknownPersona_RefusesWithoutStartingRun()
    {
        var spawner = NewSessionSpawner();

        var spawn = await spawner.SpawnAsync("Nobody", "t", CancellationToken.None);

        Assert.False(spawn.Ok);
        Assert.Contains("Nobody", spawn.Message);
        Assert.Empty(_runner.Calls);
    }

    // ── Poll outcomes ───────────────────────────────────────────────────────

    [Fact]
    public async Task Poll_UnknownHandle_Errors()
    {
        var spawner = NewSessionSpawner();

        var poll = await spawner.PollAsync("no-such-handle", waitSeconds: 0, CancellationToken.None);

        Assert.Equal(SpawnAgentPoll.Unknown, poll.Status);
        Assert.Contains("no-such-handle", poll.Message);
    }

    [Fact]
    public async Task Poll_Running_WhenChildNotFinished()
    {
        _runner.Next = () => new TaskCompletionSource<string>().Task;   // never completes
        var spawner = NewSessionSpawner();
        var spawn = await spawner.SpawnAsync("Scout", "t", CancellationToken.None);

        var poll = await spawner.PollAsync(spawn.Handle!, waitSeconds: 0, CancellationToken.None);

        Assert.Equal(SpawnAgentPoll.Running, poll.Status);
    }

    [Fact]
    public async Task Poll_WaitsForCompletion_UpToWaitSeconds()
    {
        var tcs = new TaskCompletionSource<string>();
        _runner.Next = () => tcs.Task;
        var spawner = NewSessionSpawner();
        var spawn = await spawner.SpawnAsync("Scout", "t", CancellationToken.None);

        var pollTask = spawner.PollAsync(spawn.Handle!, waitSeconds: 30, CancellationToken.None);
        Assert.False(pollTask.IsCompleted);                 // still blocking on the child
        tcs.SetResult("LATE REPORT");

        var poll = await pollTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SpawnAgentPoll.Done, poll.Status);
        Assert.Contains("LATE REPORT", poll.Message);
    }

    [Fact]
    public async Task Poll_Failed_WhenChildFaults()
    {
        _runner.Next = () => Task.FromException<string>(new InvalidOperationException("boom"));
        var spawner = NewSessionSpawner();
        var spawn = await spawner.SpawnAsync("Scout", "t", CancellationToken.None);

        var poll = await spawner.PollAsync(spawn.Handle!, waitSeconds: 5, CancellationToken.None);

        Assert.Equal(SpawnAgentPoll.Failed, poll.Status);
        Assert.Contains("boom", poll.Message);
    }

    // ── Concurrency cap ─────────────────────────────────────────────────────

    [Fact]
    public async Task Spawn_RefusesBeyondConcurrentChildCap()
    {
        _runner.Next = () => new TaskCompletionSource<string>().Task;   // all children hang = all "running"
        var spawner = NewSessionSpawner();

        for (var i = 0; i < SubAgentSpawnService.MaxConcurrentChildren; i++)
        {
            var ok = await spawner.SpawnAsync("Scout", $"task {i}", CancellationToken.None);
            Assert.True(ok.Ok, $"spawn {i + 1} should be admitted");
        }

        var refused = await spawner.SpawnAsync("Scout", "one too many", CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains(SubAgentSpawnService.MaxConcurrentChildren.ToString(), refused.Message);
        Assert.Equal(SubAgentSpawnService.MaxConcurrentChildren, _runner.Calls.Count);
    }

    [Fact]
    public async Task Spawn_CapFreesUp_WhenAChildCompletes()
    {
        var tcs = new TaskCompletionSource<string>();
        _runner.Next = () => tcs.Task;
        var spawner = NewSessionSpawner();

        for (var i = 0; i < SubAgentSpawnService.MaxConcurrentChildren; i++)
            await spawner.SpawnAsync("Scout", $"task {i}", CancellationToken.None);
        tcs.SetResult("done");   // completes ALL hanging children (shared TCS)

        var admitted = await spawner.SpawnAsync("Scout", "now there is room", CancellationToken.None);
        Assert.True(admitted.Ok);
    }
}
