using Aria.Web.Data;
using Aria.Web.Data.Context;
using Aria.Web.Data.Agents;
using Aria.Web.Data.Collectives;
using Aria.Web.Data.Users;
using Aria.Web.Services.Agent;
using Aria.Web.Services.Collective;
using Aria.Web.Services.Cron;
using Aria.Web.Services.ModelBridge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aria.Tests.Web;

/// <summary>
/// Covers the scoped terminal opt-in for headless runs (vigils + Hive): the default strips
/// bridge/terminal tools, an explicitly-authorised run keeps them, the ambient Hive flag flows
/// and restores, and the per-vigil / per-collective flags persist end-to-end through their
/// booking/config services.
/// </summary>
public class HeadlessToolGatingTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    private const string UserId = "soul-1";

    public HeadlessToolGatingTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-gating-tests-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _dbFactory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var db = _dbFactory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Set<User>().Add(new User { Id = UserId, Name = "Test Soul" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    // ── Headless tool filter (vigil path + spawned-child path) ──────────────

    private static Dictionary<string, (bool Enabled, Dictionary<string, string> Config)> States() => new()
    {
        ["terminal"]  = (true, []),
        ["wargame"]   = (true, []),
        ["websearch"] = (true, []),
        ["webfetch"]  = (false, []),
    };

    [Fact]
    public void HeadlessToolList_ByDefault_StripsBridgeTools()
    {
        var tools = AgentBackgroundExecutor.BuildToolList(States(), UserId, allowBridgeTools: false);
        var ids = tools.Select(t => t.ToolId).ToList();

        Assert.Contains("websearch", ids);
        Assert.DoesNotContain("terminal", ids);
        Assert.DoesNotContain("wargame", ids);
        Assert.DoesNotContain("webfetch", ids);   // disabled tools are never included
        Assert.Equal(UserId, tools.Single(t => t.ToolId == "websearch").Config["_userId"]);
    }

    [Fact]
    public void HeadlessToolList_OptedIn_KeepsBridgeTools()
    {
        var tools = AgentBackgroundExecutor.BuildToolList(States(), UserId, allowBridgeTools: true);
        var ids = tools.Select(t => t.ToolId).ToList();

        Assert.Contains("terminal", ids);
        Assert.Contains("wargame", ids);
        Assert.Contains("websearch", ids);
        Assert.DoesNotContain("webfetch", ids);
    }

    // ── Sub-agent (drone / spawned-child) tool configs ──────────────────────

    private async Task<int> SeedAgentWithToolsAsync(params string[] toolIds)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        if (!db.Set<User>().Any(u => u.Id == UserId))
            db.Set<User>().Add(new User { Id = UserId, Name = "Test Soul" });
        var agent = new SubAgent
        {
            UserId = UserId, GeneratedName = "Drone", ArchetypeName = "test", GeneratedPersonality = "test",
        };
        db.SubAgents.Add(agent);
        await db.SaveChangesAsync();
        foreach (var id in toolIds)
            db.SubAgentToolStates.Add(new SubAgentToolState { SubAgentId = agent.Id, ToolId = id, Enabled = true });
        await db.SaveChangesAsync();
        return agent.Id;
    }

    [Fact]
    public async Task SubAgentToolConfigs_ByDefault_StripBridgeTools_OptedIn_KeepsThem()
    {
        var agentId = await SeedAgentWithToolsAsync("terminal", "websearch");
        var svc = new SubAgentService(_dbFactory);

        var stripped = await svc.GetEnabledToolConfigsAsync(agentId, UserId);
        Assert.Equal(["websearch"], stripped.Select(t => t.ToolId).Order().ToList());

        var kept = await svc.GetEnabledToolConfigsAsync(agentId, UserId, allowBridgeTools: true);
        Assert.Equal(["terminal", "websearch"], kept.Select(t => t.ToolId).Order().ToList());
    }

    // ── Ambient Hive flag ───────────────────────────────────────────────────

    [Fact]
    public void AmbientBridgeTools_DefaultsOff_ScopedOn_RestoresOnDispose()
    {
        Assert.False(AgentBackgroundExecutor.AmbientBridgeToolsAllowed);

        using (AgentBackgroundExecutor.WithAmbientBridgeTools(true))
        {
            Assert.True(AgentBackgroundExecutor.AmbientBridgeToolsAllowed);

            // A nested scope restoring to the outer value (the Hive run wraps a whole fan-out).
            using (AgentBackgroundExecutor.WithAmbientBridgeTools(false))
                Assert.False(AgentBackgroundExecutor.AmbientBridgeToolsAllowed);
            Assert.True(AgentBackgroundExecutor.AmbientBridgeToolsAllowed);
        }

        Assert.False(AgentBackgroundExecutor.AmbientBridgeToolsAllowed);
    }

    // ── Flag persistence plumbing ───────────────────────────────────────────

    [Fact]
    public async Task VigilBooking_PersistsAllowProjectTools()
    {
        var cron = new CronSlotService(_dbFactory, new ModelBridgeRegistry());
        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));

        var (okOff, _, jobOff) = await cron.BookAsync(UserId, day, 10, "default vigil", null, null, null);
        var (okOn, _, jobOn) = await cron.BookAsync(UserId, day, 11, "opted-in vigil", null, null, null,
            allowProjectTools: true);

        Assert.True(okOff); Assert.True(okOn);
        Assert.False(jobOff!.AllowProjectTools);   // default unchanged: chat+web+MCP only
        Assert.True(jobOn!.AllowProjectTools);

        using var db = await _dbFactory.CreateDbContextAsync();
        Assert.False(db.AgentCronJobs.Find(jobOff.Id)!.AllowProjectTools);
        Assert.True(db.AgentCronJobs.Find(jobOn.Id)!.AllowProjectTools);
    }

    [Fact]
    public async Task CollectiveConfig_PersistsAllowProjectTools()
    {
        var registry = new ModelBridgeRegistry();
        var collectives = new CollectiveService(_dbFactory, registry, new BridgeHiveClient(registry));

        // Legacy (server-stored) collective: no connected node → OriginNodeId stays null.
        var c = await collectives.CreateAsync(UserId, "Test Hive");
        Assert.Null(c.OriginNodeId);
        Assert.False(c.AllowProjectTools);   // default off

        // Flip the flag through the service's real write path (ExecuteUpdateAsync) — EF Core
        // versions are aligned across the solution now, so the test host can run it directly.
        await collectives.UpdateConfigAsync(
            c.Id, c.Name, c.Objective, c.OvermindSubAgentId, c.OvermindSourceName, c.OvermindModelId,
            c.MaxRounds, c.RequiresHumanApproval, c.Behavior, allowProjectTools: true);

        var reloaded = await collectives.GetAsync(c.Id);
        Assert.True(reloaded!.AllowProjectTools);
    }
}
