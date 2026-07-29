namespace Aria.Harness.Bridge;

/// <summary>
/// A tool result that carries UI-only structured metadata alongside the text the model sees.
/// Used by bridge builtin file tools to pass diff / undo information to the chat UI.
/// </summary>
public sealed class FileMutationToolResult
{
    public required string Text { get; init; }
    public string? MetadataJson { get; init; }

    /// <summary>The bridge's own failure flag (ToolCallResponse.IsError) — the text alone can't
    /// carry it (bridge errors have no uniform prefix). GovernedTool reads it for the
    /// post-mutation verify nudge; the model sees only <see cref="Text"/>.</summary>
    public bool IsError { get; init; }
}
