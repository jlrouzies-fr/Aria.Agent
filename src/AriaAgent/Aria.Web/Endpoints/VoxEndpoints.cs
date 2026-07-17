using Aria.Web.Services;
using Aria.Web.Services.ModelBridge;

namespace Aria.Web.Endpoints;

public static class VoxEndpoints
{
    public static WebApplication MapVoxEndpoints(this WebApplication app)
    {
        // ── Vox transcription endpoint ───────────────────────────────────────────────
        // Transcription posts audio through the given soul's node (using that node's Whisper key),
        // so `userId` must be soul-verified — the same node-signed check the UI relies on. Without
        // it, anyone past the coarse access gate could transcribe on another soul's node/key.
        app.MapPost("/api/vox/transcribe", async (
            HttpRequest request,
            string userId,
            string channelName,
            AgentService agentService,
            VoxService voxService,
            ModelBridgeRegistry registry) =>
        {
            if (string.IsNullOrWhiteSpace(userId) || !registry.IsSoulVerified(userId))
                return Results.Json(new { error = "Soul not verified — connect the owning bridge for this userId." },
                    statusCode: StatusCodes.Status403Forbidden);

            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart form" });

            var form = await request.ReadFormAsync();
            var file = form.Files["audio"];
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "No audio file received" });

            await using var stream = file.OpenReadStream();
            var (ok, text, error) = await voxService.TranscribeAsync(
                stream, file.FileName, channelName, userId, agentService);

            return ok
                ? Results.Ok(new { text })
                : Results.Ok(new { error });
        });

        return app;
    }
}
