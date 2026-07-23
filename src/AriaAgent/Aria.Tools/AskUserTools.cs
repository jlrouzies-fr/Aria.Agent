using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Aria.Tools;

/// <summary>
/// The structured user-question tool. Always-on when the host wires an ask-and-wait
/// callback (interactive chat sessions only — headless runs leave it null and the tool
/// is simply absent). Lets the agent pause mid-run and ask the user instead of guessing:
/// the callback surfaces the question in chat (option buttons when options are given, a
/// free-text path always) and returns the user's answer, or null on timeout/skip — which
/// becomes a clear "proceed with your best judgment" result rather than a failed call.
/// Execution is in-process (pure UI coordination); the tool is not mutating, so every
/// governance mode including Plan lets it run (it is how the agent presents choices).
/// </summary>
public static class AskUserTools
{
    /// <summary>Upper bound on the number of option buttons offered alongside the free-text path.</summary>
    public const int MaxOptions = 4;

    /// <summary>Tool result when the user never answered (timeout or explicit skip).</summary>
    public const string NoAnswer =
        "The user did not answer (timed out or skipped). Proceed with your best judgment, " +
        "state the assumption you chose, and continue.";

    public static AITool Create(Func<string, string[]?, CancellationToken, Task<string?>> askAndWait)
    {
        return AIFunctionFactory.Create(
            async ([Description("The question to ask the user. Be specific and self-contained.")] string question,
                   [Description("Optional answer choices presented as buttons (at most 4). The user can always type a different answer instead, so do not add an 'Other' option.")] string[]? options = null,
                   CancellationToken ct = default) =>
            {
                if (string.IsNullOrWhiteSpace(question))
                    return "ERROR: 'question' must not be empty.";
                if (options is { Length: > MaxOptions })
                    return $"ERROR: at most {MaxOptions} options are allowed (got {options.Length}). " +
                           "Retry with fewer, broader options.";

                var answer = await askAndWait(
                    question,
                    options is { Length: > 0 } ? options : null,
                    ct);
                return string.IsNullOrWhiteSpace(answer) ? NoAnswer : answer;
            },
            name: "ask_user",
            description:
                "Ask the user a question mid-task and wait for their answer, instead of guessing. "
                + "Use it when a decision genuinely depends on the user's intent or preference and getting it "
                + "wrong would waste work — not for things you can reasonably infer. Pass up to "
                + $"{MaxOptions} short options to present as buttons; the user can always answer with free "
                + "text instead. Their chosen option or typed answer is returned as the result. If they do "
                + "not answer, proceed with your best judgment.");
    }
}
