namespace Aria.Harness.Bridge;

/// <summary>
/// A tool result that carries UI-only structured metadata alongside the text the model sees.
/// Used by bridge builtin file tools to pass diff / undo information to the chat UI.
/// </summary>
public sealed class FileMutationToolResult
{
    public required string Text { get; init; }
    public string? MetadataJson { get; init; }
}
