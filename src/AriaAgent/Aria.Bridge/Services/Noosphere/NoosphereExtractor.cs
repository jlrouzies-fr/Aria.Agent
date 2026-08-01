using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aria.Bridge.Data;
using Aria.Bridge.Services.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aria.Bridge.Services.Noosphere;

// LLM extraction (Inscribe → atomic facts/entities/relations) and contemplate-synthesis. Prefers
// opt-in built-in GGUF when enabled+ready; otherwise a non-streaming chat/completions call over the
// configured extraction channel.
public class NoosphereExtractor(
    NoosphereConfigService configService,
    NoosphereBuiltinRuntime builtinRuntime,
    IOptions<NoosphereOptions> legacyOptions,
    IServiceScopeFactory scopeFactory)
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    // Reason the most recent extraction/contemplation call failed (null if it succeeded, or none has
    // run yet). Read by BuiltinTools.Memory / NoosphereService to turn a silent failure into an actual
    // error message — e.g. LM Studio's own "model not found" text — instead of a generic fallback.
    public string? LastError { get; private set; }
    public DateTime? LastErrorAt { get; private set; }
    private void Fail(string reason)
    {
        LastError = reason;
        LastErrorAt = DateTime.UtcNow;
        // Event Log panel reads BridgeLogger — ILogger alone never surfaces there.
        BridgeLogger.Log("ERROR", $"Noosphere extraction: {reason}");
    }

    public record ExtractedEntity(string Name, string? Kind);
    public record ExtractedRelation(string From, string Relation, string To);
    public record ExtractedFact(string Content, List<ExtractedEntity> Entities, List<ExtractedRelation> Relations, string? TimeAnchor);

    public async Task<List<ExtractedFact>?> ExtractAsync(
        string content,
        IReadOnlyList<(string Name, string? Kind)> knownEntities,
        IReadOnlyList<(string Name, string Description)> anchors,
        CancellationToken ct)
    {
        var builtin = await configService.IsBuiltinActiveAsync(builtinRuntime, ct);
        var system = BuildExtractSystemPrompt(knownEntities, anchors, compact: builtin);
        var sourceLabel = "builtin";
        string? raw;
        string? error;

        if (builtin)
        {
            var extractId = configService.ResolveBuiltinExtractModelId(await configService.GetConfigAsync(ct));
            // One retry — small Quants occasionally emit broken JSON; a second draw is cheap vs raw fallback.
            // First failure sets LastError so the Memory nav/banner lights up during the retry window;
            // success clears it (a recovered attempt is not a sticky fault).
            for (var attempt = 0; attempt < 2; attempt++)
            {
                (raw, error) = await builtinRuntime.CompleteChatAsync(
                    system, content, temperature: 0.1, maxTokens: 2048, ct,
                    prefillJsonObject: true, extractModelId: extractId);
                if (error != null) { Fail(error); return null; }
                var parsed = TryParseFacts(raw, sourceLabel, out var failReason);
                if (parsed is { Count: > 0 })
                {
                    if (attempt > 0)
                        BridgeLogger.Log("INFO", "Noosphere builtin extract recovered on retry.");
                    LastError = null;
                    return parsed;
                }
                if (attempt == 0)
                {
                    // Surface on Memory UI while we retry (refreshMemoryHealth polls lastExtractionError).
                    LastError = failReason;
                    LastErrorAt = DateTime.UtcNow;
                    BridgeLogger.Log("INFO", $"Noosphere builtin extract retrying — {failReason}");
                }
                else
                {
                    Fail(failReason ?? $"{sourceLabel} returned JSON without usable facts.");
                    return null;
                }
            }
            return null;
        }

        var channel = await ResolveAsync(ct);
        if (channel == null)
        {
            // Without this, Inscribe still "succeeds" and ProcessIngest falls back to raw text with
            // no LastError for BuiltinTools to elevate — the agent then claims the Archivum was sealed.
            Fail("No extraction channel configured or resolvable on this node — open the bridge Memory tab and set a working local model (e.g. LM Studio), or enable built-in models.");
            return null;
        }
        sourceLabel = $"'{channel.Model}' on {channel.Url}";
        (raw, error) = await PostChatCompletionAsync(channel, system, content, temperature: 0.1, wantJsonMode: true, maxTokens: 2048, ct);
        if (error != null) { Fail(error); return null; }

        var result = TryParseFacts(raw, sourceLabel, out var reason);
        if (result is { Count: > 0 })
        {
            LastError = null;
            return result;
        }
        Fail(reason ?? $"{sourceLabel} returned JSON without usable facts.");
        return null;
    }

    private static List<ExtractedFact>? TryParseFacts(string? raw, string sourceLabel, out string? failReason)
    {
        failReason = null;
        var json = TryExtractJson(raw);
        if (json == null)
        {
            failReason =
                $"{sourceLabel} returned no usable JSON — check it's an instruct model, not a \"thinking\"/reasoning one. Snippet: {Snippet(raw)}";
            return null;
        }

        // Soft-repair first — 1.2B models often emit trailing commas or bare kind enums
        // (`"kind": person|place`) that JsonDocument rejects.
        foreach (var candidate in new[] { json, SoftRepairJson(json) }.Distinct())
        {
            try
            {
                var result = ParseFacts(candidate);
                if (result is { Count: > 0 })
                    return result;
                failReason = $"{sourceLabel} returned JSON without usable facts. Snippet: {Snippet(candidate)}";
            }
            catch (Exception ex)
            {
                failReason = $"{sourceLabel} returned unparseable JSON: {ex.Message}. Snippet: {Snippet(candidate)}";
            }
        }
        return null;
    }

    public async Task<string?> ContemplateSynthesisAsync(string query, string probedText, CancellationToken ct)
    {
        const string system =
            "You are the Noosphere contemplation cogitator of an Imperial archive. Answer the question " +
            "drawing only on the probed engrams below. If the archive holds nothing relevant, say so " +
            "plainly — do not invent facts.";
        var user = $"Probed engrams:\n{probedText}\n\nQuestion: {query}";

        string? text;
        string? error;
        if (await configService.IsBuiltinActiveAsync(builtinRuntime, ct))
        {
            var extractId = configService.ResolveBuiltinExtractModelId(await configService.GetConfigAsync(ct));
            (text, error) = await builtinRuntime.CompleteChatAsync(
                system, user, temperature: 0.3, maxTokens: 1024, ct, extractModelId: extractId);
        }
        else
        {
            var channel = await ResolveAsync(ct);
            if (channel == null)
            {
                Fail("No extraction channel configured or resolvable on this node — open the bridge Memory tab and set a working local model (e.g. LM Studio), or enable built-in models.");
                return null;
            }
            (text, error) = await PostChatCompletionAsync(channel, system, user, temperature: 0.3, wantJsonMode: false, maxTokens: 1024, ct);
        }

        if (error != null) { Fail(error); return null; }
        LastError = null;
        return text;
    }

    private static string BuildExtractSystemPrompt(
        IReadOnlyList<(string Name, string? Kind)> knownEntities,
        IReadOnlyList<(string Name, string Description)> anchors,
        bool compact)
    {
        // Known-entities + anchors go directly above the schema — small local models lose
        // instructions placed far from the thing they govern, so the "reuse EXACT names" rule sits
        // right next to the list it applies to.
        var knownBlock = knownEntities.Count == 0 ? "" : $"""

            KNOWN ENTITIES (reuse EXACT names when the same thing is mentioned):
            {string.Join("\n", knownEntities.Select(e => $"- {e.Name} ({e.Kind ?? "other"})"))}

            """;
        var anchorBlock = anchors.Count == 0 ? "" : $"""

            ACTIVE PROJECTS (include as kind "project" with exact name when relevant):
            {string.Join("\n", anchors.Select(a => $"- {a.Name} — {a.Description}"))}

            """;

        // Compact prompt for the on-node 1.2B model — long Imperial framing + schema prose was
        // producing well-formed JSON that ignored the facts[] contract.
        if (compact)
        {
            // Avoid {{...}} object literals inside $$""" — the interpolator treats inner { as expressions.
            return
                "Extract atomic self-contained facts from the user text into JSON only.\n" +
                $"Current UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm}.\n" +
                "Rules: resolve pronouns; put ISO dates in timeAnchor when clear; " +
                "entities MUST be objects {\"name\":\"...\",\"kind\":\"person\"} — never bare strings; " +
                "kind is one of person,place,org,concept,thing,event,project,other; " +
                "relations only between entities listed on the same fact. No markdown, no commentary.\n" +
                knownBlock + anchorBlock +
                "Schema:\n" +
                "{\"facts\":[{\"content\":\"...\",\"entities\":[{\"name\":\"...\",\"kind\":\"person\"}]," +
                "\"relations\":[{\"from\":\"...\",\"relation\":\"...\",\"to\":\"...\"}],\"timeAnchor\":\"YYYY-MM-DD\"}]}";
        }

        return $$"""
            You are the Noosphere ingestion cogitator of an Imperial archive. You convert raw
            intelligence into discrete memory engrams.
            Current date-time: {{DateTime.UtcNow:yyyy-MM-dd HH:mm}} UTC.
            Rules:
            - Split the input into atomic, self-contained facts ("engrams"). Each must be
              understandable with no other context: resolve pronouns and relative references.
            - Resolve relative time ("yesterday", "next week") into an ISO-8601 date in timeAnchor
              when possible; otherwise omit it.
            - Name entities canonically and consistently (e.g. "Alex", not "he").
            - relations link two entities named in the same fact's entities list.
            {{knownBlock}}{{anchorBlock}}
            - Output STRICT JSON only, matching exactly this schema. No commentary, no markdown.

            {"facts":[{"content":"string","entities":[{"name":"string","kind":"person|place|org|concept|thing|event|project|other"}],"relations":[{"from":"string","relation":"string","to":"string"}],"timeAnchor":"string (optional)"}]}
            """;
    }

    /// <summary>
    /// Tolerant fact parse for channel + builtin models. Accepts root <c>facts</c>/<c>Facts</c>,
    /// a bare array of fact objects, and <c>content</c>/<c>text</c>/<c>fact</c> for the body.
    /// </summary>
    internal static List<ExtractedFact>? ParseFacts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement factsEl;
        if (root.ValueKind == JsonValueKind.Array)
            factsEl = root;
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (!TryGetPropertyIgnoreCase(root, "facts", out factsEl)
                || factsEl.ValueKind != JsonValueKind.Array)
            {
                // Single fact object at the root — wrap it.
                if (ReadFactContent(root) != null)
                    factsEl = root; // handled as one-shot below
                else
                    return null;
            }
        }
        else
            return null;

        var result = new List<ExtractedFact>();
        if (factsEl.ValueKind == JsonValueKind.Object)
        {
            var one = ReadFact(factsEl);
            if (one != null) result.Add(one);
            return result.Count == 0 ? null : result;
        }

        foreach (var f in factsEl.EnumerateArray())
        {
            var one = ReadFact(f);
            if (one != null) result.Add(one);
        }
        return result;
    }

    private static ExtractedFact? ReadFact(JsonElement f)
    {
        var factContent = ReadFactContent(f);
        if (string.IsNullOrWhiteSpace(factContent)) return null;

        var entities = new List<ExtractedEntity>();
        if (TryGetPropertyIgnoreCase(f, "entities", out var ents) && ents.ValueKind == JsonValueKind.Array)
            foreach (var e in ents.EnumerateArray())
            {
                // 1.2B models often emit ["Alice","Berlin"] instead of [{name,kind},…].
                if (e.ValueKind == JsonValueKind.String)
                {
                    var bare = e.GetString();
                    if (!string.IsNullOrWhiteSpace(bare))
                    {
                        var trimmed = bare.Trim();
                        entities.Add(new ExtractedEntity(trimmed, ResolveEntityKind(trimmed, null)));
                    }
                    continue;
                }
                var name = GetStringIgnoreCase(e, "name") ?? GetStringIgnoreCase(e, "entity");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var trimmedName = name.Trim();
                var kind = GetStringIgnoreCase(e, "kind");
                entities.Add(new ExtractedEntity(trimmedName, ResolveEntityKind(trimmedName, kind)));
            }

        var relations = new List<ExtractedRelation>();
        if (TryGetPropertyIgnoreCase(f, "relations", out var rels) && rels.ValueKind == JsonValueKind.Array)
            foreach (var r in rels.EnumerateArray())
            {
                var from = GetStringIgnoreCase(r, "from");
                var to = GetStringIgnoreCase(r, "to");
                var rel = GetStringIgnoreCase(r, "relation") ?? GetStringIgnoreCase(r, "type");
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(rel))
                    continue;
                relations.Add(new ExtractedRelation(from.Trim(), rel.Trim(), to.Trim()));
            }

        var timeAnchor = GetStringIgnoreCase(f, "timeAnchor") ?? GetStringIgnoreCase(f, "time_anchor");
        return new ExtractedFact(factContent.Trim(), entities, relations,
            string.IsNullOrWhiteSpace(timeAnchor) ? null : timeAnchor);
    }

    private static string? ReadFactContent(JsonElement f) =>
        GetStringIgnoreCase(f, "content")
        ?? GetStringIgnoreCase(f, "text")
        ?? GetStringIgnoreCase(f, "fact");

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? GetStringIgnoreCase(JsonElement obj, string name) =>
        TryGetPropertyIgnoreCase(obj, name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private async Task<NoosphereChannelResolver.ResolvedChannel?> ResolveAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var opts = await configService.GetExtractionOptionsAsync(ct);
        if (string.IsNullOrWhiteSpace(opts.Url))
            opts = legacyOptions.Value.Extraction; // fall back to legacy appsettings
        return await NoosphereChannelResolver.ResolveAsync(opts, db, ct);
    }

    private static async Task<(string? Content, string? Error)> PostChatCompletionAsync(
        NoosphereChannelResolver.ResolvedChannel channel, string systemPrompt, string userContent,
        double temperature, bool wantJsonMode, int maxTokens, CancellationToken ct)
    {
        async Task<HttpResponseMessage> SendAsync(bool withResponseFormat)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = channel.Model,
                ["temperature"] = temperature,
                ["max_tokens"] = maxTokens,
                // Best-effort: a reasoning-tuned model (e.g. a Qwen3-family "thinking" distill) emits a
                // long <think>...</think> preamble before any answer, for even a trivial extraction task
                // — turning a sub-second call into minutes of generation and backing up the whole ingest
                // queue behind it. This is the standard Qwen3 chat-template switch to skip that; servers
                // that don't recognize it just ignore the field, so it's harmless to always send.
                ["chat_template_kwargs"] = new { enable_thinking = false },
                ["messages"] = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent }
                }
            };
            if (withResponseFormat) payload["response_format"] = new { type = "json_object" };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{channel.Url}/chat/completions");
            if (!string.IsNullOrEmpty(channel.Key))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", channel.Key);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return await _http.SendAsync(req, ct);
        }

        try
        {
            var resp = await SendAsync(wantJsonMode);
            if (wantJsonMode && (int)resp.StatusCode is >= 400 and < 500)
            {
                resp.Dispose();
                resp = await SendAsync(false); // some local servers reject response_format
            }
            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    return (null, $"HTTP {(int)resp.StatusCode}: {NoosphereHttpError.ExtractMessage(body)}");
                using var doc = JsonDocument.Parse(body);
                var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                return (content, null);
            }
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    internal static string? TryExtractJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();

        // In case the model still emitted a reasoning block despite enable_thinking:false (server/model
        // support for that varies) — strip it so its braces can't confuse the first-{/last-} extraction
        // below. An unclosed <think> (truncated by max_tokens mid-thought) drops everything after it too,
        // which correctly yields "no JSON found" rather than picking up leftover fragments.
        var thinkStart = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (thinkStart >= 0)
        {
            var thinkEnd = text.IndexOf("</think>", thinkStart, StringComparison.OrdinalIgnoreCase);
            text = thinkEnd >= 0
                ? text[..thinkStart] + text[(thinkEnd + "</think>".Length)..]
                : text[..thinkStart];
            text = text.Trim();
        }

        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            var fenceEnd = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0) text = text[..fenceEnd];
            text = text.Trim();
        }

        // Prefer a root object; fall back to a root array (small models often emit [{...}] ).
        // Use balanced scanning — first-{/last-} grabs trailing junk the 1.2B model sometimes
        // appends after a valid object ("…} more text {…}"), which then fails JsonDocument.Parse.
        var objStart = text.IndexOf('{');
        var arrStart = text.IndexOf('[');
        if (objStart < 0 && arrStart < 0) return null;

        if (arrStart >= 0 && (objStart < 0 || arrStart < objStart))
            return SliceBalanced(text, arrStart, '[', ']');

        return SliceBalanced(text, objStart, '{', '}');
    }

    private static string? SliceBalanced(string text, int start, char open, char close)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escape) { escape = false; continue; }
                if (ch == '\\') { escape = true; continue; }
                if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') { inString = true; continue; }
            if (ch == open) depth++;
            else if (ch == close)
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return null;
    }

    private static string Snippet(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        var t = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length <= 180 ? t : t[..180] + "…";
    }

    private static readonly HashSet<string> KnownKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "person", "place", "org", "concept", "thing", "event", "project", "other"
    };

    // Common file suffixes the 1.2B model names as bare strings without a kind.
    private static readonly HashSet<string> FileLikeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "md", "txt", "py", "cs", "ts", "tsx", "js", "jsx", "json", "yaml", "yml", "toml",
        "rs", "go", "java", "kt", "rb", "php", "html", "css", "scss", "sql", "sh", "bash",
        "bat", "ps1", "gguf", "onnx", "dll", "exe", "so", "dylib", "png", "jpg", "jpeg",
        "svg", "pdf", "xml", "csv", "lock", "gradle", "swift", "m", "mm", "c", "h", "hpp",
        "cpp", "cc", "vue", "svelte", "wasm", "bin"
    };

    /// <summary>
    /// Normalize a model-supplied kind, or infer one from the entity name when the builtin
    /// 1.2B extract emits bare strings / omits kind. Always returns a known kind so the
    /// Memory canvas does not strand nodes in the untyped grey bucket.
    /// </summary>
    public static string ResolveEntityKind(string name, string? kind)
    {
        var normalized = NormalizeKind(kind);
        if (normalized != null) return normalized;

        var n = name.Trim();
        if (n.Length == 0) return "other";

        // Paths / filenames → thing (AGENTS.md, server.py, src/foo).
        if (n.Contains('/') || n.Contains('\\'))
            return "thing";
        var dot = n.LastIndexOf('.');
        if (dot > 0 && dot < n.Length - 1)
        {
            var ext = n[(dot + 1)..];
            if (FileLikeExtensions.Contains(ext))
                return "thing";
        }

        // Bare ports / numeric ids → concept (8001, 5741).
        if (n.Length is >= 2 and <= 5 && n.All(char.IsDigit))
            return "concept";

        // Kebab / snake slugs → project (data-repo, aria_agent). Skip sentence fragments.
        if (!n.Contains(' ')
            && n.Count(c => c is '-' or '_') is >= 1 and <= 4
            && n.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            return "project";

        return "other";
    }

    private static string? NormalizeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        var k = kind.Trim();
        // SoftRepair may quote model enum unions: "person|place" → take the first known token.
        foreach (var part in k.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (KnownKinds.Contains(part))
                return part.ToLowerInvariant();
        }
        if (KnownKinds.Contains(k))
            return k.ToLowerInvariant();
        return null;
    }

    /// <summary>
    /// Best-effort fixes for common small-model JSON mistakes before System.Text.Json parses.
    /// </summary>
    internal static string SoftRepairJson(string json)
    {
        // Trailing commas: {"a":1,} or [1,2,]
        var repaired = Regex.Replace(json, @",(\s*[\]}])", "$1");
        // Bare values after ':' — "kind": person|place  or  "url": http://x — quote them.
        repaired = Regex.Replace(
            repaired,
            @":\s*([A-Za-z_][A-Za-z0-9_/.|:-]*)\s*(?=[,}\]])",
            m =>
            {
                var v = m.Groups[1].Value;
                if (v is "true" or "false" or "null") return m.Value;
                if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                    return m.Value;
                return ": \"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            });
        return repaired;
    }
}
