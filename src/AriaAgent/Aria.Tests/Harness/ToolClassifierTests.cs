using System.Text.Json;
using Aria.Harness.Governance;
using Xunit;

namespace Aria.Tests.Harness;

/// <summary>
/// Guards the governance decision logic: budgets, loop detection, scope lock, and mutation/seal
/// escalation per mode. Pure logic — no host, bridge, or model involved.
/// </summary>
public class ToolClassifierTests
{
    private static Dictionary<string, JsonElement> Args(object o)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(o));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static GovernanceContext Ctx(GovernanceMode mode, IReadOnlyList<string>? scope = null)
    {
        var ctx = new GovernanceContext(GovernancePolicy.FromMode(mode));
        ctx.BeginTurn(scope);
        return ctx;
    }

    [Fact]
    public void Off_AllowsEverything()
    {
        var ctx = Ctx(GovernanceMode.Off);
        var v = ToolClassifier.Classify(ctx, "bash_exec", Args(new { command = "rm -rf x" }), "rm -rf x");
        Assert.Equal(ToolSeverity.Allowed, v.Severity);
    }

    [Fact]
    public void Strict_Mutation_NeedsApproval()
    {
        var ctx = Ctx(GovernanceMode.Strict);
        var v = ToolClassifier.Classify(ctx, "write_file", Args(new { file_path = "/tmp/a.txt" }), "a.txt");
        Assert.Equal(ToolSeverity.NeedsApproval, v.Severity);
    }

    [Fact]
    public void Paranoid_HighStakes_NeedsSeal()
    {
        var ctx = Ctx(GovernanceMode.Paranoid);
        var v = ToolClassifier.Classify(ctx, "bash_exec", Args(new { command = "ls" }), "ls");
        Assert.Equal(ToolSeverity.NeedsSeal, v.Severity);
    }

    [Fact]
    public void Strict_OutOfScopeRead_Blocked()
    {
        var ctx = Ctx(GovernanceMode.Strict, scope: new[] { "/home/user/project" });
        var v = ToolClassifier.Classify(ctx, "read_file", Args(new { path = "/etc/passwd" }), "/etc/passwd");
        Assert.Equal(ToolSeverity.Blocked, v.Severity);
    }

    [Fact]
    public void Strict_InScopeRead_Allowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "gov-scope-test");
        var ctx  = Ctx(GovernanceMode.Strict, scope: new[] { root });
        var v = ToolClassifier.Classify(ctx, "read_file",
            Args(new { path = Path.Combine(root, "src", "a.cs") }), "a.cs");
        Assert.Equal(ToolSeverity.Allowed, v.Severity);
    }

    [Fact]
    public void Balanced_ReadBudget_BlocksWhenExceeded()
    {
        var ctx = Ctx(GovernanceMode.Balanced);
        var max = GovernancePolicy.FromMode(GovernanceMode.Balanced).MaxFileReadsPerTurn;

        ToolSeverity last = ToolSeverity.Allowed;
        for (var i = 0; i <= max + 1; i++)
            last = ToolClassifier.Classify(ctx, "read_file", Args(new { path = $"/x/f{i}.txt" }), "f").Severity;

        Assert.Equal(ToolSeverity.Blocked, last);
    }

    [Fact]
    public void Balanced_RepeatedIdenticalCall_BlockedAsLoop()
    {
        var ctx = Ctx(GovernanceMode.Balanced);
        var args = new { path = "/x/same.txt" };

        var v1 = ToolClassifier.Classify(ctx, "read_file", Args(args), "same").Severity;
        var v2 = ToolClassifier.Classify(ctx, "read_file", Args(args), "same").Severity;
        var v3 = ToolClassifier.Classify(ctx, "read_file", Args(args), "same").Severity;

        Assert.Equal(ToolSeverity.Allowed, v1);
        Assert.Equal(ToolSeverity.Allowed, v2);
        Assert.Equal(ToolSeverity.Blocked, v3); // LoopThreshold = 3
    }
}
