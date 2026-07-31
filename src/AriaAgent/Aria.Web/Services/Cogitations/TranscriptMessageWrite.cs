namespace Aria.Web.Services.Cogitations;

/// <summary>
/// One message to write when replacing a cogitation's full transcript
/// (<see cref="CogitationService.ReplaceMessagesAsync"/> / bridge <c>/messages/replace</c>).
/// Shared by Compact (single summary) and edit-and-replay (kept prefix).
/// </summary>
public sealed record TranscriptMessageWrite(
    string Role,
    string Content,
    string? ThinkingContent = null,
    string? SectionsJson = null,
    string? ImageBase64 = null,
    string? ImageMediaType = null);
