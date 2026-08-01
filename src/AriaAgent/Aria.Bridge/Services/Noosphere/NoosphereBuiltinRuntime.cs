using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Aria.Bridge.Services.Logging;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Aria.Bridge.Services.Noosphere;

/// <summary>
/// Opt-in on-node Noosphere models (MiniLM ONNX embeddings + LFM2.5 GGUF extraction). Mirrors
/// <see cref="Speech.LocalWhisperService"/>: catalog download into app-data, progress poll, SHA256
/// verify, lazy load. Never involves Aria.Web.
/// </summary>
public sealed class NoosphereBuiltinRuntime : IDisposable
{
    private readonly ConcurrentDictionary<string, int> _progress = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    // SHA256 of a ~731 MB GGUF is multi-second — cache by (path, length, mtime) so Status polls and
    // IsBuiltinActive checks don't re-hash on every Memory-tab open / 2.5s loaded poll.
    private readonly ConcurrentDictionary<string, (long Length, DateTime MtimeUtc)> _verifiedCache = new(StringComparer.Ordinal);
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string _modelsDir;
    private readonly SemaphoreSlim _extractLock = new(1, 1);
    private readonly SemaphoreSlim _embedLock = new(1, 1);

    private LLamaWeights? _extractWeights;
    private ModelParams? _extractParams;
    private InferenceSession? _embedSession;
    private BertTokenizer? _embedTokenizer;

