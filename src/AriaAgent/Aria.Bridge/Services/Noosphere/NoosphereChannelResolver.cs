using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Services.Llm;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Services.Noosphere;

// Resolves the effective (url, model, key) for an extraction/embedding call. Falls back to the
// first bridged SyncedLocalSource when no explicit Noosphere URL is configured, so a freshly-linked
// soul gets extraction/embeddings "for free" from whatever local model channel is already set up.
public static class NoosphereChannelResolver
{
    public record ResolvedChannel(string Url, string Model, string? Key);

    public static async Task<ResolvedChannel?> ResolveAsync(
        NoosphereChannelOptions opts, BridgeDbContext db, CancellationToken ct)
    {
        var url   = opts.Url;
        var model = opts.Model;
        var keyRef = opts.KeyRef;

        if (string.IsNullOrWhiteSpace(url))
        {
            var src = await db.SyncedLocalSources.AsNoTracking()
                .Where(s => s.IsBridged)
                .OrderBy(s => s.SortOrder)
                .FirstOrDefaultAsync(ct);
            if (src == null) return null;

            url = src.Url;
            keyRef ??= src.Name; // same "key stored under the source's name" convention as a custom channel
            if (string.IsNullOrWhiteSpace(model))
            {
                try
                {
                    var models = JsonSerializer.Deserialize<List<string>>(src.ModelsJson) ?? [];
                    model = models.FirstOrDefault() ?? "";
                }
                catch { model = ""; }
            }
        }

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(model))
            return null;

        string? key = null;
        if (!string.IsNullOrEmpty(keyRef))
            key = await LlmKeyStore.GetPlaintextKeyAsync(db, keyRef);
        if (key == null && !string.IsNullOrEmpty(opts.ApiKeyFile) && File.Exists(opts.ApiKeyFile))
            key = (await File.ReadAllTextAsync(opts.ApiKeyFile, ct)).Trim();

        return new ResolvedChannel(url.TrimEnd('/'), model, key);
    }
}
