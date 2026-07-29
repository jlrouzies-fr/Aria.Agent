using System.Text.Json;
using Aria.Harness.Bridge;
using Aria.Harness.Governance;
using Microsoft.Extensions.AI;
using Xunit;

namespace Aria.Tests.Harness;

/// <summary>
/// Post-mutation verify nudge: while a turn accumulates successful file mutations with no
/// build/test verification, GovernedTool appends a one-line reminder to the mutation's own result
/// (at 1, then every 5). A PASSED run_tests or a bash_exec build/test command silences it; the
/// Governance:VerifyNudge policy toggle (default on) turns the whole behaviour off. The nudge
/// never blocks, never fails a call, and never touches budgets.
/// </summary>
public class GovernedToolVerifyNudgeTests
{
    private const string NudgeMarker = "no build/test run yet";

    private sealed class FakeTool : AIFunction
    {
        private static readonly JsonElement _schema = JsonDocument.Parse("{}").RootElement;

        private readonly object _result;

        public FakeTool(string name, object result)
        {
            Name    = name;
            _result = result;
        }

        public override string Name { get; }
        public override string Description => "";
        public override JsonElement JsonSchema => _schema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => new(_result);
    }

    private static GovernanceContext CodingCtx(GovernancePolicy? policy = null)
    {
        var ctx = new GovernanceContext(policy ?? GovernancePolicy.FromMode(GovernanceMode.Coding));
        ctx.BeginTurn(null);
        return ctx;
    }

    private static GovernedTool Wrap(AIFunction inner, GovernanceContext ctx) =>
        new(inner, ctx, null, null, null);

    private static AIFunctionArguments CallArgs(object o)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;
        return new AIFunctionArguments(dict.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
    }

    private static async Task<string> Invoke(GovernedTool tool, object args) =>
        (await tool.InvokeAsync(CallArgs(args)))?.ToString() ?? "";

    // Successful file mutations arrive from the bridge with UI metadata; failures (and the
    // metadata-less successes of create_dir / delete_dir / directory moves) arrive as plain text
    // wrapped with the bridge's own failure flag.
    private static FileMutationToolResult MutationOk(string path) =>
        new() { Text = $"Wrote 5 chars to {path}" };

    private static BridgeToolResult TextOk(string text)  => new() { Text = text };
    private static BridgeToolResult TextErr(string text) => new() { Text = text, IsError = true };

    private const string PassedRun = "◈ TEST RUN [dotnet test] — PASSED (exit 0, 1.2s)\npassed: 3  failed: 0";
    private const string FailedRun = "◈ TEST RUN [dotnet test] — FAILED (exit 1, 1.2s)\npassed: 1  failed: 2";

    [Fact]
    public async Task FirstSuccessfulMutation_AppendsNudge()
    {
        var ctx  = CodingCtx();
        var tool = Wrap(new FakeTool("write_file", MutationOk("/tmp/a.txt")), ctx);

        var result = await Invoke(tool, new { path = "/tmp/a.txt", content = "hello" });

        Assert.StartsWith("Wrote 5 chars to /tmp/a.txt", result);
        Assert.Contains("◈ 1 file(s) mutated this turn, no build/test run yet — " +
                        "consider verifying (run_tests, or project_info to infer the command).", result);
        Assert.Equal(1, ctx.MutationsThisTurn);
    }

    [Fact]
    public async Task NudgeThresholds_FirstThenEveryFive()
    {
        var ctx  = CodingCtx();
        var tool = Wrap(new FakeTool("edit_file", MutationOk("/tmp/x")), ctx);

        // Distinct path per call — identical (name, args) repeats trip loop detection.
        var nudged = new List<bool>();
        for (var i = 1; i <= 7; i++)
            nudged.Add((await Invoke(tool, new { path = $"/tmp/f{i}.txt", old_string = "a", new_string = "b" }))
                .Contains(NudgeMarker));

        Assert.Equal([true, false, false, false, false, true, false], nudged);
        Assert.Equal(7, ctx.MutationsThisTurn);
    }

    [Fact]
    public async Task FailedMutation_NeitherCountsNorNudges()
    {
        var ctx  = CodingCtx();
        var tool = Wrap(new FakeTool("create_dir", TextErr("BLOCKED: outside allowed directories")), ctx);

        var result = await Invoke(tool, new { path = "/tmp/nope" });

        Assert.Equal("BLOCKED: outside allowed directories", result); // unwrapped, no nudge
        Assert.Equal(0, ctx.MutationsThisTurn);
    }

