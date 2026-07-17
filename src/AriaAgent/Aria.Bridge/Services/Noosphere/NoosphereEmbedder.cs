using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aria.Bridge.Services.Noosphere;

// Calls an OpenAI-compatible /v1/embeddings endpoint. Vectors are stored as float32 blobs in
// SQLite (durable storage only — see NoosphereService for the in-memory SIMD cosine search).
public class NoosphereEmbedder(
    NoosphereConfigService configService,
    IOptions<NoosphereOptions> legacyOptions,
    IServiceScopeFactory scopeFactory,
    ILogger<NoosphereEmbedder> logger)
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // Fast synchronous toggle check (skip the vector leg without I/O). Whether a channel actually
    // resolves (explicit URL or SyncedLocalSources fallback) is checked async in EmbedBatchAsync.
    public bool Enabled => configService.GetConfigAsync(default).GetAwaiter().GetResult().EmbeddingsEnabled;

    // Reason the most recent EmbedBatchAsync call failed (null if it succeeded, or none has run yet).
    // Read by BuiltinTools.Memory to turn a silent vector-leg degradation into an actual error message
    // in the tool's response — e.g. LM Studio's own "model not found" text — instead of a generic note.
    public string? LastError { get; private set; }
    public DateTime? LastErrorAt { get; private set; }

    public record EmbedResult(List<float[]> Vectors, string Model);

    public async Task<EmbedResult?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var opts = await GetEmbeddingOptionsAsync(ct);
        if (!opts.Enabled || texts.Count == 0) return null;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var channel = await NoosphereChannelResolver.ResolveAsync(opts, db, ct);
        if (channel == null) return null;

        try
        {
            var payload = JsonSerializer.Serialize(new { model = channel.Model, input = texts });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{channel.Url}/embeddings");
            if (!string.IsNullOrEmpty(channel.Key))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", channel.Key);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Fail($"HTTP {(int)resp.StatusCode}: {NoosphereHttpError.ExtractMessage(body)}");
                logger.LogWarning(
                    "Embeddings request to {Url}/embeddings failed with {Status}: {Body}. Falling back to FTS/graph-only probe.",
                    channel.Url, (int)resp.StatusCode, Truncate(body));
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            // Some OpenAI-compatible servers return 200 for a request they don't actually support
            // (e.g. an unsupported endpoint on a model that can't embed, or a rejected/missing API key) —
            // the status code alone can't be trusted, so a response that doesn't actually contain
            // embeddings is logged and treated as a failure rather than silently degrading unnoticed.
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                Fail($"'{channel.Model}' on {channel.Url} answered but returned no embeddings — it may not be an embedding model, or the request was rejected silently.");
                logger.LogWarning(
                    "Embeddings response from {Url}/embeddings had HTTP {Status} but no 'data' array — " +
                    "likely an unsupported endpoint or rejected auth reporting success anyway. Body: {Body}",
                    channel.Url, (int)resp.StatusCode, Truncate(body));
                return null;
            }

            var vectors = new List<float[]>();
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("embedding", out var emb) || emb.ValueKind != JsonValueKind.Array)
                {
                    Fail($"'{channel.Model}' on {channel.Url} returned a malformed embeddings response.");
                    logger.LogWarning(
                        "Embeddings response from {Url}/embeddings had a 'data' entry with no 'embedding' array. Body: {Body}",
                        channel.Url, Truncate(body));
                    return null;
                }
                var vec = new float[emb.GetArrayLength()];
                var i = 0;
                foreach (var e in emb.EnumerateArray()) vec[i++] = e.GetSingle();
                vectors.Add(vec);
            }
            LastError = null;
            return new EmbedResult(vectors, channel.Model);
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            logger.LogWarning(ex, "Embeddings request to {Url}/embeddings threw — unreachable or misconfigured. Falling back to FTS/graph-only probe.", channel.Url);
            return null;
        }
    }

    private void Fail(string reason) { LastError = reason; LastErrorAt = DateTime.UtcNow; }

    private async Task<NoosphereEmbeddingOptions> GetEmbeddingOptionsAsync(CancellationToken ct)
    {
        var configured = await configService.GetEmbeddingOptionsAsync(ct);
        if (!string.IsNullOrWhiteSpace(configured.Url))
            return configured;

        // No channel selected in the UI yet — fall back to legacy appsettings so existing installs keep working.
        return legacyOptions.Value.Embeddings;
    }

    public static byte[] Encode(float[] vec) => MemoryMarshal.AsBytes(vec.AsSpan()).ToArray();
    public static float[] Decode(byte[] blob) => MemoryMarshal.Cast<byte, float>(blob).ToArray();

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "…";
}
