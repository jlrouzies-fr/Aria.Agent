using System.Collections.Concurrent;
using Aria.Bridge.Services.Logging;
using Whisper.net;

namespace Aria.Bridge.Services.Speech;

/// <summary>
/// On-device speech-to-text via whisper.cpp (Whisper.net). Everything — the model file and the
/// audio — stays on the user's machine; the server is never involved. Models are downloaded once
/// from Hugging Face into the bridge's app-data dir, then reused fully offline.
/// </summary>
public sealed class LocalWhisperService : IDisposable
{
    // Catalog of offered model sizes → (GGML file name, Hugging Face URL, approx download size).
    // URLs are the canonical whisper.cpp GGML weights published by ggerganov.
    public sealed record ModelInfo(string Size, string Label, string FileName, string Url, long ApproxBytes);

    private static readonly IReadOnlyList<ModelInfo> Catalog =
    [
        new("tiny",   "Tiny",   "ggml-tiny.bin",   "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin",     77_000_000L),
        new("base",   "Base",   "ggml-base.bin",   "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",    148_000_000L),
        new("small",  "Small",  "ggml-small.bin",  "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",   488_000_000L),
        new("medium", "Medium", "ggml-medium.bin", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin", 1_530_000_000L),
    ];

    // Live download progress per size (0..100); presence = a download is in flight.
    private readonly ConcurrentDictionary<string, int>    _progress = new();
    private readonly ConcurrentDictionary<string, string> _errors   = new();
    // Loaded whisper factories, cached per size (model load is heavy; reuse across transcriptions).
    private readonly ConcurrentDictionary<string, WhisperFactory> _factories = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private static readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string _modelsDir;

    public LocalWhisperService()
    {
        // Sit beside the vault in per-user app data, so a bridge reinstall doesn't wipe the models.
        _modelsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "aria-bridge", "whisper-models");
        Directory.CreateDirectory(_modelsDir);
    }

    private string PathFor(ModelInfo m) => Path.Combine(_modelsDir, m.FileName);

    public static ModelInfo? Lookup(string size) =>
        Catalog.FirstOrDefault(m => string.Equals(m.Size, size, StringComparison.OrdinalIgnoreCase));

    /// <summary>Snapshot of every model's state for the UI.</summary>
    public object Status() => new
    {
        models = Catalog.Select(m =>
        {
            var path = PathFor(m);
            var onDisk = File.Exists(path);
            _progress.TryGetValue(m.Size, out var pct);
            _errors.TryGetValue(m.Size, out var err);
            return new
            {
                size        = m.Size,
                label       = m.Label,
                approxBytes = m.ApproxBytes,
                downloaded  = onDisk,
                downloading = _progress.ContainsKey(m.Size),
                progress    = pct,
                error       = err
            };
        }).ToArray()
    };

    /// <summary>
    /// Kick off a background download for the given size. No-op if already present or in flight.
    /// Progress is polled via <see cref="Status"/>.
    /// </summary>
    public void StartDownload(string size)
    {
        var m = Lookup(size);
        if (m == null) return;
        if (File.Exists(PathFor(m))) return;
        // Reserve the slot atomically so concurrent clicks don't launch two downloads.
        if (!_progress.TryAdd(m.Size, 0)) return;
        _errors.TryRemove(m.Size, out _);
        _ = Task.Run(() => DownloadAsync(m));
    }

    private async Task DownloadAsync(ModelInfo m)
    {
        var finalPath = PathFor(m);
        var tmpPath   = finalPath + ".part";
        try
        {
            using var resp = await _http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? m.ApproxBytes;

            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(tmpPath))
            {
                var buffer = new byte[1 << 16];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    _progress[m.Size] = (int)Math.Min(100, read * 100 / Math.Max(1, total));
                }
            }
            File.Move(tmpPath, finalPath, overwrite: true);
            BridgeLogger.Log("INFO", $"Whisper model '{m.Size}' downloaded ({m.FileName}).");
        }
        catch (Exception ex)
        {
            _errors[m.Size] = ex.Message;
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
            BridgeLogger.Log("ERROR", $"Whisper model '{m.Size}' download failed: {ex.Message}");
        }
        finally
        {
            _progress.TryRemove(m.Size, out _);
        }
    }

    public bool DeleteModel(string size)
    {
        var m = Lookup(size);
        if (m == null) return false;
        if (_factories.TryRemove(m.Size, out var f)) f.Dispose();
        var path = PathFor(m);
        if (File.Exists(path)) { File.Delete(path); return true; }
        return false;
    }

    private async Task<WhisperFactory> GetFactoryAsync(ModelInfo m)
    {
        if (_factories.TryGetValue(m.Size, out var cached)) return cached;
        await _loadLock.WaitAsync();
        try
        {
            if (_factories.TryGetValue(m.Size, out cached)) return cached;
            var factory = WhisperFactory.FromPath(PathFor(m));
            _factories[m.Size] = factory;
            return factory;
        }
        finally { _loadLock.Release(); }
    }

    /// <summary>
    /// Transcribe a 16 kHz mono WAV stream with the given model size. Returns the joined text.
    /// </summary>
    public async Task<(bool Ok, string? Text, string? Error)> TranscribeAsync(string size, Stream wav)
    {
        var m = Lookup(size);
        if (m == null) return (false, null, $"Unknown model size '{size}'");
        if (!File.Exists(PathFor(m)))
            return (false, null, $"Model '{size}' is not downloaded on this node");

        try
        {
            var factory = await GetFactoryAsync(m);
            await using var processor = factory.CreateBuilder().WithLanguage("auto").Build();

            var sb = new System.Text.StringBuilder();
            await foreach (var seg in processor.ProcessAsync(wav))
                sb.Append(seg.Text);
            return (true, CleanTranscript(sb.ToString()), null);
        }
        catch (Exception ex)
        {
            return (false, null, $"Local transcription error: {ex.Message}");
        }
    }

    // whisper.cpp emits bracketed non-speech annotations for silence/noise (e.g. "[BLANK_AUDIO]",
    // "[ Silence ]", "[MUSIC]", "(applause)"). Strip them so an empty/silent clip yields "" rather
    // than a literal tag that would otherwise be fed into the LLM text-fixing channel.
    private static string CleanTranscript(string raw)
    {
        // Remove every square-bracket group (whisper's brackets are always annotations, never speech).
        var text = System.Text.RegularExpressions.Regex.Replace(raw, @"\[[^\]]*\]", " ");
        // Remove known parenthetical annotations only (parenthesised words can be real speech).
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\(\s*(silence|blank[_ ]?audio|music|inaudible|noise|pause|applause|laughter)\s*\)",
            " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Collapse whitespace left behind.
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    }

    public void Dispose()
    {
        foreach (var f in _factories.Values) f.Dispose();
        _loadLock.Dispose();
    }
}
