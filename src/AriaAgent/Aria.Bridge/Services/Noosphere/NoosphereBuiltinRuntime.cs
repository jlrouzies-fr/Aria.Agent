using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Aria.Bridge.Services.Logging;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Sampling;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Aria.Bridge.Services.Noosphere;

/// <summary>
/// Opt-in on-node Noosphere models (MiniLM ONNX embeddings + Qwen2.5 Instruct GGUF extract variants).
/// Mirrors <see cref="Speech.LocalWhisperService"/>: catalog download into app-data, progress poll,
/// SHA256 verify, lazy load. Never involves Aria.Web.
/// </summary>
public sealed class NoosphereBuiltinRuntime : IDisposable
{
    // Qwen2.5 GGUFs support far more; 8k keeps RAM reasonable on CPU while fitting compact extract
    // prompts + an Inscribe body. LLamaSharp defaults to ThrowException on overflow — we opt into truncate.
    internal const uint ExtractContextSize = 8192;
    // Char budget for the user turn (~3–4 chars/token). Kept well under the window so generation can
    // finish a JSON object without TruncateAndReprefill chewing the partial reply (that yielded
    // "no usable JSON" on long Inscribes even after the overflow exception was gone).
    internal const int MaxExtractUserChars = 6_000;
    private readonly ConcurrentDictionary<string, int> _progress = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    // SHA256 of a multi-GB GGUF is multi-second — cache by (path, length, mtime) so Status polls and
    // IsBuiltinActive checks don't re-hash on every Memory-tab open / 2.5s loaded poll.
    private readonly ConcurrentDictionary<string, (long Length, DateTime MtimeUtc)> _verifiedCache = new(StringComparer.Ordinal);
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string _modelsDir;
    private readonly SemaphoreSlim _extractLock = new(1, 1);
    private readonly SemaphoreSlim _embedLock = new(1, 1);

    private LLamaWeights? _extractWeights;
    private ModelParams? _extractParams;
    private string? _loadedExtractModelId;
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

    public bool IsExtractOnDisk(string? extractModelId)
    {
        var v = NoosphereBuiltinCatalog.LookupExtract(NoosphereBuiltinCatalog.ResolveExtractId(extractModelId));
        return v != null && v.Files.All(IsFileVerified);
    }

    public bool IsEmbedOnDisk() => NoosphereBuiltinCatalog.Embed.Files.All(IsFileVerified);

    /// <summary>
    /// Legacy role check: extract uses the default variant id; prefer
    /// <see cref="IsExtractOnDisk"/> / <see cref="IsEmbedOnDisk"/>.
    /// </summary>
    public bool IsRoleOnDisk(string role)
    {
        if (string.Equals(role, NoosphereBuiltinCatalog.RoleEmbed, StringComparison.OrdinalIgnoreCase))
            return IsEmbedOnDisk();
        if (string.Equals(role, NoosphereBuiltinCatalog.RoleExtract, StringComparison.OrdinalIgnoreCase))
            return IsExtractOnDisk(NoosphereBuiltinCatalog.DefaultExtractModelId);
        if (NoosphereBuiltinCatalog.IsKnownExtractId(role))
            return IsExtractOnDisk(role);
        return false;
    }

    public bool IsReady(string? extractModelId = null) =>
        IsExtractOnDisk(extractModelId) && IsEmbedOnDisk();

