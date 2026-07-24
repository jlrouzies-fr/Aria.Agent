namespace Aria.Web.Services.Cogitations;

/// <summary>
/// A pending <c>ask_user</c> question awaiting the user's answer in chat. Held by whichever
/// sink hosts the gate (an attached Chat component or a detached <see cref="CogitationRun"/>)
/// while the tool call is paused; <see cref="AskUserPrompt.Options"/> is null/empty on the
/// free-text-only path.
/// </summary>
public sealed record AskUserPrompt(string Question, string[]? Options);
