using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Services.Logging;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Services.Noosphere;

/// <summary>
/// Node-authoritative configuration for Noosphere memory: which existing channel to use for extraction
/// and (optionally) embeddings. Persists the choice in the local SQLite vault and resolves it into
/// the same <see cref="NoosphereChannelOptions"/> shape the rest of the subsystem already expects.
/// </summary>
public sealed class NoosphereConfigService(IServiceScopeFactory scopeFactory)
{
    public record SaveRequest(string? ExtractionChannelName, string? EmbeddingsChannelName, bool EmbeddingsEnabled, string? EmbeddingsModel = null, string? ExtractionModel = null);
    public record SaveBuiltinRequest(bool Enabled, bool AcceptLicense = false);

    public async Task<NoosphereConfig> GetConfigAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var config = await db.NoosphereConfigs.AsNoTracking().FirstOrDefaultAsync(ct);
        if (config == null)
        {
            config = new NoosphereConfig { EmbeddingsEnabled = true };
        }
        return config;
    }

    public async Task SaveConfigAsync(SaveRequest req, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var config = await db.NoosphereConfigs.FirstOrDefaultAsync(ct);
        if (config == null)
        {
            config = new NoosphereConfig();
            db.NoosphereConfigs.Add(config);
        }
        config.ExtractionChannelName = string.IsNullOrWhiteSpace(req.ExtractionChannelName) ? null : req.ExtractionChannelName.Trim();
        config.EmbeddingsChannelName = string.IsNullOrWhiteSpace(req.EmbeddingsChannelName) ? null : req.EmbeddingsChannelName.Trim();
        config.EmbeddingsEnabled = req.EmbeddingsEnabled;
        config.EmbeddingsModel = string.IsNullOrWhiteSpace(req.EmbeddingsModel) ? null : req.EmbeddingsModel.Trim();
        config.ExtractionModel = string.IsNullOrWhiteSpace(req.ExtractionModel) ? null : req.ExtractionModel.Trim();
        await db.SaveChangesAsync(ct);
        BridgeLogger.Log("INFO", $"Noosphere config saved: extraction={config.ExtractionChannelName ?? "auto"} model={config.ExtractionModel ?? "auto"}, embeddings={config.EmbeddingsChannelName ?? "auto"} model={config.EmbeddingsModel ?? "auto"} (enabled={config.EmbeddingsEnabled})");
    }

    public async Task SaveBuiltinConfigAsync(SaveBuiltinRequest req, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var config = await db.NoosphereConfigs.FirstOrDefaultAsync(ct);
        if (config == null)
        {
            config = new NoosphereConfig { EmbeddingsEnabled = true };
            db.NoosphereConfigs.Add(config);
        }
        config.BuiltinEnabled = req.Enabled;
        if (req.AcceptLicense && config.BuiltinLicenseAcceptedAt == null)
            config.BuiltinLicenseAcceptedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        BridgeLogger.Log("INFO",
            $"Noosphere builtin config saved: enabled={config.BuiltinEnabled}, licenseAccepted={config.BuiltinLicenseAcceptedAt != null}");
    }

    /// <summary>True when the node should use in-process models instead of HTTP channels.</summary>
    public async Task<bool> IsBuiltinActiveAsync(NoosphereBuiltinRuntime runtime, CancellationToken ct)
    {
        var config = await GetConfigAsync(ct);
        return config.BuiltinEnabled && runtime.IsReady;
    }

    /// <summary>
    /// Builds effective extraction options from the configured channel. If no channel is selected,
    /// returns an empty options object so <see cref="NoosphereChannelResolver"/> falls back to the
    /// first bridged local source. <see cref="NoosphereConfig.ExtractionModel"/> is a free-text
    /// override — same rationale as the embeddings model override below: a channel is just a URL+key
    /// and may serve several models, so "whichever model happens to be first in the channel's list"
    /// is not a real choice.
    /// </summary>
    public async Task<NoosphereChannelOptions> GetExtractionOptionsAsync(CancellationToken ct)
    {
        var config = await GetConfigAsync(ct);
        var opts = await ResolveOptionsAsync(extraction: true, ct);
        if (!string.IsNullOrWhiteSpace(config.ExtractionModel))
            opts.Model = config.ExtractionModel;
        return opts;
    }

    public async Task<NoosphereEmbeddingOptions> GetEmbeddingOptionsAsync(CancellationToken ct)
    {
        var config = await GetConfigAsync(ct);
        var opts = await ResolveOptionsAsync(extraction: false, ct);
        return new NoosphereEmbeddingOptions
        {
            Url = opts.Url,
            // A channel is just a URL+key — its "model" list is whatever chat models it serves, which
            // is not necessarily embedding-capable. The embeddings model is a free-text override for
            // exactly that reason (e.g. one LM Studio instance can load a chat model for extraction and
            // a separate, purpose-built embedding model at the same URL).
            Model = !string.IsNullOrWhiteSpace(config.EmbeddingsModel) ? config.EmbeddingsModel : opts.Model,
            KeyRef = opts.KeyRef,
            ApiKeyFile = opts.ApiKeyFile,
            Enabled = config.EmbeddingsEnabled
        };
    }

    public async Task<bool> IsExtractionConfiguredAsync(CancellationToken ct)
    {
        // Caller without runtime: channel path only. StatsAsync uses the overload with runtime.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        return await NoosphereChannelResolver.ResolveAsync(await GetExtractionOptionsAsync(ct), db, ct) is not null;
    }

    public async Task<bool> IsExtractionConfiguredAsync(NoosphereBuiltinRuntime runtime, CancellationToken ct)
    {
        if (await IsBuiltinActiveAsync(runtime, ct)) return true;
        return await IsExtractionConfiguredAsync(ct);
    }

    public async Task<bool> IsEmbeddingsConfiguredAsync(CancellationToken ct)
    {
        var opts = await GetEmbeddingOptionsAsync(ct);
        if (!opts.Enabled) return false;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        return await NoosphereChannelResolver.ResolveAsync(opts, db, ct) is not null;
    }

    public async Task<bool> IsEmbeddingsConfiguredAsync(NoosphereBuiltinRuntime runtime, CancellationToken ct)
    {
        var opts = await GetEmbeddingOptionsAsync(ct);
        if (!opts.Enabled) return false;
        if (await IsBuiltinActiveAsync(runtime, ct)) return true;
        return await IsEmbeddingsConfiguredAsync(ct);
    }

    private async Task<NoosphereChannelOptions> ResolveOptionsAsync(bool extraction, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var config = await db.NoosphereConfigs.AsNoTracking().FirstOrDefaultAsync(ct);
        var channelName = extraction
            ? config?.ExtractionChannelName
            : config?.EmbeddingsChannelName;

        if (string.IsNullOrWhiteSpace(channelName))
            return new NoosphereChannelOptions(); // triggers SyncedLocalSource fallback in resolver

        // Prefer a node-authored custom channel, then a public provider if a key is stored.
        // KeyRef = the channel's own name: LlmKeys stores provider keys under exactly that name (see
        // LlmKeyEndpoints/LlmKeyStore), so a bridged channel that needs auth — e.g. a local LM Studio
        // instance with its "require API token" setting on — actually gets its stored key sent instead
        // of silently going out unauthenticated.
        var custom = await db.Channels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Name == channelName, ct);
        if (custom != null)
        {
            return new NoosphereChannelOptions
            {
                Url = custom.Url,
                Model = FirstModel(custom.ModelsJson),
                KeyRef = custom.Name,
                ApiKeyFile = null
            };
        }

        var publicProvider = Aria.Shared.PublicProviderCatalog.Providers
            .FirstOrDefault(p => string.Equals(p.Name, channelName, StringComparison.OrdinalIgnoreCase));
        if (publicProvider != null)
        {
            return new NoosphereChannelOptions
            {
                Url = publicProvider.CanonicalUrl,
                Model = publicProvider.DefaultModels.FirstOrDefault() ?? "",
                KeyRef = publicProvider.Name
            };
        }

        // Unknown channel name — leave empty so the resolver falls back instead of failing silently.
        return new NoosphereChannelOptions();
    }

    private static string FirstModel(string modelsJson)
    {
        try
        {
            var models = JsonSerializer.Deserialize<List<string>>(modelsJson) ?? [];
            return models.FirstOrDefault() ?? "";
        }
        catch { return ""; }
    }
}