    public NoosphereBuiltinRuntime()
    {
        _modelsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "aria-bridge", "noosphere-models");
        Directory.CreateDirectory(_modelsDir);
    }

    /// <summary>Test seam — point the runtime at a temp catalog dir.</summary>
    internal NoosphereBuiltinRuntime(string modelsDir)
    {
        _modelsDir = modelsDir;
        Directory.CreateDirectory(_modelsDir);
    }

    private string PathFor(NoosphereBuiltinCatalog.ModelFile f) => Path.Combine(_modelsDir, f.FileName);

    /// <summary>Catalog-pinned SHA check with mtime/length cache — safe for frequent Status polls.</summary>
    private bool IsFileVerified(NoosphereBuiltinCatalog.ModelFile f)
    {
        var path = PathFor(f);
        if (!File.Exists(path))
        {
            _verifiedCache.TryRemove(path, out _);
            return false;
        }

        var fi = new FileInfo(path);
        var mtime = fi.LastWriteTimeUtc;
        var length = fi.Length;
        if (_verifiedCache.TryGetValue(path, out var cached)
            && cached.Length == length
            && cached.MtimeUtc == mtime)
            return true;

        if (!VerifyFileSha256(path, f.Sha256Hex))
        {
            _verifiedCache.TryRemove(path, out _);
            return false;
        }

        _verifiedCache[path] = (length, mtime);
        return true;
    }

    public bool IsRoleOnDisk(string role)
    {
        var info = NoosphereBuiltinCatalog.Lookup(role);
        if (info == null) return false;
        return info.Files.All(IsFileVerified);
    }

    public bool IsReady =>
        IsRoleOnDisk(NoosphereBuiltinCatalog.RoleExtract)
        && IsRoleOnDisk(NoosphereBuiltinCatalog.RoleEmbed);

    /// <summary>True when the role's weights/session are resident in process memory.</summary>
    public bool IsRoleLoaded(string role)
    {
        if (string.Equals(role, NoosphereBuiltinCatalog.RoleExtract, StringComparison.OrdinalIgnoreCase))
            return _extractWeights != null;
        if (string.Equals(role, NoosphereBuiltinCatalog.RoleEmbed, StringComparison.OrdinalIgnoreCase))
            return _embedSession != null && _embedTokenizer != null;
        return false;
    }

    public object Status(bool enabled, DateTime? licenseAcceptedAt) => new
    {
        enabled,
        licenseAccepted = licenseAcceptedAt != null,
        licenseAcceptedAt,
        ready = enabled && IsReady,
        anyLoaded = IsRoleLoaded(NoosphereBuiltinCatalog.RoleExtract)
            || IsRoleLoaded(NoosphereBuiltinCatalog.RoleEmbed),
        roles = NoosphereBuiltinCatalog.Roles.Select(r =>
        {
            _progress.TryGetValue(r.Role, out var pct);
            _errors.TryGetValue(r.Role, out var err);
            var files = r.Files.Select(f =>
            {
                var present = File.Exists(PathFor(f));
                return new
                {
                    fileName = f.FileName,
                    present,
                    verified = present && IsFileVerified(f)
                };
            }).ToArray();
            var onDisk = files.Length > 0 && files.All(x => x.verified);
            return new
            {
                role = r.Role,
                label = r.Label,
                license = r.License,
                approxBytes = r.Files.Sum(f => f.ApproxBytes),
                downloaded = onDisk,
                loaded = IsRoleLoaded(r.Role),
                downloading = _progress.ContainsKey(r.Role),
                progress = pct,
                error = err,
                files
            };
        }).ToArray()
    };

    public string? StartDownload(string role, bool licenseAccepted)
    {
        var info = NoosphereBuiltinCatalog.Lookup(role);
        if (info == null) return $"Unknown role '{role}'";
        if (string.Equals(role, NoosphereBuiltinCatalog.RoleExtract, StringComparison.OrdinalIgnoreCase)
            && !licenseAccepted)
            return "Accept the LFM Open License before downloading the extraction model.";
        if (IsRoleOnDisk(role)) return null;
        if (!_progress.TryAdd(info.Role, 0)) return null; // already in flight
        _errors.TryRemove(info.Role, out _);
        _ = Task.Run(() => DownloadRoleAsync(info));
        return null;
    }

    private async Task DownloadRoleAsync(NoosphereBuiltinCatalog.RoleInfo role)
    {
        try
        {
            var totalApprox = role.Files.Sum(f => f.ApproxBytes);
            long doneBytes = 0;
            foreach (var file in role.Files)
            {
                var finalPath = PathFor(file);
                if (File.Exists(finalPath) && VerifyFileSha256(finalPath, file.Sha256Hex))
                {
                    doneBytes += file.ApproxBytes;
                    _progress[role.Role] = (int)Math.Min(100, doneBytes * 100 / Math.Max(1, totalApprox));
                    continue;
                }

                var tmpPath = finalPath + ".part";
                using var resp = await Http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? file.ApproxBytes;

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
                        // Scale this file's contribution by approx size so multi-file roles report smoothly.
                        var scaled = doneBytes + (long)(read * (double)file.ApproxBytes / Math.Max(1, total));
                        _progress[role.Role] = (int)Math.Min(99, scaled * 100 / Math.Max(1, totalApprox));
                    }
                }

                if (!VerifyFileSha256(tmpPath, file.Sha256Hex))
                {
                    try { File.Delete(tmpPath); } catch { /* best effort */ }
                    throw new InvalidOperationException(
                        $"SHA256 mismatch for {file.FileName} — refusing to install (catalog pin failed).");
                }

                File.Move(tmpPath, finalPath, overwrite: true);
                // We just verified the .part — seed the cache so the next Status poll is instant.
                var fi = new FileInfo(finalPath);
                _verifiedCache[finalPath] = (fi.Length, fi.LastWriteTimeUtc);
                doneBytes += file.ApproxBytes;
                _progress[role.Role] = (int)Math.Min(100, doneBytes * 100 / Math.Max(1, totalApprox));
            }

            BridgeLogger.Log("INFO", $"Noosphere builtin '{role.Role}' downloaded ({role.Label}).");
        }
        catch (Exception ex)
        {
            _errors[role.Role] = ex.Message;
            BridgeLogger.Log("ERROR", $"Noosphere builtin '{role.Role}' download failed: {ex.Message}");
        }
        finally
        {
            _progress.TryRemove(role.Role, out _);
        }
    }

    public bool DeleteModel(string role)
    {
        var info = NoosphereBuiltinCatalog.Lookup(role);
        if (info == null) return false;

        UnloadModel(role);

        var deleted = false;
        foreach (var f in info.Files)
        {
            var path = PathFor(f);
            _verifiedCache.TryRemove(path, out _);
            if (!File.Exists(path)) continue;
            File.Delete(path);
            deleted = true;
        }
        return deleted;
    }

    /// <summary>
    /// Drop an in-RAM model. Files on disk stay; next Inscribe/Probe reloads. No-op if cold.
    /// </summary>
    public bool UnloadModel(string role)
    {
        if (string.Equals(role, NoosphereBuiltinCatalog.RoleExtract, StringComparison.OrdinalIgnoreCase))
        {
            _extractLock.Wait();
            try
            {
                var was = _extractWeights != null;
                UnloadExtract();
                if (was) BridgeLogger.Log("INFO", "Noosphere builtin extraction model unloaded.");
                return true;
            }
            finally { _extractLock.Release(); }
        }

        if (string.Equals(role, NoosphereBuiltinCatalog.RoleEmbed, StringComparison.OrdinalIgnoreCase))
        {
            _embedLock.Wait();
            try
            {
                var was = _embedSession != null;
                UnloadEmbed();
                if (was) BridgeLogger.Log("INFO", "Noosphere builtin embedding model unloaded.");
                return true;
            }
            finally { _embedLock.Release(); }
        }

        return false;
    }

    public void UnloadAll()
    {
        UnloadModel(NoosphereBuiltinCatalog.RoleExtract);
        UnloadModel(NoosphereBuiltinCatalog.RoleEmbed);
    }

    internal static bool VerifyFileSha256(string path, string expectedHex)
    {
        if (!File.Exists(path)) return false;
        using var fs = File.OpenRead(path);
        var hash = SHA256.HashData(fs);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(hex, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    // ── Inference ────────────────────────────────────────────────────────────

    /// <param name="prefillJsonObject">
    /// When true, seeds the assistant turn with <c>{</c> so a 1.2B model stays on a JSON object
    /// (Liquid's recommended structured-output prefill). Caller must treat the returned text as a
    /// full object (leading brace is restored).
    /// </param>
    public async Task<(string? Text, string? Error)> CompleteChatAsync(
        string systemPrompt, string userContent, double temperature, int maxTokens, CancellationToken ct,
        bool prefillJsonObject = false)
    {
        if (!IsRoleOnDisk(NoosphereBuiltinCatalog.RoleExtract))
            return (null, "Built-in extraction model is not downloaded on this node.");

        try
        {
            await EnsureExtractLoadedAsync(ct);
            var weights = _extractWeights!;
            var modelParams = _extractParams!;

            // LFM2.5 requires <|startoftext|> before the first turn — omitting it degrades instruct
            // following (we saw valid-looking JSON with no usable facts).
            var prompt =
                "<|startoftext|><|im_start|>system\n" + systemPrompt + "<|im_end|>\n" +
                "<|im_start|>user\n" + userContent + "<|im_end|>\n" +
                "<|im_start|>assistant\n" + (prefillJsonObject ? "{" : "");

            var executor = new StatelessExecutor(weights, modelParams);
            var inf = new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = ["<|im_end|>", "<|startoftext|>"],
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = (float)temperature }
            };

            var sb = new StringBuilder();
            await foreach (var piece in executor.InferAsync(prompt, inf).WithCancellation(ct))
                sb.Append(piece);

            var text = sb.ToString().Trim();
            if (text.EndsWith("<|im_end|>", StringComparison.Ordinal))
                text = text[..^"<|im_end|>".Length].Trim();
            if (prefillJsonObject && !text.StartsWith('{'))
                text = "{" + text;
            return (text, null);
        }
        catch (Exception ex)
        {
            // NoosphereExtractor.Fail logs the Event Log line; keep this return for the call chain.
            return (null, ex.Message);
        }
    }

    public async Task<(List<float[]>? Vectors, string? Error)> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (!IsRoleOnDisk(NoosphereBuiltinCatalog.RoleEmbed))
            return (null, "Built-in embedding model is not downloaded on this node.");
        if (texts.Count == 0) return ([], null);

        try
        {
            await EnsureEmbedLoadedAsync(ct);
            var session = _embedSession!;
            var tokenizer = _embedTokenizer!;
            var vectors = new List<float[]>(texts.Count);
            foreach (var text in texts)
            {
                ct.ThrowIfCancellationRequested();
                vectors.Add(EmbedOne(session, tokenizer, text));
            }
            return (vectors, null);
        }
        catch (Exception ex)
        {
            // NoosphereEmbedder.Fail logs the Event Log line; keep this return for the call chain.
            return (null, ex.Message);
        }
    }

    private async Task EnsureExtractLoadedAsync(CancellationToken ct)
    {
        if (_extractWeights != null) return;
        await _extractLock.WaitAsync(ct);
        try
        {
            if (_extractWeights != null) return;
            var info = NoosphereBuiltinCatalog.Lookup(NoosphereBuiltinCatalog.RoleExtract)!;
            var path = PathFor(info.Files[0]);
            if (!IsFileVerified(info.Files[0]))
                throw new InvalidOperationException("Built-in extraction model failed SHA256 verification.");

            var parameters = new ModelParams(path)
            {
                ContextSize = 4096,
                GpuLayerCount = 0
            };
            _extractWeights = await Task.Run(() => LLamaWeights.LoadFromFile(parameters), ct);
            _extractParams = parameters;
            BridgeLogger.Log("INFO", "Noosphere builtin extraction model loaded.");
        }
        finally { _extractLock.Release(); }
    }

    private async Task EnsureEmbedLoadedAsync(CancellationToken ct)
    {
        if (_embedSession != null && _embedTokenizer != null) return;
        await _embedLock.WaitAsync(ct);
        try
        {
            if (_embedSession != null && _embedTokenizer != null) return;
            var info = NoosphereBuiltinCatalog.Lookup(NoosphereBuiltinCatalog.RoleEmbed)!;
            var onnx = info.Files.First(f => f.FileName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));
            var vocab = info.Files.First(f => f.FileName.Equals("vocab.txt", StringComparison.OrdinalIgnoreCase));
            if (!IsFileVerified(onnx) || !IsFileVerified(vocab))
                throw new InvalidOperationException("Built-in embedding model failed SHA256 verification.");

            await Task.Run(() =>
            {
                _embedSession = new InferenceSession(PathFor(onnx));
                using var vocabStream = File.OpenRead(PathFor(vocab));
                _embedTokenizer = BertTokenizer.Create(vocabStream, new BertOptions
                {
                    LowerCaseBeforeTokenization = true,
                    ApplyBasicTokenization = true
                });
            }, ct);
            BridgeLogger.Log("INFO", "Noosphere builtin embedding model loaded.");
        }
        finally { _embedLock.Release(); }
    }

    private static float[] EmbedOne(InferenceSession session, BertTokenizer tokenizer, string text)
    {
        const int maxLen = 256;
        // Encode without specials, then wrap with [CLS]/[SEP] so length stays ≤ maxLen.
        var body = tokenizer.EncodeToIds(text, maxLen - 2, out _, out _).ToList();
        var ids = tokenizer.BuildInputsWithSpecialTokens(body).ToArray();
        if (ids.Length == 0)
            ids = [tokenizer.ClassificationTokenId, tokenizer.SeparatorTokenId];

        var seq = ids.Length;
        var inputIds = new DenseTensor<long>(new[] { 1, seq });
        var attention = new DenseTensor<long>(new[] { 1, seq });
        var tokenTypes = new DenseTensor<long>(new[] { 1, seq });
        for (var i = 0; i < seq; i++)
        {
            inputIds[0, i] = ids[i];
            attention[0, i] = 1;
            tokenTypes[0, i] = 0;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attention),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypes)
        };

        using var results = session.Run(inputs);
        var output = results.First().AsTensor<float>();
        // Mean-pool token embeddings (skip padding — all positions are real tokens here) then L2-norm.
        var dims = output.Dimensions.ToArray();
        // [1, seq, hidden] or [seq, hidden]
        int hidden;
        float[] pooled;
        if (dims.Length == 3)
        {
            hidden = dims[2];
            pooled = new float[hidden];
            for (var t = 0; t < seq; t++)
            for (var h = 0; h < hidden; h++)
                pooled[h] += output[0, t, h];
            for (var h = 0; h < hidden; h++) pooled[h] /= seq;
        }
        else if (dims.Length == 2)
        {
            hidden = dims[1];
            pooled = new float[hidden];
            for (var t = 0; t < Math.Min(seq, dims[0]); t++)
            for (var h = 0; h < hidden; h++)
                pooled[h] += output[t, h];
            for (var h = 0; h < hidden; h++) pooled[h] /= Math.Min(seq, dims[0]);
        }
        else
            throw new InvalidOperationException($"Unexpected ONNX output rank {dims.Length}");

        var norm = MathF.Sqrt(pooled.Sum(v => v * v));
        if (norm > 1e-8f)
            for (var i = 0; i < pooled.Length; i++) pooled[i] /= norm;
        return pooled;
    }

    private void UnloadExtract()
    {
        _extractWeights?.Dispose();
        _extractWeights = null;
        _extractParams = null;
    }

    private void UnloadEmbed()
    {
        _embedSession?.Dispose();
        _embedSession = null;
        _embedTokenizer = null;
    }

    public void Dispose()
    {
        UnloadExtract();
        UnloadEmbed();
        _extractLock.Dispose();
        _embedLock.Dispose();
    }
}
