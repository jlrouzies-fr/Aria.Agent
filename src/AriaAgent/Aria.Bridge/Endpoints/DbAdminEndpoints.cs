using Aria.Bridge.Data;
using Aria.Bridge.Infrastructure;
using Aria.Bridge.Services.Logging;
using Aria.Bridge.Services.Noosphere;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aria.Bridge.Endpoints;

public static class DbAdminEndpoints
{
    public static void MapDbAdminEndpoints(this WebApplication app)
    {
        // DB diagnostics — path, file size, record counts.
        app.MapGet("/db-info", async (BridgeDbContext db) =>
        {
            var soulCount = await db.Souls.CountAsync();
            var cogCount  = await db.Cogitations.CountAsync();
            var msgCount  = await db.Messages.CountAsync();
            var info = new FileInfo(BridgeDatabaseInitializer.DbPath);
            return Results.Ok(new
            {
                path        = BridgeDatabaseInitializer.DbPath,
                sizeBytes   = info.Exists ? info.Length : 0,
                souls       = soulCount,
                cogitations = cogCount,
                messages    = msgCount,
            });
        });

        // Wipe all cogitations + their messages.
        app.MapDelete("/db/cogitations", async (BridgeDbContext db) =>
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Messages;");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Cogitations;");
            BridgeLogger.Log("WARN", "All cogitations wiped by user.");
            return Results.Ok(new { ok = true });
        });

        // Wipe messages only (keep cogitation shells).
        app.MapDelete("/db/messages", async (BridgeDbContext db) =>
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Messages;");
            BridgeLogger.Log("WARN", "All messages wiped by user.");
            return Results.Ok(new { ok = true });
        });

        // Wipe all Noosphere memory (engrams, entities, relations, anchors, pending ingests).
        // Soul identity and cogitations are untouched.
        app.MapDelete("/db/noosphere", async (BridgeDbContext db, NoosphereService svc) =>
        {
            await WipeNoosphereTablesAsync(db);
            svc.ClearAllCaches();
            BridgeLogger.Log("WARN", "Noosphere memory wiped by user.");
            return Results.Ok(new { ok = true });
        });

        // Wipe soul identity (keypair, server link, name — full reset).
        // Best-effort: notifies the linked server first so it can null the public key,
        // allowing re-registration under the same name with a fresh keypair.
        // Local wipe always succeeds even if the server is unreachable.
        app.MapDelete("/db/soul", async (BridgeDbContext db, NoosphereService svc) =>
        {
            var soul = await db.Souls.FirstOrDefaultAsync(s => s.Name != "")
                       ?? await db.Souls.FirstOrDefaultAsync();

            if (soul?.ServerUrl != null && soul.PrivateKeyBase64 != null && soul.PublicKeyBase64 != null)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var baseUrl = soul.ServerUrl.TrimEnd('/');

                    // 1. Ask the server for a fresh single-use challenge nonce (replay protection).
                    var chPayload = JsonSerializer.Serialize(new { publicKey = soul.PublicKeyBase64 });
                    var chResp = await http.PostAsync(baseUrl + "/api/bridge/unlink-challenge",
                        new StringContent(chPayload, Encoding.UTF8, "application/json"));
                    chResp.EnsureSuccessStatusCode();
                    using var chDoc = JsonDocument.Parse(await chResp.Content.ReadAsStringAsync());
                    var nonce = Convert.FromBase64String(chDoc.RootElement.GetProperty("nonceBase64").GetString()!);

                    // 2. Sign the server's nonce with the soul's private key.
                    using var ecdsa = ECDsa.Create();
                    ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(soul.PrivateKeyBase64), out _);
                    var sig = ecdsa.SignData(nonce, HashAlgorithmName.SHA256);

                    // 3. Unlink with the signature over the server-issued nonce.
                    var payload = JsonSerializer.Serialize(new
                    {
                        publicKey       = soul.PublicKeyBase64,
                        signatureBase64 = Convert.ToBase64String(sig),
                    });
                    var resp = await http.PostAsync(baseUrl + "/api/bridge/unlink-soul",
                        new StringContent(payload, Encoding.UTF8, "application/json"));
                    BridgeLogger.Log("INFO", $"Server unlink: {(int)resp.StatusCode}");
                }
                catch (Exception ex)
                {
                    BridgeLogger.Log("WARN", $"Server unlink failed (will still wipe locally): {ex.Message}");
                }
            }

            // SQLite doesn't enforce FK cascade unless PRAGMA foreign_keys = ON,
            // so delete in dependency order explicitly.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Messages;");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Cogitations;");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Contacts;");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM LlmKeys;");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM BridgeOAuthTokens;");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM ServerLinks;");
            // Noosphere memories are personal data in the vault — a full reset (e.g. before handing
            // over the machine) must remove them too, not just chat history and keys.
            await WipeNoosphereTablesAsync(db);
            svc.ClearAllCaches();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Souls;");
            BridgeLogger.Log("WARN", "Soul identity wiped. Re-open bridge to create a new soul.");
            return Results.Ok(new { ok = true });
        });
    }

    private static async Task WipeNoosphereTablesAsync(BridgeDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM EntityLinks;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM EngramEntities;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Engrams;"); // AFTER DELETE trigger clears EngramsFts
        await db.Database.ExecuteSqlRawAsync("DELETE FROM MemoryEntities;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM MemoryIngests;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM MemoryAnchors;");
    }
}
