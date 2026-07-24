using Aria.Bridge.Data;
using Aria.Bridge.Infrastructure;
using Aria.Shared;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Locks the Layer B scope contract the scoped terminal opt-in relies on: a session-scoped grant
/// (vigil:{id}, hive:{id}, or a chat session token) authorises BOTH sensitive surfaces a project-
/// tools run touches — /tools/call (terminal/git/file built-ins) and /project-files/* — because
/// grants carry no per-endpoint scope and both paths funnel through the same grant check
/// (DirectTunnel.GateSensitiveAsync → ContextGrantStore.HasValidGrantForRequestAsync).
/// If the grant model ever grows per-endpoint scopes, the vigil/Hive minting must be extended —
/// these tests are the tripwire.
/// </summary>
public class ContextGrantCoverageTests : IDisposable
{
    private readonly string _dbPath;
    private readonly BridgeDbContext _db;

    public ContextGrantCoverageTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-cgc-tests-{Guid.NewGuid():N}.db");
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

    [Theory]
    // The sensitive tool surface an opted-in vigil/Hive child exercises.
    [InlineData("/tools/call")]
    [InlineData("/terminal/exec")]
    [InlineData("/project-files")]
    [InlineData("/project-files/list")]
    [InlineData("/project-files/read")]
    [InlineData("/project-files/write")]
    [InlineData("/project-git/status")]
    public void SensitiveSurfaces_AreAllClassifiedSensitive(string path)
    {
        Assert.Equal(RequestSensitivity.Sensitive, RequestClassifier.Classify("POST", path));
        Assert.Equal(RequestSensitivity.Sensitive, RequestClassifier.Classify("POST", path, body: null));
    }

    [Theory]
    // A write/exec built-in is Sensitive even body-aware; a read-only built-in stays Benign.
    [InlineData("""{"toolName":"write_file","arguments":{}}""", RequestSensitivity.Sensitive)]
    [InlineData("""{"toolName":"bash_exec","arguments":{}}""", RequestSensitivity.Sensitive)]
    [InlineData("""{"toolName":"read_file","arguments":{}}""", RequestSensitivity.Benign)]
    [InlineData("""{"toolName":"grep","arguments":{}}""", RequestSensitivity.Benign)]
    [InlineData("""{"serverName":"github","toolName":"create_issue"}""", RequestSensitivity.Sensitive)]
    public void ToolCall_BodyAware_Classification(string body, RequestSensitivity expected)
    {
        Assert.Equal(expected, RequestClassifier.Classify("POST", "/tools/call", body));
    }

    [Theory]
    [InlineData("vigil:42")]
    [InlineData("hive:7")]
    [InlineData("chat-session-token")]
    public async Task SessionGrant_CoversTheWholeSensitiveSurface(string sessionId)
    {
        var soul = TestCrypto.GenerateSoul(out _);
        var ctxId = ContextGrantStore.ContextId(soul.ServerSoulId!, sessionId);
        await ContextGrantStore.GrantAsync(_db, soul, ctxId, TimeSpan.FromHours(2));

        // Both /tools/call and /project-files/* authorize through this single check with the
        // request's session id — one minted grant covers every sensitive surface of the run.
        Assert.True(await ContextGrantStore.HasValidGrantForRequestAsync(_db, soul, sessionId));

        // ...but ONLY that session: a sibling run's id is not covered.
        Assert.False(await ContextGrantStore.HasValidGrantForRequestAsync(_db, soul, sessionId + "-other"));
    }

    [Fact]
    public async Task SoulWideGrant_CoversAnySession()
    {
        var soul = TestCrypto.GenerateSoul(out _);
        await ContextGrantStore.GrantAsync(_db, soul, soul.ServerSoulId!, TimeSpan.FromHours(2));

        Assert.True(await ContextGrantStore.HasValidGrantForRequestAsync(_db, soul, "vigil:42"));
        Assert.True(await ContextGrantStore.HasValidGrantForRequestAsync(_db, soul, null));
    }
}
