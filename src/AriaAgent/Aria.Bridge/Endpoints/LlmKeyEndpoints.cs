using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Services.Diagnostics;
using Aria.Bridge.Services.Llm;
using Aria.Bridge.Services.Logging;
using Aria.Shared;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// Cloud-provider API keys live here on the user's own machine — never on the Aria server.
/// Values are encrypted at rest under the bridge vault DEK (AES-256-GCM via <see cref="VaultEncryption"/>)
/// when a vault is available; legacy plaintext values are migrated on first startup.
/// The server routes cloud LLM calls through the WASM bridge to <c>/llm/proxy</c>, which injects
/// the locally-stored key and makes the call. Keys are returned to no one (only the names are
/// listed); they leave this process only as the Authorization header on the outbound LLM request.
/// </summary>
public static class LlmKeyEndpoints
{
    // Long timeout: LLM streaming responses can run for minutes.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static void MapLlmKeyEndpoints(this WebApplication app)
    {
        // List configured provider names (NOT the keys) — drives the Channel UI.
        app.MapGet("/keys", async (BridgeDbContext db) =>
        {
            var names = new List<string>();
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Provider FROM LlmKeys ORDER BY Provider;";
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) names.Add(r.GetString(0));
            }
            finally { await conn.CloseAsync(); }
            return Results.Ok(new { providers = names });
        });

        // NOTE: there is deliberately no "GET /keys/{provider}" that returns a usable key. The node
        // never hands a cloud LLM key back to the server: cloud calls are proxied through /llm/proxy,
        // which injects the key locally so it only leaves this process as the outbound Authorization
        // header to the provider. The server sees provider *names* (GET /keys) but never the secret.

        // Store / replace a provider's key (stored base64 — same level as the local soul key,
        // on the user's own machine).
        app.MapPut("/keys/{provider}", async (string provider, SaveKeyRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(req.Key))
                return Results.BadRequest("provider and key required");

            // Detect whether the previous stored key is unreadable (vault DEK rotated or lost). Logging this
            // makes it obvious when a re-save is fixing a real mismatch instead of a no-op.
            var prior = await GetStoredKeyB64Async(db, provider);
            if (prior != null && db.Vault != null && prior.StartsWith("enc:1:", StringComparison.Ordinal))
            {
                try { db.Vault.Decrypt(prior); }
                catch (CryptographicException ex)
                {
                    BridgeLogger.Log("WARN", $"Replacing unreadable stored key for '{provider}' (vault DEK mismatch: {ex.Message})");
                }
            }

            var keyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(req.Key.Trim()));
            // Encrypt at rest under the bridge vault DEK when available.
            if (db.Vault != null)
                keyB64 = db.Vault.Encrypt(keyB64);

            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO LlmKeys (Provider, KeyB64) VALUES (@p, @k)
                    ON CONFLICT(Provider) DO UPDATE SET KeyB64 = @k;
                """;
                AddParam(cmd, "@p", provider);
                AddParam(cmd, "@k", keyB64);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { await conn.CloseAsync(); }

            BridgeLogger.Log("INFO", $"Stored key for provider '{provider}'");
            return Results.Ok(new { ok = true });
        });


        app.MapDelete("/keys/{provider}", async (string provider, BridgeDbContext db) =>
        {
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM LlmKeys WHERE Provider = @p;";
                AddParam(cmd, "@p", provider);
                var deleted = await cmd.ExecuteNonQueryAsync();
                if (deleted > 0)
                    BridgeLogger.Log("INFO", $"Deleted key for provider '{provider}'");
            }
            finally { await conn.CloseAsync(); }
            return Results.Ok(new { ok = true });
        });

        // Single egress for ALL model traffic (Noosphere's extraction/embedding calls go straight
        // out from NoosphereExtractor/NoosphereEmbedder in-process — they don't ride this proxy).
        // The node makes the outbound call so the browser never has to (no CORS, no mixed-content),
        // and cloud keys stay here. The response body streams straight back (SSE for chat).
        //   • KeyRef set  → node channel: inject the stored key AND resolve the destination host from
        //                   the node's own record (public catalog or BridgeChannel), NOT from req.Url.
        //   • ApiKey set  → local model with its own key: use it as-is.
        //   • neither     → no Authorization (e.g. local model with no key).
        //
        // Destination pinning is the key-custody guarantee: a stored key is only ever sent to the host
        // the node itself declared for that channel, so a compromised server cannot set req.Url to an
        // endpoint it controls and capture the key.
        app.MapPost("/llm/proxy", async (LlmProxyRequest req, BridgeDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Url))
                return Results.BadRequest("url required");

            var targetUrl = req.Url;
            string? key;
            if (!string.IsNullOrEmpty(req.KeyRef))
            {
                key = await GetPlaintextKeyAsync(db, req.KeyRef);

                // If a key is stored but cannot be read, fail generically instead of silently falling
                // back to no auth. The error message is streamed back to the UI via the bridge's
                // generic failure path so any failure is visible.
                var storedKeyB64 = await GetStoredKeyB64Async(db, req.KeyRef);
                if (key == null && !string.IsNullOrEmpty(storedKeyB64) && storedKeyB64.StartsWith("enc:1:", StringComparison.Ordinal))
                {
                    return Results.Problem(statusCode: 400,
                        title: $"Failed to read stored key for '{req.KeyRef}'. Re-save the key on the bridge (http://localhost:5741).");
                }

                // Resolve the authoritative destination for this channel from the node's own records.
                var authBase = await ResolveChannelBaseUrlAsync(db, req.KeyRef);
                if (authBase != null)
                {
                    var pinned = PublicProviderCatalog.PinToHost(authBase, req.Url);
                    if (!string.Equals(pinned, req.Url, StringComparison.OrdinalIgnoreCase))
                        BridgeLogger.Log("WARN", $"/llm/proxy pinned '{req.KeyRef}' to node URL (server asked for {req.Url})");
                    targetUrl = pinned;
                }
                else if (key != null || req.RequireKey)
                {
                    // A key would be attached (or is required) but the node has no channel for this
                    // keyRef — refuse rather than send a secret to a server-chosen host.
                    return Results.Problem(statusCode: 400,
                        title: $"Unknown channel '{req.KeyRef}' — configure it on the bridge (http://localhost:5741) before use.");
                }

                if (key == null)
                {
                    if (req.RequireKey)
                        return Results.Problem(statusCode: 401, title: $"No API key stored for '{req.KeyRef}'");
                    // Optional keyRef with no stored key and no channel URL: no secret at risk; fall back
                    // to the explicit ApiKey (if any) and the server-supplied URL.
                    key = req.ApiKey;
                }
            }
            else
            {
                key = req.ApiKey; // local model's own key, or null
            }

            var httpReq = new HttpRequestMessage(HttpMethod.Post, targetUrl);
            var hasAuth = !string.IsNullOrEmpty(key) && key != "none";
            if (hasAuth)
                httpReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            httpReq.Content = new StringContent(req.Body ?? "", Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // The model endpoint itself is unreachable (not running, wrong port, DNS failure).
                // Surface this as a clear error instead of an unhandled 500 that DirectTunnel would
                // otherwise silently treat as a successful, empty response.
                return Results.Problem(statusCode: 502,
                    title: $"Could not reach model endpoint at {targetUrl} — is it running?",
                    detail: ex.Message);
            }
            var status      = (int)resp.StatusCode;
            var contentType = resp.Content.Headers.ContentType?.ToString();

            // Stream the response body straight through (SSE on success, or the error body) while
            // capturing the first bytes into the egress log — that head is what distinguishes a real
            // SSE stream from, say, LM Studio's "unexpected endpoint" JSON help body.
            return Results.Stream(async outStream =>
            {
                try
                {
                    await using var inStream = await resp.Content.ReadAsStreamAsync(ct);
                    var head = new byte[600];
                    var headLen = 0;
                    while (headLen < head.Length)
                    {
                        var n = await inStream.ReadAsync(head.AsMemory(headLen, head.Length - headLen), ct);
                        if (n == 0) break;
                        headLen += n;
                        await outStream.WriteAsync(head.AsMemory(headLen - n, n), ct);
                        await outStream.FlushAsync(ct);
                    }
                    EgressLog.Add(targetUrl, hasAuth, status, contentType,
                        Encoding.UTF8.GetString(head, 0, headLen));
                    await inStream.CopyToAsync(outStream, ct);
                }
                catch (Exception ex)
                {
                    EgressLog.Add(targetUrl, hasAuth, status, contentType, $"(relay error: {ex.Message})");
                    throw;
                }
                finally { resp.Dispose(); httpReq.Dispose(); }
            }, contentType: "text/event-stream");
        });

        // Recent outbound LLM calls with response heads — readable locally or via the tunnel.
        app.MapGet("/debug/llm-log", () => Results.Ok(EgressLog.List()));

        // Quick connectivity + auth probe — used by the channel editor to validate the endpoint
        // before the user saves. Returns { ok, latencyMs } on success or { ok:false, error } on failure.
        app.MapPost("/llm/probe", async (LlmProbeRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Url))
                return Results.BadRequest("url required");

            // Pin: a stored key only ever reaches the node's declared host for this channel.
            var (targetUrl, key) = await ResolveProbeTargetAsync(db, req.KeyRef, req.Url, req.ApiKey);

            var base_ = targetUrl.TrimEnd('/');
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                // Probe GET /models (then /v1/models for a missing-/v1 misconfiguration) — NOT a dummy
                // chat completion. A bogus {"model":"probe",...} completion validated reachability by
                // provoking a model-level error, but that made OpenAI-compatible servers like LM Studio
                // log a scary "Invalid model identifier 'probe'" on every probe. GET /models checks
                // reachability, auth (401/403) and the /v1 path exactly the same way, sends no model
                // name, and mirrors the path /llm/discover-models already uses.
                string? warning = null;
                HttpResponseMessage? resp = null;
                foreach (var candidate in new[] { base_ + "/models", base_ + "/v1/models" })
                {
                    using var httpReq = new HttpRequestMessage(HttpMethod.Get, candidate);
                    if (!string.IsNullOrEmpty(key) && key != "none")
                        httpReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
                    resp?.Dispose();
                    resp = await _http.SendAsync(httpReq, cts.Token);
                    // 200 (model list) or auth errors (401/403) mean the endpoint exists; only 404 says
                    // "wrong path" → fall through and try /v1.
                    if ((int)resp.StatusCode != 404)
                    {
                        if (candidate.EndsWith("/v1/models"))
                            warning = "URL should end with /v1 — chat completions won't work without it";
                        break;
                    }
                }
                sw.Stop();
                using (resp)
                {
                    var status = (int)resp!.StatusCode;
                    // 401/403 = wrong key, 404 = wrong path, 200 = endpoint found
                    if (status == 401 || status == 403)
                    {
                        var body = await resp.Content.ReadAsStringAsync(cts.Token);
                        var snippet = body.Length > 200 ? body[..200] : body;
                        return Results.Ok(new { ok = false, error = $"Auth rejected ({status}) — check API key. {snippet}", latencyMs = (int)sw.ElapsedMilliseconds });
                    }
                    if (status == 404)
                        return Results.Ok(new { ok = false, error = "Endpoint not found — verify the URL and that the server is running", latencyMs = (int)sw.ElapsedMilliseconds });
                    return Results.Ok(new { ok = true, latencyMs = (int)sw.ElapsedMilliseconds, warning });
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                return Results.Ok(new { ok = false, error = ex.InnerException?.Message ?? ex.Message });
            }
        });

        // Enumerate the models an OpenAI-compatible (or Ollama) endpoint actually serves — powers the
        // channel editor's "discover models" action so models never have to be typed in by hand. Same
        // key-custody pinning as /llm/probe: a stored key only ever reaches the node's declared host.
        // Returns { ok, models } on success or { ok:false, error } if nothing answered.
        app.MapPost("/llm/discover-models", async (LlmDiscoverModelsRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Url))
                return Results.BadRequest("url required");

            var (targetUrl, key) = await ResolveProbeTargetAsync(db, req.KeyRef, req.Url, req.ApiKey);
            var base_ = targetUrl.TrimEnd('/');
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // Try OpenAI-compatible GET {base}/models, then GET {base}/v1/models (missing-/v1 case,
            // same fallback /llm/probe uses), then Ollama's native GET {base}/api/tags.
            (string Url, Func<string, List<string>> Parse)[] candidates =
            [
                (base_ + "/models", ParseOpenAiModels),
                (base_ + "/v1/models", ParseOpenAiModels),
                (base_ + "/api/tags", ParseOllamaTags),
            ];

            foreach (var (candidateUrl, parse) in candidates)
            {
                try
                {
                    using var httpReq = new HttpRequestMessage(HttpMethod.Get, candidateUrl);
                    if (!string.IsNullOrEmpty(key) && key != "none")
                        httpReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
                    using var resp = await _http.SendAsync(httpReq, cts.Token);
                    if (!resp.IsSuccessStatusCode) continue;
                    var body = await resp.Content.ReadAsStringAsync(cts.Token);
                    var models = parse(body);
                    if (models.Count > 0)
                    {
                        // Persist the freshly-discovered list onto the matching custom channel so it
                        // survives — otherwise the web's ⟳ rediscover updates only its in-memory cache
                        // (it can't PUT here — that's local-origin only) and the next /channels fetch
                        // reverts to this stale stored list. Node-authored: WE re-queried OUR endpoint
                        // and store OUR result; the server supplies no model data. Public providers
                        // (catalog-fixed models, not in db.Channels) simply don't match and are skipped.
                        await PersistDiscoveredModelsAsync(db, req.KeyRef, models);
                        return Results.Ok(new { ok = true, models });
                    }
                }
                catch { /* try the next candidate */ }
            }
            return Results.Ok(new { ok = false, error = "Could not list models from this endpoint." });
        });

        // Full thinking-format detection — streams a real query through the local LLM and inspects
        // the SSE deltas. Returns { thinking: "ThinkTags|ReasoningContent|StartsInThinkMode|None" }.
        // Called by the server via LocalRest when a bridged source needs format detection.
        app.MapPost("/llm/detect-format", async (LlmDetectFormatRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Url) || string.IsNullOrWhiteSpace(req.Model))
                return Results.BadRequest("url and model required");

            // Pin: a stored key only ever reaches the node's declared host for this channel.
            var (targetUrl, key) = await ResolveProbeTargetAsync(db, req.KeyRef, req.Url, req.ApiKey);

            // Warm the model FIRST. LM Studio's just-in-time loading means the first request triggers
            // a (possibly slow) model load; firing the three probes at a cold model lets that load race
            // their timeouts, and the tool/vision probe intermittently times out → a false "Unknown"
            // (observed on a heavy-reasoning Llama-3.3 fine-tune: warm == correct "None", cold ==
            // "Unknown"). A tiny blocking request loads the model so the real probes hit it warm.
            await Aria.Shared.FormatProber.WarmupAsync(targetUrl, req.Model, key);

            // Run all probes concurrently — tool-call and vision are short (20s max), thinking is 45s.
            var thinkingTask = Aria.Shared.FormatProber.ProbeThinkingAsync(targetUrl, req.Model, key);
            var toolCallTask = Aria.Shared.FormatProber.ProbeToolCallAsync(targetUrl, req.Model, key);
            var visionTask   = Aria.Shared.FormatProber.ProbeVisionAsync(targetUrl, req.Model, key);
            var contextTask  = Aria.Shared.ContextWindowProber.ProbeAsync(targetUrl, req.Model, key);
            await Task.WhenAll(thinkingTask, toolCallTask, visionTask, contextTask);

            int? contextWindow = contextTask.Result;
            return Results.Ok(new
            {
                thinking = thinkingTask.Result,
                toolCall = toolCallTask.Result,
                vision = visionTask.Result,
                contextWindow,
                contextWindowAssumed = !contextWindow.HasValue,
            });
        });

        // Voice transcription: the browser POSTs audio straight here (multipart). The bridge injects
        // the provider key and calls Whisper — so the audio AND the key stay on the user's machine,
        // never the server. Returns { text } or { error }.
        app.MapPost("/transcribe", async (HttpRequest httpReq, BridgeDbContext db) =>
        {
            if (!httpReq.HasFormContentType) return Results.BadRequest("multipart form required");
            var form     = await httpReq.ReadFormAsync();
            var provider = form["provider"].ToString();
            var file     = form.Files["audio"];
            if (file == null || file.Length == 0) return Results.BadRequest("no audio");

            var (url, model) = provider switch
            {
                "OpenAI" => ("https://api.openai.com/v1/audio/transcriptions", "whisper-1"),
                "Groq"   => ("https://api.groq.com/openai/v1/audio/transcriptions", "whisper-large-v3"),
                _        => ((string?)null, (string?)null)
            };
            if (url == null)
                return Results.Ok(new { error = $"'{provider}' does not support transcription" });

            var key = await GetPlaintextKeyAsync(db, provider);
            if (key == null)
                return Results.Ok(new { error = $"No API key stored on the bridge for {provider}" });

            try
            {
                using var content = new MultipartFormDataContent();
                var audio = new StreamContent(file.OpenReadStream());
                audio.Headers.ContentType = new MediaTypeHeaderValue("audio/webm");
                content.Add(audio, "file", string.IsNullOrEmpty(file.FileName) ? "vox.webm" : file.FileName);
                content.Add(new StringContent(model!), "model");

                using var whisperReq = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                whisperReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
                using var resp = await _http.SendAsync(whisperReq);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    return Results.Ok(new { error = $"Transcription failed ({(int)resp.StatusCode})" });

                using var doc = JsonDocument.Parse(body);
                var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
                return Results.Ok(new { text });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { error = $"Transcription error: {ex.Message}" });
            }
        });
    }

    // Returns the plaintext key for a provider, or null if none stored.
    private static Task<string?> GetPlaintextKeyAsync(BridgeDbContext db, string provider) =>
        LlmKeyStore.GetPlaintextKeyAsync(db, provider);

    // Returns the raw stored KeyB64 for a provider, or null if none stored.
    private static async Task<string?> GetStoredKeyB64Async(BridgeDbContext db, string provider)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT KeyB64 FROM LlmKeys WHERE Provider = @p LIMIT 1;";
            AddParam(cmd, "@p", provider);
            return await cmd.ExecuteScalarAsync() as string;
        }
        finally { await conn.CloseAsync(); }
    }

    /// <summary>
    /// Resolves the node-authoritative base URL for a channel name: the fixed canonical URL for a public
    /// provider, or the URL of a node-authored custom channel. Returns null if the node has no such
    /// channel — in which case a keyed call must be refused rather than sent to a server-chosen host.
    /// </summary>
    private static async Task<string?> ResolveChannelBaseUrlAsync(BridgeDbContext db, string name)
    {
        var pub = PublicProviderCatalog.CanonicalUrlFor(name);
        if (pub != null) return pub;

        var custom = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Name == name);
        return string.IsNullOrWhiteSpace(custom?.Url) ? null : custom!.Url;
    }

    /// <summary>
    /// Resolves the base URL + key for a probe/detect call, applying the same key-custody rule as the
    /// proxy: a stored key is only ever attached to the host the node declared for that channel. If the
    /// node has no channel record for the keyRef, the stored key is dropped rather than sent to a
    /// server-supplied URL (connectivity can still be probed without auth).
    /// </summary>
    internal static async Task<(string Url, string? Key)> ResolveProbeTargetAsync(
        BridgeDbContext db, string? keyRef, string requestedUrl, string? apiKey)
    {
        string? key = null;
        var url = requestedUrl;
        if (!string.IsNullOrEmpty(keyRef))
        {
            key = await GetPlaintextKeyAsync(db, keyRef);
            var authBase = await ResolveChannelBaseUrlAsync(db, keyRef);
            // Pin to the node's declared HOST but keep the requested PATH (…/v1/chat/completions).
            // Using authBase verbatim dropped the path, so the probe POSTed to the bare base (…/v1) and
            // LM Studio answered with its "unexpected endpoint" help body → format detection always
            // failed. Mirrors the /llm/proxy pinning above.
            if (authBase != null) url = PublicProviderCatalog.PinToHost(authBase, requestedUrl);
            else if (key != null) key = null;       // no authoritative URL → never leak the stored key
        }
        if (key == null && !string.IsNullOrEmpty(apiKey)) key = apiKey;
        return (url, key);
    }

    /// <summary>Writes a freshly-discovered model list onto the custom channel named <paramref name="keyRef"/>,
    /// if one exists. Keeps the discovered order, drops blanks. No-op when keyRef is empty or names a
    /// public provider (those aren't stored in db.Channels). Best-effort: a DB hiccup never fails the
    /// discovery response the caller already has.</summary>
    private static async Task PersistDiscoveredModelsAsync(BridgeDbContext db, string? keyRef, List<string> models)
    {
        if (string.IsNullOrWhiteSpace(keyRef)) return;
        try
        {
            var channel = await db.Channels.FirstOrDefaultAsync(c => c.Name == keyRef);
            if (channel == null) return;   // unknown / public provider — nothing local to update
            var cleaned = models.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToArray();
            var newJson = JsonSerializer.Serialize(cleaned);
            if (channel.ModelsJson == newJson) return;   // unchanged — skip the write
            channel.ModelsJson = newJson;
            await db.SaveChangesAsync();
            BridgeLogger.Log("INFO", $"Channel models refreshed: {keyRef} ({cleaned.Length} model(s))");
        }
        catch (Exception ex)
        {
            BridgeLogger.Log("WARN", $"Could not persist discovered models for {keyRef}: {ex.Message}");
        }
    }

    // OpenAI-compatible GET /models response: { data: [{ id: "..." }, ...] }.
    internal static List<string> ParseOpenAiModels(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                return data.EnumerateArray()
                    .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
        catch { /* not this shape — caller tries the next candidate */ }
        return [];
    }

    // Ollama's GET /api/tags response: { models: [{ name: "..." }, ...] }.
    internal static List<string> ParseOllamaTags(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                return models.EnumerateArray()
                    .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
        catch { /* not this shape */ }
        return [];
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}

public record SaveKeyRequest(string Key);
public record KeySyncImportRequest(string Blob);
public record LlmProxyRequest(string Url, string? Body, string? KeyRef, string? ApiKey, bool RequireKey = true);
public record LlmProbeRequest(string Url, string? KeyRef, string? ApiKey);
public record LlmDiscoverModelsRequest(string Url, string? KeyRef, string? ApiKey);
public record LlmDetectFormatRequest(string Url, string Model, string? KeyRef, string? ApiKey);
