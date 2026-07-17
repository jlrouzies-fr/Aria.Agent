using Aria.Bridge.Services.Speech;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// On-device (offline) voice transcription. The browser records raw 16 kHz mono WAV and POSTs it
/// straight here; whisper.cpp runs locally and returns text. No cloud, no API key, no server.
/// </summary>
public static class LocalWhisperEndpoints
{
    public static void MapLocalWhisperEndpoints(this WebApplication app)
    {
        // State of every offered model (downloaded? downloading? progress?).
        app.MapGet("/transcribe/local/status", (LocalWhisperService svc) => Results.Ok(svc.Status()));

        // Begin a background download of a model size. Poll /status for progress.
        app.MapPost("/transcribe/local/download", (string size, LocalWhisperService svc) =>
        {
            if (LocalWhisperService.Lookup(size) is null)
                return Results.BadRequest(new { error = $"Unknown model size '{size}'" });
            svc.StartDownload(size);
            return Results.Ok(new { started = true });
        });

        // Remove a downloaded model to reclaim disk.
        app.MapDelete("/transcribe/local/model", (string size, LocalWhisperService svc) =>
            Results.Ok(new { deleted = svc.DeleteModel(size) }));

        // Transcribe an uploaded WAV with the chosen model. Returns { text } or { error }.
        app.MapPost("/transcribe/local", async (HttpRequest req, string size, LocalWhisperService svc) =>
        {
            if (!req.HasFormContentType) return Results.BadRequest(new { error = "multipart form required" });
            var form = await req.ReadFormAsync();
            var file = form.Files["audio"];
            if (file == null || file.Length == 0) return Results.Ok(new { error = "no audio" });

            // Buffer to a seekable stream — Whisper.net's WAV parser seeks over the header.
            using var ms = new MemoryStream();
            await using (var upload = file.OpenReadStream())
                await upload.CopyToAsync(ms);
            ms.Position = 0;

            var (ok, text, error) = await svc.TranscribeAsync(size, ms);
            return Results.Ok(ok ? new { text } : new { error });
        });
    }
}
