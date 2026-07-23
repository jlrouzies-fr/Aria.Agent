using System.Text.Json;
using Aria.Bridge;
using Aria.Bridge.Data;
using Aria.Bridge.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Verifies the Wave 5 enforcement seam: a session's node-signed path grants union into the effective
/// allowed set in <see cref="NodeTerminalPolicy.ResolveAsync"/> (and into a server-supplied built-in
/// tools policy via <see cref="NodeTerminalPolicy.ApplySessionPathGrantsAsync"/>) — only for the
/// session they were minted for, while the request-may-only-narrow rule stays untouched.
/// </summary>
public class NodeTerminalPolicySessionGrantTests : IDisposable
{
    private readonly string _dbPath;
    private readonly BridgeDbContext _db;

    private readonly string _declared = NewDir("declared");
    private readonly string _granted  = NewDir("granted");
    private readonly string _other    = NewDir("other");

    public NodeTerminalPolicySessionGrantTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-ntp-tests-{Guid.NewGuid():N}.db");
        var opts = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _db = new BridgeDbContext(opts);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static string NewDir(string name) =>
        Path.Combine(Path.GetTempPath(), $"aria-ntp-{name}-{Guid.NewGuid():N}");

    private async Task<BridgeSoul> SeedSoulAsync(bool projectsEnabled = true)
    {
        var soul = TestCrypto.GenerateSoul(out _);
        soul.Name = "test-soul";
        soul.ProjectsEnabled = projectsEnabled;
        soul.TerminalAllowedPathsJson = JsonSerializer.Serialize(new[] { _declared });
        _db.Souls.Add(soul);
        await _db.SaveChangesAsync();
        return soul;
    }

    private static string ChildOf(string dir) => Path.Combine(dir, "file.txt");

    [Fact]
    public async Task SessionGrant_UnionsIntoEffectivePaths()
    {
        var soul = await SeedSoulAsync();
        await ContextGrantStore.GrantPathAsync(_db, soul, "sess-1", _granted, TimeSpan.FromHours(8));

        var policy = await NodeTerminalPolicy.ResolveAsync(_db, null, "sess-1");

        policy.EnforcePath(ChildOf(_declared));   // declared base still allowed
        policy.EnforcePath(ChildOf(_granted));    // the node-signed expansion is allowed
        Assert.Contains(_granted, policy.AllowedPaths!);
        Assert.Throws<TerminalSecurityException>(() => policy.EnforcePath(ChildOf(_other)));
    }

    [Fact]
    public async Task NoSessionId_NoUnion()
    {
        var soul = await SeedSoulAsync();
        await ContextGrantStore.GrantPathAsync(_db, soul, "sess-1", _granted, TimeSpan.FromHours(8));

        var policy = await NodeTerminalPolicy.ResolveAsync(_db, null);

        Assert.Throws<TerminalSecurityException>(() => policy.EnforcePath(ChildOf(_granted)));
    }

    [Fact]
    public async Task OtherSession_NoUnion()
    {
        var soul = await SeedSoulAsync();
        await ContextGrantStore.GrantPathAsync(_db, soul, "sess-1", _granted, TimeSpan.FromHours(8));

        var policy = await NodeTerminalPolicy.ResolveAsync(_db, null, "sess-2");

        Assert.Throws<TerminalSecurityException>(() => policy.EnforcePath(ChildOf(_granted)));
    }

    [Fact]
    public async Task RequestParams_StillCannotWiden_BeyondBasePlusGrants()
    {
        var soul = await SeedSoulAsync();
        await ContextGrantStore.GrantPathAsync(_db, soul, "sess-1", _granted, TimeSpan.FromHours(8));

        // The request asks for a path outside both the declared base and the grant: it is narrowed
        // away to nothing, so the effective policy blocks EVERYTHING (fail closed) — even the base.
        var policy = await NodeTerminalPolicy.ResolveAsync(_db, [_other], "sess-1");

        Assert.Throws<TerminalSecurityException>(() => policy.EnforcePath(ChildOf(_other)));
        Assert.Throws<TerminalSecurityException>(() => policy.EnforcePath(ChildOf(_declared)));
        Assert.Throws<TerminalSecurityException>(() => policy.EnforcePath(ChildOf(_granted)));
    }

    [Fact]
    public async Task RequestNarrowing_WorksWithinAGrant()
    {
        var soul = await SeedSoulAsync();
        await ContextGrantStore.GrantPathAsync(_db, soul, "sess-1", _granted, TimeSpan.FromHours(8));

        var sub = Path.Combine(_granted, "sub");
        var policy = await NodeTerminalPolicy.ResolveAsync(_db, [sub], "sess-1");

        policy.EnforcePath(Path.Combine(sub, "file.txt"));
        Assert.Throws<TerminalSecurityException>(() => policy.EnforcePath(ChildOf(_granted)));
    }

    [Fact]
    public async Task ProjectsCapabilityOff_GrantDoesNotResurrectAccess()
    {
        var soul = await SeedSoulAsync(projectsEnabled: false);
        await ContextGrantStore.GrantPathAsync(_db, soul, "sess-1", _granted, TimeSpan.FromHours(8));

        var policy = await NodeTerminalPolicy.ResolveAsync(_db, null, "sess-1");

        Assert.Throws<TerminalSecurityException>(() => policy.EnforcePath(ChildOf(_granted)));
        Assert.Throws<TerminalSecurityException>(() => policy.EnforcePath(ChildOf(_declared)));
    }

    [Fact]
    public async Task BuiltinPolicy_UnionAddsGrantPaths_OnlyForTheRightSession()
    {
        var soul = await SeedSoulAsync();
        await ContextGrantStore.GrantPathAsync(_db, soul, "sess-1", _granted, TimeSpan.FromHours(8));

        var serverPolicy = new SecurityPolicy(AllowedPaths: [_declared]);

        var merged = await NodeTerminalPolicy.ApplySessionPathGrantsAsync(serverPolicy, "sess-1", _db);
        Assert.NotNull(merged);
        merged!.EnforcePath(ChildOf(_granted));
        Assert.Throws<TerminalSecurityException>(() => merged.EnforcePath(ChildOf(_other)));

        var otherSession = await NodeTerminalPolicy.ApplySessionPathGrantsAsync(serverPolicy, "sess-2", _db);
        Assert.Same(serverPolicy, otherSession);   // untouched — no grant for that session
    }

    [Fact]
    public async Task BuiltinPolicy_NullOrUnrestricted_PassesThrough()
    {
        await SeedSoulAsync();

        Assert.Null(await NodeTerminalPolicy.ApplySessionPathGrantsAsync(null, "sess-1", _db));

        var unrestricted = new SecurityPolicy();
        Assert.Same(unrestricted,
            await NodeTerminalPolicy.ApplySessionPathGrantsAsync(unrestricted, "sess-1", _db));
    }
}
