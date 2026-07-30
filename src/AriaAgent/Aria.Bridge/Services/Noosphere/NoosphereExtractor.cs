using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aria.Bridge.Services.Noosphere;

// LLM extraction (Inscribe → atomic facts/entities/relations) and contemplate-synthesis, both via a
// single non-streaming chat/completions call over the configured extraction channel.
public class NoosphereExtractor(
    NoosphereConfigService configService,
    IOptions<NoosphereOptions> legacyOptions,
    IServiceScopeFactory scopeFactory)
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    // Reason the most recent extraction/contemplation call failed (null if it succeeded, or none has
    // run yet). Read by BuiltinTools.Memory / NoosphereService to turn a silent failure into an actual
    // error message — e.g. LM Studio's own "model not found" text — instead of a generic fallback.
    public string? LastError { get; private set; }
    public DateTime? LastErrorAt { get; private set; }
    private void Fail(string reason) { LastError = reason; LastErrorAt = DateTime.UtcNow; }

    public record ExtractedEntity(string Name, string? Kind);
    public record ExtractedRelation(string From, string Relation, string To);
    public record ExtractedFact(string Content, List<ExtractedEntity> Entities, List<ExtractedRelation> Relations, string? TimeAnchor);

    public async Task<List<ExtractedFact>?> ExtractAsync(
        string content,
        IReadOnlyList<(string Name, string? Kind)> knownEntities,
        IReadOnlyList<(string Name, string Description)> anchors,
        CancellationToken ct)
    {
        var channel = await ResolveAsync(ct);
        if (channel == null)
        {
            // Without this, Inscribe still "succeeds" and ProcessIngest falls back to raw text with
            // no LastError for BuiltinTools to elevate — the agent then claims the Archivum was sealed.
            Fail("No extraction channel configured or resolvable on this node — open the bridge Memory tab and set a working local model (e.g. LM Studio).");
            return null;
        }

        // Known-entities + anchors go directly above the schema — small local models lose
        // instructions placed far from the thing they govern, so the "reuse EXACT names" rule sits
        // right next to the list it applies to.
        var knownBlock = knownEntities.Count == 0 ? "" : $"""

            KNOWN ENTITIES already in the archive (reuse the EXACT name string when a mention refers
            to the same thing; only create a new entity for genuinely new things):
            {string.Join("\n", knownEntities.Select(e => $"- {e.Name} ({e.Kind ?? "other"})"))}

            """;
        var anchorBlock = anchors.Count == 0 ? "" : $"""

            ACTIVE PROJECTS (if the input concerns one of these projects, include the project itself
            as an entity of kind "project", using its exact name, in the relevant facts — otherwise
            do not mention it):
            {string.Join("\n", anchors.Select(a => $"- {a.Name} — {a.Description}"))}

            """;

        var system = $$"""
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

        var (raw, error) = await PostChatCompletionAsync(channel, system, content, temperature: 0.1, wantJsonMode: true, maxTokens: 2048, ct);
        if (error != null) { Fail(error); return null; }

        var json = TryExtractJson(raw);
        if (json == null)
        {
            Fail($"'{channel.Model}' on {channel.Url} returned no usable JSON — check it's an instruct model, not a \"thinking\"/reasoning one.");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("facts", out var facts) || facts.ValueKind != JsonValueKind.Array)
            {
                Fail($"'{channel.Model}' on {channel.Url} returned JSON without a 'facts' array.");
                return null;
            }

            var result = new List<ExtractedFact>();
            foreach (var f in facts.EnumerateArray())
            {
                var factContent = f.TryGetProperty("content", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(factContent)) continue;

                var entities = new List<ExtractedEntity>();
                if (f.TryGetProperty("entities", out var ents) && ents.ValueKind == JsonValueKind.Array)
                    foreach (var e in ents.EnumerateArray())
                    {
                        var name = e.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var kind = e.TryGetProperty("kind", out var k) ? k.GetString() : null;
                        entities.Add(new ExtractedEntity(name.Trim(), string.IsNullOrWhiteSpace(kind) ? null : kind));
                    }

                var relations = new List<ExtractedRelation>();
                if (f.TryGetProperty("relations", out var rels) && rels.ValueKind == JsonValueKind.Array)
                    foreach (var r in rels.EnumerateArray())
                    {
                        var from = r.TryGetProperty("from", out var fr) ? fr.GetString() : null;
                        var to   = r.TryGetProperty("to", out var to_) ? to_.GetString() : null;
                        var rel  = r.TryGetProperty("relation", out var rl) ? rl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(rel))
                            continue;
                        relations.Add(new ExtractedRelation(from.Trim(), rel.Trim(), to.Trim()));
                    }

                var timeAnchor = f.TryGetProperty("timeAnchor", out var ta) ? ta.GetString() : null;
                result.Add(new ExtractedFact(factContent.Trim(), entities, relations,
                    string.IsNullOrWhiteSpace(timeAnchor) ? null : timeAnchor));
            }
            if (result.Count == 0) return null;
            LastError = null;
            return result;
        }
        catch (Exception ex)
        {
            Fail($"'{channel.Model}' on {channel.Url} returned unparseable JSON: {ex.Message}");
            return null;
        }
    }

    public async Task<string?> ContemplateSynthesisAsync(string query, string probedText, CancellationToken ct)
    {
        var channel = await ResolveAsync(ct);
        if (channel == null)
        {
            Fail("No extraction channel configured or resolvable on this node — open the bridge Memory tab and set a working local model (e.g. LM Studio).");
            return null;
        }

        const string system =
            "You are the Noosphere contemplation cogitator of an Imperial archive. Answer the question " +
            "drawing only on the probed engrams below. If the archive holds nothing relevant, say so " +
            "plainly — do not invent facts.";
        var user = $"Probed engrams:\n{probedText}\n\nQuestion: {query}";

        var (text, error) = await PostChatCompletionAsync(channel, system, user, temperature: 0.3, wantJsonMode: false, maxTokens: 1024, ct);
        if (error != null) { Fail(error); return null; }
        LastError = null;
        return text;
    }

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

    private static string? TryExtractJson(string? raw)
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
        }
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start < 0 || end < 0 || end <= start ? null : text[start..(end + 1)];
    }
}
