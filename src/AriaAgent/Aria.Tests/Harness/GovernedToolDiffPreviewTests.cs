using System.Text.Json;
using Aria.Harness.Bridge;
using Aria.Harness.Governance;
using Microsoft.Extensions.AI;
using Xunit;

namespace Aria.Tests.Harness;

/// <summary>
/// Prospective diff in approvals: when a file-mutation call pauses for approval, GovernedTool
/// fetches the bridge's read-only preview and attaches the diff to the approval payload. The
/// fetch is bounded to the file-mutation set and fails open — an unavailable or erroring preview
/// leaves the approval on the plain args preview.
/// </summary>
public class GovernedToolDiffPreviewTests
{
    private const string SampleDiff = "--- a/a.txt\n+++ b/a.txt\n@@ -1,1 +1,1 @@\n-old\n+new\n";

    private sealed class FakePreviewTool : AIFunction, IDiffPreviewTool
    {
        private static readonly JsonElement _schema = JsonDocument.Parse("{}").RootElement;

        private readonly string? _diff;
        private readonly bool _throw;

        public FakePreviewTool(string name, string? diff, bool throwOnFetch = false)
        {
            Name = name;
            _diff = diff;
            _throw = throwOnFetch;
        }

        public int FetchCount { get; private set; }
        public override string Name { get; }
        public override string Description => "";
        public override JsonElement JsonSchema => _schema;

        public Task<string?> FetchDiffPreviewAsync(Dictionary<string, JsonElement> args, CancellationToken ct)
        {
            FetchCount++;
            if (_throw) throw new InvalidOperationException("bridge unreachable");
            return Task.FromResult(_diff);
        }

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => new("done");
    }

    // Strict mode gates every mutation behind an in-chat approval.
    private static GovernanceContext StrictCtx()
    {
        var ctx = new GovernanceContext(GovernancePolicy.FromMode(GovernanceMode.Strict));
        ctx.BeginTurn(null);
        return ctx;
    }

    private static AIFunctionArguments CallArgs(object o)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;
        return new AIFunctionArguments(dict.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
    }

    [Fact]
    public async Task ApprovalPause_FetchesAndAttachesPreviewDiff()
    {
        var inner = new FakePreviewTool("edit_file", SampleDiff);
        var approvals = new List<ActionDescriptor>();
        var tool = new GovernedTool(inner, StrictCtx(),
            (d, _) => { approvals.Add(d); return Task.FromResult(true); }, null, null);

        var result = await tool.InvokeAsync(CallArgs(new { path = "/tmp/a.txt", old_string = "old", new_string = "new" }));

        var d = Assert.Single(approvals);
        Assert.Equal(ToolSeverity.NeedsApproval, d.Severity);
        Assert.Equal(SampleDiff, d.Diff);
        Assert.Equal(1, inner.FetchCount);
        Assert.StartsWith("done", result?.ToString()); // approved → the real call ran (a post-mutation verify nudge may suffix the text)
    }

    [Fact]
    public async Task PreviewUnavailable_FallsBackToArgsPreview()
    {
        var inner = new FakePreviewTool("write_file", diff: null);
        var approvals = new List<ActionDescriptor>();
        var tool = new GovernedTool(inner, StrictCtx(),
            (d, _) => { approvals.Add(d); return Task.FromResult(true); }, null, null);

        await tool.InvokeAsync(CallArgs(new { path = "/tmp/a.txt", content = "x" }));

        var d = Assert.Single(approvals);
        Assert.Null(d.Diff); // approval card shows the args blob instead
        Assert.False(string.IsNullOrEmpty(d.ArgsPreview));
    }

    [Fact]
    public async Task PreviewThrows_FailOpen_ApprovalStillProceeds()
    {
        var inner = new FakePreviewTool("multi_edit", SampleDiff, throwOnFetch: true);
        var approvals = new List<ActionDescriptor>();
        var tool = new GovernedTool(inner, StrictCtx(),
            (d, _) => { approvals.Add(d); return Task.FromResult(true); }, null, null);

        var result = await tool.InvokeAsync(CallArgs(new
        {
            path = "/tmp/a.txt",
            edits = new[] { new { old_string = "a", new_string = "b" } },
        }));

        var d = Assert.Single(approvals);
        Assert.Null(d.Diff);
        Assert.StartsWith("done", result?.ToString()); // approved → the real call ran (a post-mutation verify nudge may suffix the text)
    }

    [Fact]
    public async Task NonPreviewableMutation_NoFetch_AttachesNoDiff()
    {
        var inner = new FakePreviewTool("delete_file", SampleDiff);
        var approvals = new List<ActionDescriptor>();
        var tool = new GovernedTool(inner, StrictCtx(),
            (d, _) => { approvals.Add(d); return Task.FromResult(true); }, null, null);

        await tool.InvokeAsync(CallArgs(new { path = "/tmp/a.txt" }));

        var d = Assert.Single(approvals);
        Assert.Equal(0, inner.FetchCount); // deletes keep the plain args preview
        Assert.Null(d.Diff);
    }

    [Fact]
    public async Task AllowedTool_NoApproval_NoFetch()
    {
        var inner = new FakePreviewTool("read_file", SampleDiff);
        var approvals = 0;
        var tool = new GovernedTool(inner, StrictCtx(),
            (_, _) => { approvals++; return Task.FromResult(true); }, null, null);

        var result = await tool.InvokeAsync(CallArgs(new { path = "/tmp/a.txt" }));

        Assert.Equal(0, approvals);
        Assert.Equal(0, inner.FetchCount);
        Assert.Equal("done", result?.ToString());
    }
}