    /// <summary>True when the role's weights/session are resident in process memory.</summary>
    public bool IsRoleLoaded(string role)
    {
        if (string.Equals(role, NoosphereBuiltinCatalog.RoleExtract, StringComparison.OrdinalIgnoreCase)
            || NoosphereBuiltinCatalog.IsKnownExtractId(role))
            return _extractWeights != null
                   && (string.IsNullOrEmpty(role)
                       || string.Equals(role, NoosphereBuiltinCatalog.RoleExtract, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(role, _loadedExtractModelId, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(role, NoosphereBuiltinCatalog.RoleEmbed, StringComparison.OrdinalIgnoreCase))
            return _embedSession != null && _embedTokenizer != null;
        return false;
    }

    public string? LoadedExtractModelId => _loadedExtractModelId;

    public object Status(bool enabled, DateTime? licenseAcceptedAt, string? selectedExtractModelId)
    {
        var selected = NoosphereBuiltinCatalog.ResolveExtractId(selectedExtractModelId);
        var ready = enabled && IsReady(selected);

        var extractVariants = NoosphereBuiltinCatalog.ExtractVariants.Select(v =>
        {
            var key = NoosphereBuiltinCatalog.ProgressKey(NoosphereBuiltinCatalog.RoleExtract, v.Id);
            _progress.TryGetValue(key, out var pct);
            _errors.TryGetValue(key, out var err);
            var onDisk = v.Files.All(IsFileVerified);
            var loaded = _extractWeights != null
                         && string.Equals(_loadedExtractModelId, v.Id, StringComparison.OrdinalIgnoreCase);
            return new
            {
                id = v.Id,
                label = v.Label,
                license = v.License,
                approxBytes = v.Files.Sum(f => f.ApproxBytes),
                downloaded = onDisk,
                loaded,
                selected = string.Equals(v.Id, selected, StringComparison.OrdinalIgnoreCase),
                downloading = _progress.ContainsKey(key),
                progress = pct,
                error = err,
                warnTip = v.WarnTip,
                recommended = v.Recommended,
                files = v.Files.Select(f => new
                {
                    fileName = f.FileName,
                    present = File.Exists(PathFor(f)),
                    verified = File.Exists(PathFor(f)) && IsFileVerified(f)
                }).ToArray()
            };
        }).ToArray();

        var embed = NoosphereBuiltinCatalog.Embed;
        _progress.TryGetValue(NoosphereBuiltinCatalog.RoleEmbed, out var embedPct);
        _errors.TryGetValue(NoosphereBuiltinCatalog.RoleEmbed, out var embedErr);
        var embedOnDisk = embed.Files.All(IsFileVerified);

        return new
        {
            enabled,
            licenseAccepted = licenseAcceptedAt != null,
            licenseAcceptedAt,
            selectedExtractModelId = selected,
            ready,
            anyLoaded = _extractWeights != null || (_embedSession != null && _embedTokenizer != null),
            extractVariants,
            roles = new object[]
            {
                new
                {
                    role = NoosphereBuiltinCatalog.RoleEmbed,
                    label = embed.Label,
                    license = embed.License,
                    approxBytes = embed.Files.Sum(f => f.ApproxBytes),
                    downloaded = embedOnDisk,
                    loaded = IsRoleLoaded(NoosphereBuiltinCatalog.RoleEmbed),
                    downloading = _progress.ContainsKey(NoosphereBuiltinCatalog.RoleEmbed),
                    progress = embedPct,
                    error = embedErr,
                    files = embed.Files.Select(f => new
                    {
                        fileName = f.FileName,
                        present = File.Exists(PathFor(f)),
                        verified = File.Exists(PathFor(f)) && IsFileVerified(f)
                    }).ToArray()
                }
            }
        };
    }

    public string? StartDownload(string role, bool licenseAccepted, string? extractModelId = null)
    {
        if (string.Equals(role, NoosphereBuiltinCatalog.RoleEmbed, StringComparison.OrdinalIgnoreCase))
        {
            if (IsEmbedOnDisk()) return null;
            var key = NoosphereBuiltinCatalog.RoleEmbed;
            if (!_progress.TryAdd(key, 0)) return null;
            _errors.TryRemove(key, out _);
            _ = Task.Run(() => DownloadFilesAsync(key, NoosphereBuiltinCatalog.Embed.Label, NoosphereBuiltinCatalog.Embed.Files));
            return null;
        }

        if (string.Equals(role, NoosphereBuiltinCatalog.RoleExtract, StringComparison.OrdinalIgnoreCase)
            || NoosphereBuiltinCatalog.IsKnownExtractId(role))
        {
            if (!licenseAccepted)
                return "Accept the Apache-2.0 license notice before downloading the extraction model.";
            var id = NoosphereBuiltinCatalog.IsKnownExtractId(role)
                ? role
                : NoosphereBuiltinCatalog.ResolveExtractId(extractModelId);
            var variant = NoosphereBuiltinCatalog.LookupExtract(id);
            if (variant == null) return $"Unknown extract model '{id}'";
            if (IsExtractOnDisk(id)) return null;
            var key = NoosphereBuiltinCatalog.ProgressKey(NoosphereBuiltinCatalog.RoleExtract, id);
            if (!_progress.TryAdd(key, 0)) return null;
            _errors.TryRemove(key, out _);
            _ = Task.Run(() => DownloadFilesAsync(key, variant.Label, variant.Files));
            return null;
        }

        return $"Unknown role '{role}'";
    }

    private async Task DownloadFilesAsync(string progressKey, string label, IReadOnlyList<NoosphereBuiltinCatalog.ModelFile> files)
    {
        try
        {
            var totalApprox = files.Sum(f => f.ApproxBytes);
            long doneBytes = 0;
            foreach (var file in files)
            {
                var finalPath = PathFor(file);
                if (File.Exists(finalPath) && VerifyFileSha256(finalPath, file.Sha256Hex))
                {
                    doneBytes += file.ApproxBytes;
                    _progress[progressKey] = (int)Math.Min(100, doneBytes * 100 / Math.Max(1, totalApprox));
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
                        var scaled = doneBytes + (long)(read * (double)file.ApproxBytes / Math.Max(1, total));
                        _progress[progressKey] = (int)Math.Min(99, scaled * 100 / Math.Max(1, totalApprox));
                    }
                }

                if (!VerifyFileSha256(tmpPath, file.Sha256Hex))
                {
                    try { File.Delete(tmpPath); } catch { /* best effort */ }
                    throw new InvalidOperationException(
                        $"SHA256 mismatch for {file.FileName} — refusing to install (catalog pin failed).");
                }

                File.Move(tmpPath, finalPath, overwrite: true);
                var fi = new FileInfo(finalPath);
                _verifiedCache[finalPath] = (fi.Length, fi.LastWriteTimeUtc);
                doneBytes += file.ApproxBytes;
                _progress[progressKey] = (int)Math.Min(100, doneBytes * 100 / Math.Max(1, totalApprox));
            }

            BridgeLogger.Log("INFO", $"Noosphere builtin '{progressKey}' downloaded ({label}).");
        }
        catch (Exception ex)
        {
            _errors[progressKey] = ex.Message;
            BridgeLogger.Log("ERROR", $"Noosphere builtin '{progressKey}' download failed: {ex.Message}");
        }
        finally
        {
            _progress.TryRemove(progressKey, out _);
        }
    }

    public bool DeleteModel(string role, string? extractModelId = null)
    {
        if (string.Equals(role, NoosphereBuiltinCatalog.RoleEmbed, StringComparison.OrdinalIgnoreCase))
        {
            UnloadModel(NoosphereBuiltinCatalog.RoleEmbed);
            return DeleteFiles(NoosphereBuiltinCatalog.Embed.Files);
        }

        if (string.Equals(role, NoosphereBuiltinCatalog.RoleExtract, StringComparison.OrdinalIgnoreCase)
            || NoosphereBuiltinCatalog.IsKnownExtractId(role))
        {
            var id = NoosphereBuiltinCatalog.IsKnownExtractId(role)
                ? role
                : NoosphereBuiltinCatalog.ResolveExtractId(extractModelId);
            var variant = NoosphereBuiltinCatalog.LookupExtract(id);
            if (variant == null) return false;
            if (string.Equals(_loadedExtractModelId, id, StringComparison.OrdinalIgnoreCase))
                UnloadModel(NoosphereBuiltinCatalog.RoleExtract);
            return DeleteFiles(variant.Files);
        }

        return false;
    }

    private bool DeleteFiles(IReadOnlyList<NoosphereBuiltinCatalog.ModelFile> files)
    {
        var deleted = false;
        foreach (var f in files)
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
        if (string.Equals(role, NoosphereBuiltinCatalog.RoleExtract, StringComparison.OrdinalIgnoreCase)
            || NoosphereBuiltinCatalog.IsKnownExtractId(role))
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
    /// When true, seeds the assistant turn with <c>{</c> so a small model stays on a JSON object
    /// (Qwen ChatML structured-output prefill). Caller must treat the returned text as a
    /// full object (leading brace is restored).
    /// </param>
    public async Task<(string? Text, string? Error)> CompleteChatAsync(
        string systemPrompt, string userContent, double temperature, int maxTokens, CancellationToken ct,
        bool prefillJsonObject = false, string? extractModelId = null)
    {
        var id = NoosphereBuiltinCatalog.ResolveExtractId(extractModelId);
        if (!IsExtractOnDisk(id))
            return (null, "Built-in extraction model is not downloaded on this node.");

        try
        {
            await EnsureExtractLoadedAsync(id, ct);
            var weights = _extractWeights!;
            var modelParams = _extractParams!;

            var user = TruncateForExtractContext(userContent);
            // Qwen2.5 ChatML — llama.cpp injects BOS; do not also prefix <|endoftext|> / start tokens.
            var prompt =
                "<|im_start|>system\n" + systemPrompt + "<|im_end|>\n" +
                "<|im_start|>user\n" + user + "<|im_end|>\n" +
                "<|im_start|>assistant\n" + (prefillJsonObject ? "{" : "");

            // Leave headroom for the reply. Long user turns get a smaller completion budget so the
            // model finishes a short facts[] instead of streaming until max_tokens mid-object.
            var replyCap = user.Length > 3_000 ? 768 : 1024;
            var cappedMax = Math.Clamp(maxTokens, 64, replyCap);
            var executor = new StatelessExecutor(weights, modelParams);
            var inf = new InferenceParams
            {
                MaxTokens = cappedMax,
                AntiPrompts = ["<|im_end|>", "<|endoftext|>"],
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = (float)temperature },
                OverflowStrategy = ContextOverflowStrategy.TruncateAndReprefill,
                ContextTruncationPercentage = 0.2f,
                // Keep the system turn when truncating so instruct rules survive a reprefills.
                TokensKeep = 256
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
        catch (ContextOverflowException)
        {
            return (null,
                "Built-in extract context full — Inscribe text too long for the local model window. Shorten the content or unload/reload after updating the bridge.");
        }
        catch (Exception ex)
        {
            // Older LLamaSharp builds / wrapped messages still carry the ThrowException wording.
            if (ex.Message.Contains("context window is full", StringComparison.OrdinalIgnoreCase))
                return (null,
                    "Built-in extract context full — Inscribe text too long for the local model window. Shorten the content or unload/reload after updating the bridge.");
            return (null, ex.Message);
        }
    }

    internal static string TruncateForExtractContext(string userContent)
    {
        if (string.IsNullOrEmpty(userContent) || userContent.Length <= MaxExtractUserChars)
            return userContent;
        return userContent[..MaxExtractUserChars] + "\n…[truncated for built-in extract context]";
    }

    public async Task<(List<float[]>? Vectors, string? Error)> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (!IsEmbedOnDisk())
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
            return (null, ex.Message);
        }
    }

    private bool IsExtractWarm(string id) =>
        _extractWeights != null
        && string.Equals(_loadedExtractModelId, id, StringComparison.OrdinalIgnoreCase)
        && _extractParams != null
        && _extractParams.ContextSize == ExtractContextSize;

    private async Task EnsureExtractLoadedAsync(string extractModelId, CancellationToken ct)
    {
        var id = NoosphereBuiltinCatalog.ResolveExtractId(extractModelId);
        if (IsExtractWarm(id)) return;

        await _extractLock.WaitAsync(ct);
        try
        {
            if (IsExtractWarm(id)) return;

            if (_extractWeights != null)
                UnloadExtract();

            var variant = NoosphereBuiltinCatalog.LookupExtract(id)
                          ?? throw new InvalidOperationException($"Unknown extract model '{id}'.");
            var path = PathFor(variant.Files[0]);
            if (!IsFileVerified(variant.Files[0]))
                throw new InvalidOperationException("Built-in extraction model failed SHA256 verification.");

            var parameters = new ModelParams(path)
            {
                ContextSize = ExtractContextSize,
                GpuLayerCount = 0
            };
            _extractWeights = await Task.Run(() => LLamaWeights.LoadFromFile(parameters), ct);
            _extractParams = parameters;
            _loadedExtractModelId = id;
            BridgeLogger.Log("INFO",
                $"Noosphere builtin extraction model loaded ({variant.Label}, ctx={ExtractContextSize}).");
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
            var info = NoosphereBuiltinCatalog.Embed;
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
        var dims = output.Dimensions.ToArray();
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
        _loadedExtractModelId = null;
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
