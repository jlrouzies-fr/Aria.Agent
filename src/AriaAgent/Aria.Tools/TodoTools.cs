using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Aria.Tools;

/// <summary>
/// A single directive in the agent's task manifest. Rendered as a live checklist
/// in the chat UI and updated by the agent as work progresses.
/// </summary>
public sealed class TodoItem
{
    [Description("The directive — a short, imperative description of one step of the task.")]
    public string Text { get; set; } = "";

    [Description("Directive state. Exactly one of: pending, in_progress, completed.")]
    public string Status { get; set; } = "pending";
}

/// <summary>
/// The task-manifest tool. Always-on: lets the agent post and update an ordered
/// checklist of directives for multi-step work. Each call replaces the whole
/// manifest, so the agent always sends the complete list with updated statuses.
/// Execution is in-process (no node secret, pure UI coordination); the supplied
/// callback delivers the parsed manifest to the host for rendering.
/// </summary>
public static class TodoTools
{
    public static AITool Create(Action<IReadOnlyList<TodoItem>> onUpdate)
    {
        return AIFunctionFactory.Create(
            ([Description("The COMPLETE ordered manifest of directives. Always resend every directive each call with its full 'text', mutating only the 'status' fields — never send a partial list or status-only entries.")] TodoItem[] directives) =>
            {
                var items = directives ?? [];
                onUpdate(items);
                var done  = items.Count(d => string.Equals(d.Status, "completed", StringComparison.OrdinalIgnoreCase));
                return $"Manifest inscribed — {done}/{items.Length} directives complete.";
            },
            name: "update_task_manifest",
            description:
                "Post or update your task manifest: an ordered checklist of directives shown to the user. "
                + "Use it for any multi-step task — declare the directives up front, then call this again to mark "
                + "each 'in_progress' as you begin it and 'completed' as you finish, keeping at most one 'in_progress'. "
                + "Always send the entire manifest every call, with the full 'text' of every directive. "
                + "Each call REPLACES the whole manifest: when the user moves on to a new, separate task, send only "
                + "the new task's directives — never carry directives over from a previous task. "
                + "Do not use it for trivial single-step requests.");
    }
}
