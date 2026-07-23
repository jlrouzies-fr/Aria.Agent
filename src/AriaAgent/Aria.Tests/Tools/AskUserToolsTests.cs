using Aria.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Aria.Tests.Tools;

/// <summary>
/// Covers the <c>ask_user</c> tool's Harness-side plumbing: registration, argument validation
/// (free-text path when no options, the 4-option cap), the answer flowing back as the tool
/// result, and the timeout/skip text when the host's ask callback reports no answer. The
/// Blazor bar itself is not tested here — the callback IS the seam.
/// </summary>
public class AskUserToolsTests
{
    private static Task<object?> Invoke(AITool tool, IDictionary<string, object?> args) =>
        ((AIFunction)tool).InvokeAsync(new AIFunctionArguments(args)).AsTask();

    private static Func<string, string[]?, CancellationToken, Task<string?>> Returning(
        string? answer,
        Action<string, string[]?>? capture = null) =>
        (q, o, _) =>
        {
            capture?.Invoke(q, o);
            return Task.FromResult(answer);
        };

    [Fact]
    public void Registration_ExposesExpectedName_AndOnlyQuestionRequired()
    {
        var tool = (AIFunction)AskUserTools.Create(Returning("x"));
        Assert.Equal("ask_user", tool.Name);

        // options must be OPTIONAL in the schema — the free-text path sends question only.
        var required = tool.JsonSchema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Equal(["question"], required);
    }

    [Fact]
    public async Task NoOptions_FreeTextPath_CallbackGetsNullOptions()
    {
        string? gotQuestion = null;
        string[]? gotOptions = ["sentinel"];
        var tool = AskUserTools.Create(Returning("typed answer", (q, o) => { gotQuestion = q; gotOptions = o; }));

        var result = await Invoke(tool, new Dictionary<string, object?> { ["question"] = "Which file?" });

        Assert.Equal("Which file?", gotQuestion);
        Assert.Null(gotOptions);                    // free-text path: no option buttons
        Assert.Equal("typed answer", result?.ToString());       // the answer IS the tool result
    }

    [Fact]
    public async Task Options_PassedThrough_ChosenOptionFlowsBackAsResult()
    {
        string[]? gotOptions = null;
        var tool = AskUserTools.Create(Returning("blue", (_, o) => gotOptions = o));

        var result = await Invoke(tool, new Dictionary<string, object?>
        {
            ["question"] = "Which colour?",
            ["options"]  = new[] { "red", "green", "blue" },
        });

        Assert.Equal(new[] { "red", "green", "blue" }, gotOptions);
        Assert.Equal("blue", result?.ToString());
    }

    [Fact]
    public async Task MoreThanFourOptions_Rejected_CallbackNotInvoked()
    {
        var invoked = false;
        var tool = AskUserTools.Create(Returning("x", (_, _) => invoked = true));

        var result = await Invoke(tool, new Dictionary<string, object?>
        {
            ["question"] = "Pick one?",
            ["options"]  = new[] { "a", "b", "c", "d", "e" },
        });

        Assert.False(invoked);
        Assert.Contains("at most 4", result?.ToString());
    }

    [Fact]
    public async Task NoAnswer_TimeoutOrSkip_ReturnsBestJudgmentText()
    {
        var tool = AskUserTools.Create(Returning(null));

        var result = await Invoke(tool, new Dictionary<string, object?> { ["question"] = "Continue?" });

        Assert.Equal(AskUserTools.NoAnswer, result?.ToString());
        Assert.Contains("best judgment", result?.ToString());
    }

    [Fact]
    public async Task EmptyQuestion_Rejected_CallbackNotInvoked()
    {
        var invoked = false;
        var tool = AskUserTools.Create(Returning("x", (_, _) => invoked = true));

        var result = await Invoke(tool, new Dictionary<string, object?> { ["question"] = "  " });

        Assert.False(invoked);
        Assert.Contains("question", result?.ToString());
    }
}