    [Fact]
    public async Task MetadataCarryingErrorResult_NeitherCountsNorNudges()
    {
        var ctx  = CodingCtx();
        var tool = Wrap(new FakeTool("edit_file",
            new FileMutationToolResult { Text = "ERROR: disk full", IsError = true }), ctx);

        var result = await Invoke(tool, new { path = "/tmp/a.txt", old_string = "a", new_string = "b" });

        Assert.Equal("ERROR: disk full", result);
        Assert.Equal(0, ctx.MutationsThisTurn);
    }

    [Theory]
    [InlineData("bash_exec")]
    [InlineData("git_commit")]
    [InlineData("read_file")]
    [InlineData("undo_file")]
    public async Task NonFileMutations_DoNotCount(string toolName)
    {
        var ctx  = CodingCtx();
        var tool = Wrap(new FakeTool(toolName, TextOk("ok")), ctx);

        var result = await Invoke(tool, new { command = "ls", path = "/tmp/a.txt" });

        Assert.Equal("ok", result);
        Assert.Equal(0, ctx.MutationsThisTurn);
        Assert.DoesNotContain(NudgeMarker, result);
    }

    [Fact]
    public async Task RunTestsPassed_SuppressesNudge()
    {
        var ctx = CodingCtx();

        var runResult = await Invoke(Wrap(new FakeTool("run_tests", TextOk(PassedRun)), ctx),
            new { cwd = "/tmp/proj" });

        Assert.True(ctx.VerificationRan);
        Assert.DoesNotContain(NudgeMarker, runResult); // run_tests itself never nudges

        // Mutations are still counted — only the reminder goes quiet.
        var result = await Invoke(Wrap(new FakeTool("write_file", MutationOk("/tmp/a.txt")), ctx),
            new { path = "/tmp/a.txt", content = "x" });
        Assert.Equal(1, ctx.MutationsThisTurn);
        Assert.DoesNotContain(NudgeMarker, result);
    }

    [Fact]
    public async Task RunTestsFailed_DoesNotSuppress()
    {
        var ctx = CodingCtx();

        await Invoke(Wrap(new FakeTool("run_tests", TextErr(FailedRun)), ctx), new { cwd = "/tmp/proj" });

        Assert.False(ctx.VerificationRan);
        var result = await Invoke(Wrap(new FakeTool("write_file", MutationOk("/tmp/a.txt")), ctx),
            new { path = "/tmp/a.txt", content = "x" });
        Assert.Contains(NudgeMarker, result);
    }

    [Fact]
    public async Task RunTestsBackgroundConversion_DoesNotSuppress()
    {
        var ctx = CodingCtx();

        // A suite that outlives its timeout converts to a background job — the run has NOT
        // completed, so it must not silence the nudge even though the call itself succeeded.
        const string converted = """{"converted_to_background":true,"pid":4321,"note":"Test run exceeded the 120s timeout and is STILL RUNNING"}""";
        await Invoke(Wrap(new FakeTool("run_tests", TextOk(converted)), ctx), new { cwd = "/tmp/proj" });

        Assert.False(ctx.VerificationRan);
        var result = await Invoke(Wrap(new FakeTool("write_file", MutationOk("/tmp/a.txt")), ctx),
            new { path = "/tmp/a.txt", content = "x" });
        Assert.Contains(NudgeMarker, result);
    }

    [Theory]
    // The build/test pattern list (mirrors what project_info / run_tests can infer).
    [InlineData("dotnet test", true)]
    [InlineData("dotnet build", true)]
    [InlineData("cd src && dotnet test --filter Foo", true)]
    [InlineData("dotnet test --logger trx; dotnet build -c Release", true)]
    [InlineData("pytest", true)]
    [InlineData("pytest -k cart", true)]
    [InlineData("python -m pytest tests/", true)]
    [InlineData("npm test", true)]
    [InlineData("npm run build", true)]
    [InlineData("npm run test", true)]
    [InlineData("cargo test", true)]
    [InlineData("go test ./...", true)]
    [InlineData("make test", true)]
    [InlineData("DOTNET TEST", true)]
    // Near-misses that must NOT count as verification.
    [InlineData("dotnet restore", false)]
    [InlineData("dotnet run", false)]
    [InlineData("npm install", false)]
    [InlineData("npm run dev", false)]
    [InlineData("cargo build", false)]
    [InlineData("go build ./...", false)]
    [InlineData("make", false)]
    [InlineData("make build", false)]
    [InlineData("ls -la", false)]
    public async Task BashExecVerificationCommand_SuppressesPerPattern(string command, bool suppresses)
    {
        var ctx = CodingCtx();

        await Invoke(Wrap(new FakeTool("bash_exec", TextOk("done")), ctx), new { command });

        Assert.Equal(suppresses, ctx.VerificationRan);
        var result = await Invoke(Wrap(new FakeTool("write_file", MutationOk("/tmp/a.txt")), ctx),
            new { path = "/tmp/a.txt", content = "x" });
        Assert.Equal(suppresses, !result.Contains(NudgeMarker));
    }

