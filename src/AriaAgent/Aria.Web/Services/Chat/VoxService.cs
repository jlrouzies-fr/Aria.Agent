using System.Text.Json;
using Aria.Agent;
using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.Chat;

public class VoxService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<UserVoxSettings?> GetSettingsAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.UserVoxSettings.FirstOrDefaultAsync(v => v.UserId == userId);
    }

    public async Task SaveSettingsAsync(string userId, string? transcriptionChannelName, string? fixingChannelName)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var s = await db.UserVoxSettings.FirstOrDefaultAsync(v => v.UserId == userId);
        if (s == null) { s = new UserVoxSettings { UserId = userId }; db.UserVoxSettings.Add(s); }
        s.TranscriptionChannelName = transcriptionChannelName;
        s.FixingChannelName        = fixingChannelName;
        await db.SaveChangesAsync();
    }

    public async Task<(bool Ok, string? Text, string? Error)> TranscribeAsync(
        Stream audio, string filename, string channelName, string userId, AgentService agentService)
    {
        var source = agentService.AvailableModelSources.FirstOrDefault(s => s.Name == channelName);
        if (source == null)
            return (false, null, $"Channel '{channelName}' not found");

        var (url, model) = GetTranscriptionEndpoint(source);
        if (url == null)
            return (false, null, $"{channelName} does not support audio transcription — configure OpenAI or Groq");

        // Cloud (Whisper) keys now live on the bridge and are never handed to the server, so
        // server-side cloud transcription is unavailable by design. Browser speech recognition
        // remains the default path; routing Whisper through the bridge is a possible follow-up.
        string? apiKey = null;

        if (string.IsNullOrEmpty(apiKey))
            return (false, null, source.IsPublicProvider
                ? "Cloud transcription is unavailable — your API keys are held on your bridge, not the server. Use browser speech recognition."
                : "No API key configured for this channel");

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            http.Timeout = TimeSpan.FromSeconds(30);

            using var form = new MultipartFormDataContent();
            var audioContent = new StreamContent(audio);
            audioContent.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("audio/webm");
            form.Add(audioContent, "file", filename);
            form.Add(new StringContent(model!), "model");

            using var resp = await http.PostAsync(url, form);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return (false, null, $"Transcription API error {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.GetProperty("text").GetString() ?? "";
            return (true, text.Trim(), null);
        }
        catch (Exception ex)
        {
            return (false, null, $"Transcription failed: {ex.Message}");
        }
    }

    private static (string? url, string? model) GetTranscriptionEndpoint(ModelSource source) =>
        source.Name switch
        {
            "OpenAI" => ($"{source.Url.TrimEnd('/')}/audio/transcriptions", "whisper-1"),
            "Groq"   => ($"{source.Url.TrimEnd('/')}/audio/transcriptions", "whisper-large-v3"),
            _        => (null, null)
        };
}
