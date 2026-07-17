using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Services.Logging;
using Aria.Shared;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// Node-authoritative LLM channels. Channels (name → URL, models, key) live ONLY on this node and are
/// authored ONLY here (bridge status page / local-origin requests). The server never authors a channel
/// and never supplies the destination URL for a call — <c>/llm/proxy</c> resolves the host from these
/// records (or <see cref="PublicProviderCatalog"/> for public providers), so a compromised server can
/// neither redirect a stored key nor poison a channel URL.
///
/// - <c>GET /channels</c> is a read-only mirror the web fetches over the tunnel (names, models, key
///   presence). It never returns key material.
/// - <c>PUT/DELETE /channels/{name}</c> are local-origin only and deliberately kept OUT of the tunnel
///   allowlist, so the server cannot create, edit, or delete channels.
/// </summary>
public static class ChannelEndpoints
{
    public static void MapChannelEndpoints(this WebApplication app)
    {
        // GET /channels — the authoritative channel list: seeded public providers merged with custom
        // node-authored channels, each annotated with whether a key is stored. No key values are returned.
        app.MapGet("/channels", async (BridgeDbContext db) =>
        {
            var configured = await GetConfiguredProviderNamesAsync(db);
            var custom = await db.Channels.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();

            var byName = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in PublicProviderCatalog.Providers)
                byName[p.Name] = new
                {
                    name      = p.Name,
                    url       = p.CanonicalUrl,
                    models    = p.DefaultModels,
                    isBridged = true,
                    isPublic  = true,
                    hasKey    = configured.Contains(p.Name),
                };

            foreach (var c in custom)
                byName[c.Name] = new
                {
                    name      = c.Name,
                    url       = c.Url,
                    models    = ParseModels(c.ModelsJson),
                    isBridged = c.IsBridged,
                    isPublic  = false,
                    hasKey    = configured.Contains(c.Name),
                };

            return Results.Ok(new { channels = byName.Values });
        });

        // PUT /channels/{name} — create/update a CUSTOM channel. Local-origin only (not tunnel-relayable).
        // Public-provider names are reserved: their URL is catalog-fixed and cannot be authored here.
        app.MapPut("/channels/{name}", async (string name, SaveChannelRequest req, BridgeDbContext db) =>
        {
            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest("channel name required");
            if (PublicProviderCatalog.IsPublic(name))
                return Results.BadRequest($"'{name}' is a public provider — its URL is fixed. Store a key to enable it.");
            if (string.IsNullOrWhiteSpace(req.Url))
                return Results.BadRequest("url required");

            var modelsJson = JsonSerializer.Serialize(
                (req.Models ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToArray());

            var existing = await db.Channels.FirstOrDefaultAsync(c => c.Name == name);
            if (existing == null)
            {
                var maxSort = await db.Channels.AnyAsync() ? await db.Channels.MaxAsync(c => c.SortOrder) : 0;
                db.Channels.Add(new BridgeChannel
                {
                    Name       = name,
                    Url        = req.Url.Trim(),
                    ModelsJson = modelsJson,
                    IsBridged  = req.IsBridged ?? true,
                    SortOrder  = maxSort + 1,
                });
            }
            else
            {
                existing.Url        = req.Url.Trim();
                existing.ModelsJson = modelsJson;
                existing.IsBridged  = req.IsBridged ?? existing.IsBridged;
            }

            await db.SaveChangesAsync();
            BridgeLogger.Log("INFO", $"Channel saved: {name}");
            return Results.Ok(new { ok = true });
        });

        // DELETE /channels/{name} — remove a custom channel. Local-origin only.
        app.MapDelete("/channels/{name}", async (string name, BridgeDbContext db) =>
        {
            name = name.Trim();
            var existing = await db.Channels.FirstOrDefaultAsync(c => c.Name == name);
            if (existing != null)
            {
                db.Channels.Remove(existing);
                await db.SaveChangesAsync();
                BridgeLogger.Log("INFO", $"Channel deleted: {name}");
            }
            return Results.Ok(new { ok = true });
        });
    }

    public static string[] ParseModels(string modelsJson)
    {
        try { return JsonSerializer.Deserialize<string[]>(modelsJson) ?? []; }
        catch { return []; }
    }

    private static async Task<HashSet<string>> GetConfiguredProviderNamesAsync(BridgeDbContext db)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Provider FROM LlmKeys;";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) names.Add(r.GetString(0));
        }
        finally { await conn.CloseAsync(); }
        return names;
    }
}

public record SaveChannelRequest(string Url, string[]? Models, bool? IsBridged);