    [Fact]
    public async Task BashExecVerificationCommand_SuppressesEvenWhenTheRunFailed()
    {
        var ctx = CodingCtx();

        // A red test run is still a verification — the agent has the failure output to react to.
        await Invoke(Wrap(new FakeTool("bash_exec", TextErr(FailedRun)), ctx), new { command = "dotnet test" });

        Assert.True(ctx.VerificationRan);
    }

    [Fact]
    public async Task ToggleOff_NeverNudges_ButStillCounts()
    {
        var policy = GovernancePolicy.FromMode(GovernanceMode.Coding) with { VerifyNudge = false };
        var ctx    = CodingCtx(policy);
        var tool   = Wrap(new FakeTool("edit_file", MutationOk("/tmp/x")), ctx);

        for (var i = 1; i <= 6; i++)
        {
            var result = await Invoke(tool, new { path = $"/tmp/f{i}.txt", old_string = "a", new_string = "b" });
            Assert.DoesNotContain(NudgeMarker, result);
        }
        Assert.Equal(6, ctx.MutationsThisTurn);
    }

    [Fact]
    public async Task BeginTurn_ResetsCounterAndVerification()
    {
        var ctx  = CodingCtx();
        var tool = Wrap(new FakeTool("write_file", MutationOk("/tmp/x")), ctx);

        await Invoke(Wrap(new FakeTool("run_tests", TextOk(PassedRun)), ctx), new { cwd = "/tmp" });
        var first = await Invoke(tool, new { path = "/tmp/a.txt", content = "x" });
        Assert.DoesNotContain(NudgeMarker, first); // verified this turn → quiet

        ctx.BeginTurn(null); // new turn: both the counter and the verification flag reset

        Assert.False(ctx.VerificationRan);
        Assert.Equal(0, ctx.MutationsThisTurn);
        var second = await Invoke(tool, new { path = "/tmp/b.txt", content = "x" });
        Assert.Contains("◈ 1 file(s) mutated", second);
    }

    [Fact]
    public async Task NudgeAppendsAfterExistingResultText_ExactlyOnce()
    {
        var ctx = CodingCtx();
        // The bridge's diff feedback already rides the same result text — the nudge must compose
        // with it as a plain suffix, at most once per result.
        var withDiff = new FileMutationToolResult
        {
            Text = "Replaced 1 occurrence in /tmp/a.txt\n\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-old\n+new",
        };
        var tool = Wrap(new FakeTool("edit_file", withDiff), ctx);

        var result = await Invoke(tool, new { path = "/tmp/a.txt", old_string = "old", new_string = "new" });

        Assert.StartsWith(withDiff.Text, result);
        Assert.Equal(1, result.Split(NudgeMarker).Length - 1);
    }

    [Fact]
    public async Task StrictMode_NudgeStillAppendsAfterApproval()
    {
        // Strict gates every mutation behind an approval; once the human approves, the successful
        // call still earns the nudge (approvals come in batches — the reminder helps the human too).
        var ctx = new GovernanceContext(GovernancePolicy.FromMode(GovernanceMode.Strict));
        ctx.BeginTurn(null);
        var tool = new GovernedTool(new FakeTool("write_file", MutationOk("/tmp/a.txt")), ctx,
            (_, _) => Task.FromResult(true), null, null);

        var result = await Invoke(tool, new { path = "/tmp/a.txt", content = "x" });

        Assert.Contains(NudgeMarker, result);
    }

    [Fact]
    public async Task OffMode_NudgeStillAppends()
    {
        var ctx = new GovernanceContext(GovernancePolicy.FromMode(GovernanceMode.Off));
        ctx.BeginTurn(null);
        var tool = Wrap(new FakeTool("write_file", MutationOk("/tmp/a.txt")), ctx);

        var result = await Invoke(tool, new { path = "/tmp/a.txt", content = "x" });

        Assert.Contains(NudgeMarker, result);
    }

    [Fact]
    public async Task BridgeTextResult_UnwrapsToPlainTextForModelAndUi()
    {
        // Regression guard for the isError plumbing: a plain bridge result must reach the model and
        // the UI callback as the same bare string as before the wrapper existed.
        var ctx        = CodingCtx();
        var completions = new List<string?>();
        var tool = new GovernedTool(new FakeTool("read_file", TextOk("file contents")), ctx,
            null, null, (_, text, _, _, _) => completions.Add(text));

        var result = await Invoke(tool, new { path = "/tmp/a.txt" });

        Assert.Equal("file contents", result);
        Assert.Equal("file contents", Assert.Single(completions));
    }
}
