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
    public void Paranoid_RunBackground_NeedsSeal()
    {
        var ctx = Ctx(GovernanceMode.Paranoid);
        var v = ToolClassifier.Classify(ctx, "run_background", Args(new { command = "python app.py" }), "server");
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

    // ── New-tool taxonomy (grep + git builtins) ────────────────────────────────
    // ToolCategories is internal, so the taxonomy is verified behaviourally through Classify.

    [Theory]
    [InlineData("grep")]
    [InlineData("git_status")]
    [InlineData("git_diff")]
    [InlineData("git_log")]
    [InlineData("system_info")]
    [InlineData("process_list")]
    [InlineData("process_output")]
    [InlineData("wait_for")]
    public void NewReadTools_CountAgainstReadBudget(string tool)
    {
        var ctx = Ctx(GovernanceMode.Balanced);
        var max = GovernancePolicy.FromMode(GovernanceMode.Balanced).MaxFileReadsPerTurn;

        ToolSeverity last = ToolSeverity.Allowed;
        for (var i = 0; i <= max + 1; i++)
            last = ToolClassifier.Classify(ctx, tool, Args(new { path = $"/x/d{i}", repo_path = $"/x/d{i}" }), "r").Severity;

        Assert.Equal(ToolSeverity.Blocked, last);
    }

    [Theory]
    [InlineData("git_stage")]
    [InlineData("git_commit")]
    [InlineData("git_discard")]
    public void GitMutations_Strict_NeedApproval(string tool)
    {
        var ctx = Ctx(GovernanceMode.Strict);
        var v = ToolClassifier.Classify(ctx, tool,
            Args(new { repo_path = "/x", paths = new[] { "a.txt" }, message = "m" }), "git");
        Assert.Equal(ToolSeverity.NeedsApproval, v.Severity);
    }

    [Theory]
    [InlineData("process_kill")]
    [InlineData("multi_edit")]
    [InlineData("undo_file")]
    [InlineData("run_background")]
    public void NewMutations_Strict_NeedApproval(string tool)
    {
        var ctx = Ctx(GovernanceMode.Strict);
        var v = ToolClassifier.Classify(ctx, tool,
            Args(new { path = "/x/a.txt", pid = 1 }), "m");
        Assert.Equal(ToolSeverity.NeedsApproval, v.Severity);
    }

    [Fact]
    public void Paranoid_ProcessKill_NeedsApproval_NotSeal()
    {
        // Registry-validated kills are Mutating but NOT HighStakes — no Seal escalation.
        var ctx = Ctx(GovernanceMode.Paranoid);
        var v = ToolClassifier.Classify(ctx, "process_kill", Args(new { pid = 1 }), "kill");
        Assert.Equal(ToolSeverity.NeedsApproval, v.Severity);
    }

    [Fact]
    public void Paranoid_GitDiscard_NeedsSeal_OtherGitMutations_NeedApproval()
    {
        var ctx = Ctx(GovernanceMode.Paranoid);
        var discard = ToolClassifier.Classify(ctx, "git_discard",
            Args(new { repo_path = "/x", paths = new[] { "a.txt" } }), "discard");
        Assert.Equal(ToolSeverity.NeedsSeal, discard.Severity);

        var stage = ToolClassifier.Classify(ctx, "git_stage",
            Args(new { repo_path = "/x", paths = new[] { "a.txt" } }), "stage");
        Assert.Equal(ToolSeverity.NeedsApproval, stage.Severity);

        var commit = ToolClassifier.Classify(ctx, "git_commit",
            Args(new { repo_path = "/x", message = "m" }), "commit");
        Assert.Equal(ToolSeverity.NeedsApproval, commit.Severity);
    }

    // ── install_software: approval-gated in every governed mode ───────────────

    [Theory]
    [InlineData(GovernanceMode.Balanced)]
    [InlineData(GovernanceMode.Coding)]
    [InlineData(GovernanceMode.Strict)]
    public void InstallSoftware_NeedsApproval_InEveryLaxOrStrictMode(GovernanceMode mode)
    {
        // Coding and Balanced let ordinary mutations run freely — install_software must still ask.
        var ctx = Ctx(mode);
        var v = ToolClassifier.Classify(ctx, "install_software",
            Args(new { manager = "brew", package = "ripgrep" }), "brew install ripgrep");
        Assert.Equal(ToolSeverity.NeedsApproval, v.Severity);
    }

    [Fact]
    public void Plan_InstallSoftware_Blocked_LikeAnyMutation()
    {
        var ctx = Ctx(GovernanceMode.Plan);
        var v = ToolClassifier.Classify(ctx, "install_software",
            Args(new { manager = "brew", package = "ripgrep" }), "brew install ripgrep");
        Assert.Equal(ToolSeverity.Blocked, v.Severity);
        Assert.Contains("Plan mode", v.Reason);
    }

    [Fact]
    public void Paranoid_InstallSoftware_NeedsSeal()
    {
        var ctx = Ctx(GovernanceMode.Paranoid);
        var v = ToolClassifier.Classify(ctx, "install_software",
            Args(new { manager = "brew", package = "ripgrep" }), "brew install ripgrep");
        Assert.Equal(ToolSeverity.NeedsSeal, v.Severity);
    }

    [Fact]
    public void Off_InstallSoftware_RunsUnchecked()
    {
        var ctx = Ctx(GovernanceMode.Off);
        var v = ToolClassifier.Classify(ctx, "install_software",
            Args(new { manager = "brew", package = "ripgrep" }), "brew install ripgrep");
        Assert.Equal(ToolSeverity.Allowed, v.Severity);
    }

    // ── Coding mode ──────────────────────────────────────────────────────────

    [Fact]
    public void Coding_ToolBudget_61stCallBlocked()
    {
        var ctx = Ctx(GovernanceMode.Coding);
        var max = GovernancePolicy.FromMode(GovernanceMode.Coding).MaxToolCallsPerTurn;
        Assert.Equal(60, max);

        // Distinct args each call so the loop guard (threshold 4) doesn't fire first.
        ToolSeverity last = ToolSeverity.Allowed;
        for (var i = 0; i <= max; i++)
            last = ToolClassifier.Classify(ctx, "web_search", Args(new { query = $"q{i}" }), "q").Severity;

        Assert.Equal(ToolSeverity.Blocked, last);
    }

    [Fact]
    public void Coding_Mutations_RunFreely_NoApprovalNoSeal()
    {
        var ctx = Ctx(GovernanceMode.Coding);
        var write = ToolClassifier.Classify(ctx, "write_file", Args(new { file_path = "/tmp/a.txt" }), "a.txt");
        Assert.Equal(ToolSeverity.Allowed, write.Severity);

        var bash = ToolClassifier.Classify(ctx, "bash_exec", Args(new { command = "dotnet build" }), "build");
        Assert.Equal(ToolSeverity.Allowed, bash.Severity);
    }

    // ── Plan mode ────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_Mutation_Blocked_WithPresentThePlanInstruction()
    {
        var ctx = Ctx(GovernanceMode.Plan);
        var v = ToolClassifier.Classify(ctx, "write_file", Args(new { file_path = "/tmp/a.txt" }), "a.txt");
        Assert.Equal(ToolSeverity.Blocked, v.Severity);
        Assert.Contains("Plan mode", v.Reason);
        Assert.Contains("switch out of Plan mode", v.Reason);
    }

    [Fact]
    public void Plan_HighStakes_Blocked_LikeAnyMutation()
    {
        var ctx = Ctx(GovernanceMode.Plan);
        var v = ToolClassifier.Classify(ctx, "bash_exec", Args(new { command = "ls" }), "ls");
        Assert.Equal(ToolSeverity.Blocked, v.Severity);
    }

    [Fact]
    public void Plan_Reads_AllowedUpToBudget()
    {
        var ctx = Ctx(GovernanceMode.Plan);
        var max = GovernancePolicy.FromMode(GovernanceMode.Plan).MaxFileReadsPerTurn;
        Assert.Equal(40, max);

        ToolSeverity last = ToolSeverity.Allowed;
        for (var i = 0; i < max; i++)
            last = ToolClassifier.Classify(ctx, "read_file", Args(new { path = $"/x/f{i}.txt" }), "f").Severity;
        Assert.Equal(ToolSeverity.Allowed, last);

        var over = ToolClassifier.Classify(ctx, "read_file", Args(new { path = "/x/one-more.txt" }), "f");
        Assert.Equal(ToolSeverity.Blocked, over.Severity);
    }

    // ── Budget overrides ─────────────────────────────────────────────────────

    [Fact]
    public void BudgetOverride_Tools_6thCallBlocked()
    {
        var policy = GovernancePolicy.FromMode(GovernanceMode.Balanced).WithBudgetOverrides(5, null);
        var ctx = new GovernanceContext(policy);
        ctx.BeginTurn(null);

        ToolSeverity last = ToolSeverity.Allowed;
        for (var i = 0; i <= 5; i++)
            last = ToolClassifier.Classify(ctx, "web_search", Args(new { query = $"q{i}" }), "q").Severity;

        Assert.Equal(ToolSeverity.Blocked, last);
    }

    [Fact]
    public void BudgetOverride_Reads_3rdReadBlocked()
    {
        var policy = GovernancePolicy.FromMode(GovernanceMode.Balanced).WithBudgetOverrides(null, 2);
        var ctx = new GovernanceContext(policy);
        ctx.BeginTurn(null);

        var r1 = ToolClassifier.Classify(ctx, "read_file", Args(new { path = "/x/a.txt" }), "a").Severity;
        var r2 = ToolClassifier.Classify(ctx, "read_file", Args(new { path = "/x/b.txt" }), "b").Severity;
        var r3 = ToolClassifier.Classify(ctx, "read_file", Args(new { path = "/x/c.txt" }), "c").Severity;

        Assert.Equal(ToolSeverity.Allowed, r1);
        Assert.Equal(ToolSeverity.Allowed, r2);
        Assert.Equal(ToolSeverity.Blocked, r3);
    }

    [Fact]
    public void BudgetOverride_Null_KeepsModeDefaults()
    {
        var policy = GovernancePolicy.FromMode(GovernanceMode.Strict).WithBudgetOverrides(null, null);
        Assert.Equal(GovernancePolicy.FromMode(GovernanceMode.Strict), policy);
    }

    [Fact]
    public void ModeSwitch_MidSession_TakesEffectNextTurn()
    {
        var ctx = new GovernanceContext(GovernancePolicy.FromMode(GovernanceMode.Balanced));
        ctx.BeginTurn(null);

        // Balanced caps at 30 calls/turn — the 31st is refused.
        ToolSeverity balanced31st = ToolSeverity.Allowed;
        for (var i = 0; i <= 30; i++)
            balanced31st = ToolClassifier.Classify(ctx, "web_search", Args(new { query = $"q{i}" }), "q").Severity;
        Assert.Equal(ToolSeverity.Blocked, balanced31st);

        // Switch to Coding (60/turn) — re-read at the next BeginTurn, like Harness.StreamAsync does.
        ctx.BeginTurn(null, GovernancePolicy.FromMode(GovernanceMode.Coding));

        ToolSeverity coding31st = ToolSeverity.Allowed;
        for (var i = 0; i <= 30; i++)
            coding31st = ToolClassifier.Classify(ctx, "web_search", Args(new { query = $"q{i}" }), "q").Severity;
        Assert.Equal(ToolSeverity.Allowed, coding31st);
    }
}
