using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Infrastructure;
using Aria.Bridge.Services.Logging;
using Aria.Bridge.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

public static class SoulEndpoints
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static void MapSoulEndpoints(this WebApplication app)
    {
        // GET /soul — current soul, or 404 if not yet created
        app.MapGet("/soul", async (BridgeDbContext db) =>
        {
            var soul = await db.Souls
                .AsNoTracking()
                .Include(s => s.ServerLinks)
                .FirstOrDefaultAsync(s => s.ServerSoulId != null || s.Name != "")
                ?? await db.Souls.AsNoTracking().Include(s => s.ServerLinks).FirstOrDefaultAsync();
            return soul is null ? Results.NotFound() : Results.Ok(ToDto(soul));
        });

        // POST /soul — create soul (one primary soul per bridge; fails if one already named)
        app.MapPost("/soul", async (CreateSoulRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest("Name is required");

            if (await db.Souls.AnyAsync(s => s.Name != ""))
                return Results.Conflict("Soul already exists — use PUT /soul to update");

            var (pub, priv) = GenerateKeypair();
            var soul = new BridgeSoul
            {
                Name             = req.Name.Trim(),
                AvatarSpriteKey  = req.AvatarSpriteKey,
                AccentColor      = req.AccentColor,
                PublicKeyBase64  = pub,
                PrivateKeyBase64 = priv,
            };
            db.Souls.Add(soul);
            await db.SaveChangesAsync();
            return Results.Created("/soul", ToDto(soul));
        });

        // PUT /soul — update name / avatar / accent
        app.MapPut("/soul", async (UpdateSoulRequest req, BridgeDbContext db) =>
        {
            var soul = await db.Souls.FirstOrDefaultAsync(s => s.Name != "")
                       ?? await db.Souls.FirstOrDefaultAsync();
            if (soul is null) return Results.NotFound();

            if (req.Name is not null)            soul.Name            = req.Name.Trim();
            if (req.AvatarSpriteKey is not null) soul.AvatarSpriteKey = req.AvatarSpriteKey;
            if (req.AccentColor is not null)     soul.AccentColor     = req.AccentColor;
            if (req.ServerSoulId is not null)    soul.ServerSoulId    = req.ServerSoulId;

            // Generate keypair if missing
            if (soul.PublicKeyBase64 is null)
            {
                var (pub, priv) = GenerateKeypair();
                soul.PublicKeyBase64  = pub;
                soul.PrivateKeyBase64 = priv;
            }

            await db.SaveChangesAsync();
            return Results.Ok(ToDto(soul));
        });

        // GET /soul/pubkey — returns the public key only (safe to display/copy)
        app.MapGet("/soul/pubkey", async (BridgeDbContext db) =>
        {
            var soul = await db.Souls.AsNoTracking().FirstOrDefaultAsync(s => s.Name != "")
                       ?? await db.Souls.AsNoTracking().FirstOrDefaultAsync();
            return soul is null
                ? Results.NotFound()
                : Results.Ok(new { publicKey = soul.PublicKeyBase64 });
        });

        // POST /soul/keypair — (re)generate keypair for the primary soul
        app.MapPost("/soul/keypair", async (BridgeDbContext db) =>
        {
            var soul = await db.Souls.FirstOrDefaultAsync(s => s.Name != "")
                       ?? await db.Souls.FirstOrDefaultAsync();
            if (soul is null) return Results.NotFound();

            var (pub, priv)   = GenerateKeypair();
            soul.PublicKeyBase64  = pub;
            soul.PrivateKeyBase64 = priv;
            await db.SaveChangesAsync();
            return Results.Ok(new { publicKey = pub });
        });

        // POST /soul/link-server — register this soul with an Aria.Web server instance.
        // Body: { serverUrl: "http://...", sealId: "..." }
        // Requires a fresh, capability-bound Inquisitorial Seal approved at this node (F-5).
        app.MapPost("/soul/link-server", async (HttpRequest httpReq, LinkServerRequest req, BridgeDbContext db, DirectTunnel tunnel, SecurityAuditLog audit) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(httpReq))
            {
                audit.Record("soul", "link-server-denied", allowed: false, capability: "soul-link-server",
                    detail: "non-local origin");
                return Results.Json(new { error = "Server linking must be performed from the local bridge UI." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(req.SealId))
                return Results.BadRequest(new { error = "An approved seal id is required." });

            if (!SealEndpoints.TryConsumeSeal(req.SealId, "soul-link-server"))
            {
                audit.Record("soul", "link-server-denied", allowed: false, capability: "soul-link-server",
                    detail: "seal missing/not approved/wrong capability/already used");
                return Results.Json(new { error = "Seal missing, not approved, wrong capability, or already used." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(req.ServerUrl))
                return Results.BadRequest("ServerUrl required");

            var serverUrl = NormalizeServerUrl(req.ServerUrl);
            var soul = await db.Souls
                .Include(s => s.ServerLinks)
                .FirstOrDefaultAsync(s => s.Name != "")
                ?? await db.Souls.Include(s => s.ServerLinks).FirstOrDefaultAsync();
            if (soul is null) return Results.NotFound("No soul — create one first");

            // Ensure keypair exists
            if (soul.PublicKeyBase64 is null)
            {
                var (pub, priv)   = GenerateKeypair();
                soul.PublicKeyBase64  = pub;
                soul.PrivateKeyBase64 = priv;
                await db.SaveChangesAsync();
            }

            var payload = JsonSerializer.Serialize(new
            {
                name            = soul.Name,
                publicKey       = soul.PublicKeyBase64,
                avatarSpriteKey = soul.AvatarSpriteKey,
                accentColor     = soul.AccentColor,
            });

            try
            {
                var url  = serverUrl + "/api/bridge/register-soul";
                var resp = await _http.PostAsync(url,
                    new StringContent(payload, Encoding.UTF8, "application/json"));
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return Results.Problem($"Server rejected registration ({(int)resp.StatusCode}): {body}");

                using var doc = JsonDocument.Parse(body);
                var serverId = doc.RootElement.GetProperty("serverSoulId").GetString();

                // Upsert the saved server link.
                var link = soul.ServerLinks.FirstOrDefault(l => l.ServerUrl == serverUrl);
                if (link is null)
                {
                    link = new BridgeServerLink { SoulId = soul.Id, ServerUrl = serverUrl };
                    soul.ServerLinks.Add(link);
                }
                link.ServerSoulId = serverId!;

                // Activate this link on the soul (mirrored columns used by the tunnel).
                soul.ServerSoulId = serverId;
                soul.ServerUrl    = serverUrl;
                await db.SaveChangesAsync();

                // Force the tunnel loop to reconnect to the new server.
                tunnel.RequestReconnect();

                audit.Record("soul", "link-server", allowed: true, capability: "soul-link-server",
                    detail: $"serverUrl={serverUrl} serverSoulId={serverId}");
                return Results.Ok(new { ok = true, serverSoulId = serverId, serverUrl = serverUrl });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Could not reach server at {serverUrl}: {ex.Message}");
            }
        });

        // POST /soul/switch-server — activate a previously saved server link.
        // Body: { serverSoulId: "...", sealId: "..." }
        // Requires a fresh, capability-bound Inquisitorial Seal approved at this node (F-5).
        app.MapPost("/soul/switch-server", async (HttpRequest httpReq, SwitchServerRequest req, BridgeDbContext db, DirectTunnel tunnel, SecurityAuditLog audit) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(httpReq))
            {
                audit.Record("soul", "switch-server-denied", allowed: false, capability: "soul-switch-server",
                    detail: "non-local origin");
                return Results.Json(new { error = "Server switching must be performed from the local bridge UI." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(req.SealId))
                return Results.BadRequest(new { error = "An approved seal id is required." });

            if (!SealEndpoints.TryConsumeSeal(req.SealId, "soul-switch-server"))
            {
                audit.Record("soul", "switch-server-denied", allowed: false, capability: "soul-switch-server",
                    detail: "seal missing/not approved/wrong capability/already used");
                return Results.Json(new { error = "Seal missing, not approved, wrong capability, or already used." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(req.ServerSoulId))
                return Results.BadRequest("ServerSoulId required");

            var soul = await db.Souls
                .Include(s => s.ServerLinks)
                .FirstOrDefaultAsync(s => s.Name != "")
                ?? await db.Souls.Include(s => s.ServerLinks).FirstOrDefaultAsync();
            if (soul is null) return Results.NotFound();

            var link = soul.ServerLinks.FirstOrDefault(l => l.ServerSoulId == req.ServerSoulId);
            if (link is null)
                return Results.BadRequest("Server link not found — register it first");

            soul.ServerSoulId = link.ServerSoulId;
            soul.ServerUrl    = link.ServerUrl;
            await db.SaveChangesAsync();

            tunnel.RequestReconnect();

            audit.Record("soul", "switch-server", allowed: true, capability: "soul-switch-server",
                detail: $"serverSoulId={link.ServerSoulId} serverUrl={link.ServerUrl}");
            return Results.Ok(new { ok = true, serverSoulId = link.ServerSoulId, serverUrl = link.ServerUrl });
        });

        // POST /soul/unlink — clear the active server association and disconnect the tunnel.
        // Saved server links are preserved so they can be switched back later.
        // Body: { sealId: "..." }
        // Requires a fresh, capability-bound Inquisitorial Seal approved at this node (F-5).
        app.MapPost("/soul/unlink", async (HttpRequest httpReq, UnlinkRequest req, BridgeDbContext db, DirectTunnel tunnel, SecurityAuditLog audit) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(httpReq))
            {
                audit.Record("soul", "unlink-denied", allowed: false, capability: "soul-unlink",
                    detail: "non-local origin");
                return Results.Json(new { error = "Server unlink must be performed from the local bridge UI." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(req.SealId))
                return Results.BadRequest(new { error = "An approved seal id is required." });

            if (!SealEndpoints.TryConsumeSeal(req.SealId, "soul-unlink"))
            {
                audit.Record("soul", "unlink-denied", allowed: false, capability: "soul-unlink",
                    detail: "seal missing/not approved/wrong capability/already used");
                return Results.Json(new { error = "Seal missing, not approved, wrong capability, or already used." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var soul = await db.Souls
                .Include(s => s.ServerLinks)
                .FirstOrDefaultAsync(s => s.Name != "")
                ?? await db.Souls.Include(s => s.ServerLinks).FirstOrDefaultAsync();
            if (soul is null) return Results.NotFound();

            soul.ServerSoulId = null;
            soul.ServerUrl    = null;
            await db.SaveChangesAsync();

            tunnel.RequestReconnect();

            audit.Record("soul", "unlink", allowed: true, capability: "soul-unlink");
            return Results.Ok(ToDto(soul));
        });

        // DELETE /soul/server-link — remove a saved server link.
        app.MapDelete("/soul/server-link", async (string id, BridgeDbContext db, DirectTunnel tunnel) =>
        {
            var soul = await db.Souls
                .Include(s => s.ServerLinks)
                .FirstOrDefaultAsync(s => s.Name != "")
                ?? await db.Souls.Include(s => s.ServerLinks).FirstOrDefaultAsync();
            if (soul is null) return Results.NotFound();

            var link = soul.ServerLinks.FirstOrDefault(l => l.Id == id);
            if (link is null) return Results.NotFound();

            var wasActive = soul.ServerSoulId == link.ServerSoulId && soul.ServerUrl == link.ServerUrl;
            soul.ServerLinks.Remove(link);
            if (wasActive)
            {
                // Fall back to the most-recent remaining link, or clear if none.
                var next = soul.ServerLinks.OrderByDescending(l => l.CreatedAt).FirstOrDefault();
                soul.ServerSoulId = next?.ServerSoulId;
                soul.ServerUrl    = next?.ServerUrl;
                tunnel.RequestReconnect();
            }
            await db.SaveChangesAsync();

            return Results.Ok(ToDto(soul));
        });

        // POST /soul/sign — sign a nonce with the soul's private key (challenge-response)
        app.MapPost("/soul/sign", async (SignRequest req, BridgeDbContext db) =>
        {
            var soul = await db.Souls.AsNoTracking().FirstOrDefaultAsync(s => s.Name != "")
                       ?? await db.Souls.AsNoTracking().FirstOrDefaultAsync();
            if (soul?.PrivateKeyBase64 is null) return Results.NotFound("No soul or keypair");

            try
            {
                var nonce = Convert.FromBase64String(req.NonceBase64);
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(soul.PrivateKeyBase64), out _);
                var sig = ecdsa.SignData(nonce, HashAlgorithmName.SHA256);
                return Results.Ok(new { signatureBase64 = Convert.ToBase64String(sig) });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Signing failed: {ex.Message}");
            }
        });

        // POST /soul/export — passphrase-encrypted backup of the soul master key + server link.
        // Local-human-only ceremony: requires a fresh, capability-bound Inquisitorial Seal approved
        // at this node, and the request must come from the bridge's own loopback UI (not a
        // cross-origin page and not the server tunnel — see F-2 allowlist).
        app.MapPost("/soul/export", async (HttpRequest httpReq, ExportSoulRequest req, BridgeDbContext db, SecurityAuditLog audit) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(httpReq))
            {
                audit.Record("soul", "export-denied", allowed: false, capability: "soul-export",
                    detail: "non-local origin");
                return Results.Json(new { error = "Soul export must be performed from the local bridge UI." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(req.SealId))
                return Results.BadRequest(new { error = "An approved seal id is required." });

            if (!SealEndpoints.TryConsumeSeal(req.SealId, "soul-export"))
            {
                audit.Record("soul", "export-denied", allowed: false, capability: "soul-export",
                    detail: "seal missing/not approved/wrong capability/already used");
                return Results.Json(new { error = "Seal missing, not approved, wrong capability, or already used." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrEmpty(req.Passphrase) || req.Passphrase.Length < 8)
                return Results.BadRequest(new { error = "Passphrase of at least 8 characters required." });

            var soul = await db.Souls.AsNoTracking().FirstOrDefaultAsync(s => s.Name != "")
                       ?? await db.Souls.AsNoTracking().FirstOrDefaultAsync();
            if (soul?.PrivateKeyBase64 is null) return Results.NotFound("No soul master key to export");

            var json = JsonSerializer.Serialize(new
            {
                name         = soul.Name,
                serverSoulId = soul.ServerSoulId,
                serverUrl    = soul.ServerUrl,
                publicKey    = soul.PublicKeyBase64,
                privateKey   = soul.PrivateKeyBase64,
                dataKey      = soul.DataKeyBase64,
            });
            audit.Record("soul", "export", allowed: true, capability: "soul-export",
                detail: $"soul={soul.Name} serverUrl={soul.ServerUrl}");
            return Results.Ok(new { blob = EncryptSoul(json, req.Passphrase) });
        });

        // POST /soul/import — restore a soul from an encrypted backup onto a fresh bridge (becomes a
        // primary node holding the soul master key).
        // Body: { passphrase: "...", blob: "...", sealId: "..." }
        // Requires a fresh, capability-bound Inquisitorial Seal approved at this node (F-5).
        app.MapPost("/soul/import", async (HttpRequest httpReq, ImportSoulRequest req, BridgeDbContext db, SecurityAuditLog audit) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(httpReq))
            {
                audit.Record("soul", "import-denied", allowed: false, capability: "soul-import",
                    detail: "non-local origin");
                return Results.Json(new { error = "Soul import must be performed from the local bridge UI." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(req.SealId))
                return Results.BadRequest(new { error = "An approved seal id is required." });

            if (!SealEndpoints.TryConsumeSeal(req.SealId, "soul-import"))
            {
                audit.Record("soul", "import-denied", allowed: false, capability: "soul-import",
                    detail: "seal missing/not approved/wrong capability/already used");
                return Results.Json(new { error = "Seal missing, not approved, wrong capability, or already used." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrEmpty(req.Passphrase) || string.IsNullOrEmpty(req.Blob))
                return Results.BadRequest("Passphrase and blob required");
            if (await db.Souls.AnyAsync(s => s.Name != ""))
                return Results.Conflict("A soul already exists on this bridge");

            string json;
            try { json = DecryptSoul(req.Blob, req.Passphrase); }
            catch { return Results.BadRequest("Could not decrypt — wrong passphrase or corrupt backup"); }

            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var soul = new BridgeSoul
            {
                Name             = r.TryGetProperty("name", out var nm) ? nm.GetString() ?? "Imported Soul" : "Imported Soul",
                ServerSoulId     = r.TryGetProperty("serverSoulId", out var sid) ? sid.GetString() : null,
                ServerUrl        = r.TryGetProperty("serverUrl", out var su) ? su.GetString() : null,
                PublicKeyBase64  = r.TryGetProperty("publicKey",  out var pk) ? pk.GetString() : null,
                PrivateKeyBase64 = r.TryGetProperty("privateKey", out var sk) ? sk.GetString() : null,
                DataKeyBase64    = r.TryGetProperty("dataKey",    out var dk) ? dk.GetString() : null,
            };
            db.Souls.Add(soul);
            await db.SaveChangesAsync();
            audit.Record("soul", "import", allowed: true, capability: "soul-import",
                detail: $"soul={soul.Name} serverUrl={soul.ServerUrl}");
            return Results.Ok(ToDto(soul));
        });

        // POST /soul/rotate-key — generate a fresh soul master keypair and re-register it on the server
        // via challenge-response signed with the NEW key (old key not required — it may be compromised).
        // Only the PRIMARY bridge (soul-key holder) may self-rotate: it is the ultimate authority for the
        // soul. A joined node can't unilaterally rotate its node key — that would let a stolen node key
        // evade revocation. The correct recovery for a compromised node is owner-revoke + re-join.
        // Body: { sealId: "..." }
        // Requires a fresh, capability-bound Inquisitorial Seal approved at this node (F-5).
        app.MapPost("/soul/rotate-key", async (HttpRequest httpReq, RotateKeyRequest req, BridgeDbContext db, SecurityAuditLog audit) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(httpReq))
            {
                audit.Record("soul", "rotate-key-denied", allowed: false, capability: "soul-rotate-key",
                    detail: "non-local origin");
                return Results.Json(new { error = "Key rotation must be performed from the local bridge UI." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(req.SealId))
                return Results.BadRequest(new { error = "An approved seal id is required." });

            if (!SealEndpoints.TryConsumeSeal(req.SealId, "soul-rotate-key"))
            {
                audit.Record("soul", "rotate-key-denied", allowed: false, capability: "soul-rotate-key",
                    detail: "seal missing/not approved/wrong capability/already used");
                return Results.Json(new { error = "Seal missing, not approved, wrong capability, or already used." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var soul = await db.Souls.FirstOrDefaultAsync(s => s.Name != "") ?? await db.Souls.FirstOrDefaultAsync();
            if (soul is null) return Results.NotFound("No soul");
            if (soul.ServerSoulId is null || soul.ServerUrl is null)
                return Results.BadRequest("Soul must be linked to a server before rotating keys");
            if (soul.NodePublicKeyBase64 is not null)
                return Results.BadRequest("This is a joined node, not the primary. To recover a compromised node, revoke it from an owner device and re-join with a fresh key.");
            if (soul.PrivateKeyBase64 is null)
                return Results.BadRequest("No current master key to authorize rotation");

            var (newPub, newPriv) = GenerateKeypair();
            try
            {
                var baseUrl   = soul.ServerUrl.TrimEnd('/');
                var chPayload = JsonSerializer.Serialize(new { serverSoulId = soul.ServerSoulId, newPublicKey = newPub });
                var chResp    = await _http.PostAsync(baseUrl + "/api/bridge/rotation-challenge",
                    new StringContent(chPayload, Encoding.UTF8, "application/json"));
                chResp.EnsureSuccessStatusCode();

                using var chDoc = JsonDocument.Parse(await chResp.Content.ReadAsStringAsync());
                var nonce  = Convert.FromBase64String(chDoc.RootElement.GetProperty("nonceBase64").GetString()!);
                // Sign the nonce with BOTH keys: the new key proves possession of what we're installing,
                // the old (current) key proves authority over the soul — without it, knowing the public
                // GUID alone would be enough to take over the soul. See bridge-remote-nodes-security.md §9.5.
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(newPriv), out _);
                var sig = Convert.ToBase64String(ecdsa.SignData(nonce, HashAlgorithmName.SHA256));
                using var oldEcdsa = ECDsa.Create();
                oldEcdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(soul.PrivateKeyBase64), out _);
                var oldSig = Convert.ToBase64String(oldEcdsa.SignData(nonce, HashAlgorithmName.SHA256));

                var rotPayload = JsonSerializer.Serialize(new { serverSoulId = soul.ServerSoulId, newPublicKey = newPub, signatureBase64 = sig, oldSignatureBase64 = oldSig });
                var rotResp    = await _http.PostAsync(baseUrl + "/api/bridge/rotate-master-key",
                    new StringContent(rotPayload, Encoding.UTF8, "application/json"));
                if (!rotResp.IsSuccessStatusCode)
                    return Results.Problem($"Server rejected key rotation ({(int)rotResp.StatusCode}): {await rotResp.Content.ReadAsStringAsync()}");
            }
            catch (Exception ex) { return Results.Problem($"Key rotation failed: {ex.Message}"); }

            soul.PublicKeyBase64 = newPub;
            soul.PrivateKeyBase64 = newPriv;
            await db.SaveChangesAsync();
            audit.Record("soul", "rotate-key", allowed: true, capability: "soul-rotate-key",
                detail: $"soul={soul.Name} newPub={newPub[..32]}…");
            return Results.Ok(new { ok = true, newPublicKey = newPub, note = "Master key rotated. All enrolled nodes were revoked; re-join them. The bridge will reconnect automatically; restart if needed." });
        });

        static SoulDto ToDto(BridgeSoul s) =>
            new(s.Id, s.Name, s.AvatarSpriteKey, s.AccentColor,
                s.ServerSoulId, s.ServerUrl,
                s.ServerLinks.Select(l => new ServerLinkDto(l.Id, l.ServerSoulId, l.ServerUrl, l.CreatedAt)).ToArray(),
                s.PublicKeyBase64 != null, s.CreatedAt);
    }

    private static (string PublicKey, string PrivateKey) GenerateKeypair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (
            Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey())
        );
    }

    /// <summary>
    /// Force HTTPS for non-local hosts. Fly.io and similar hosts redirect HTTP to HTTPS, but the
    /// SignalR WebSocket upgrade does not follow redirects, so storing an http:// URL breaks the
    /// direct tunnel. Local development keeps http:// for the self-signed/loopback case.
    /// </summary>
    private static string NormalizeServerUrl(string raw)
    {
        var url = raw.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return url;

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1", StringComparison.Ordinal) ||
            host.Equals("::1", StringComparison.Ordinal))
        {
            return url;
        }

        var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 };
        return builder.Uri.ToString().TrimEnd('/');
    }

    // AES-256-GCM with a PBKDF2(SHA-256, 200k) key. Blob = salt(16) | nonce(12) | tag(16) | ciphertext.
    private static string EncryptSoul(string json, string passphrase)
    {
        var salt  = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key   = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 200_000, HashAlgorithmName.SHA256, 32);
        var plaintext  = Encoding.UTF8.GetBytes(json);
        var ciphertext = new byte[plaintext.Length];
        var tag        = new byte[16];
        using (var gcm = new AesGcm(key, 16))
            gcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var blob = new byte[16 + 12 + 16 + ciphertext.Length];
        Buffer.BlockCopy(salt,       0, blob,  0, 16);
        Buffer.BlockCopy(nonce,      0, blob, 16, 12);
        Buffer.BlockCopy(tag,        0, blob, 28, 16);
        Buffer.BlockCopy(ciphertext, 0, blob, 44, ciphertext.Length);
        return Convert.ToBase64String(blob);
    }

    private static string DecryptSoul(string blobB64, string passphrase)
    {
        var blob  = Convert.FromBase64String(blobB64);
        var salt  = blob[..16];
        var nonce = blob[16..28];
        var tag   = blob[28..44];
        var ct    = blob[44..];
        var key   = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 200_000, HashAlgorithmName.SHA256, 32);
        var pt    = new byte[ct.Length];
        using (var gcm = new AesGcm(key, 16))
            gcm.Decrypt(nonce, ct, tag, pt);
        return Encoding.UTF8.GetString(pt);
    }
}

public record CreateSoulRequest(string Name, string? AvatarSpriteKey, string? AccentColor);
public record UpdateSoulRequest(string? Name, string? AvatarSpriteKey, string? AccentColor, string? ServerSoulId);
public record LinkServerRequest(string ServerUrl, string SealId);
public record SwitchServerRequest(string ServerSoulId, string SealId);
public record SignRequest(string NonceBase64);
public record ExportSoulRequest(string Passphrase, string SealId);
public record ImportSoulRequest(string Passphrase, string Blob, string SealId);
public record UnlinkRequest(string SealId);
public record RotateKeyRequest(string SealId);
public record SoulDto(string Id, string Name, string? AvatarSpriteKey, string? AccentColor, string? ServerSoulId, string? ServerUrl, ServerLinkDto[] ServerLinks, bool HasKeypair, DateTime CreatedAt);
public record ServerLinkDto(string Id, string ServerSoulId, string ServerUrl, DateTime CreatedAt);
