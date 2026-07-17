using System.Security.Cryptography;
using Aria.Shared;
using Aria.Web.Data;
using Aria.Web.Services;
using Aria.Web.Services.Node;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Endpoints;

public static class SoulEndpoints
{
    public static WebApplication MapSoulEndpoints(this WebApplication app)
    {
        // ── Bridge soul registration ──────────────────────────────────────────────────
        // Called by Aria.Bridge (POST /soul/link-server) to register a local soul with this server.
        // Upserts a User row keyed by PublicKey; returns the server-side userId.
        app.MapPost("/api/bridge/register-soul", async (BridgeRegisterSoulRequest req,
            IDbContextFactory<AppDbContext> dbFactory,
            ModelBridgeRegistry registry) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.PublicKey))
                return Results.BadRequest("Name and PublicKey required");

            if (req.Name.Trim().Length < 2 || req.Name.Trim().Length > 40)
                return Results.BadRequest("Name must be between 2 and 40 characters");

            if (ProfanityFilter.Contains(req.Name))
                return Results.BadRequest("Name contains prohibited content");

            await using var db = await dbFactory.CreateDbContextAsync();
            await using var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            void AddParam(System.Data.Common.DbCommand cmd, string name, object? value) {
                var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }

            // 1. Look up by public key (returning user with same keypair)
            await using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT Id FROM Users WHERE PublicKey = @pk LIMIT 1";
            AddParam(sel, "@pk", req.PublicKey);
            var existing = await sel.ExecuteScalarAsync();

            // 2. Fall back to name match — but ONLY to (re)claim a soul whose key slot is empty (it was
            //    explicitly unlinked, which requires a signature with the real key). Never overwrite a LIVE
            //    public key on a bare name match: register-soul is unauthenticated, so that would let anyone
            //    who knows a soul's *name* hijack it by re-registering with their own key, then pass the
            //    RegisterDirectBridge challenge as the victim. Legit key-loss recovery goes through signed
            //    unlink (nulls PublicKey) or soul export/import. See bridge-remote-nodes-security.md.
            if (existing is not string)
            {
                await using var selName = conn.CreateCommand();
                selName.CommandText = "SELECT Id, PublicKey FROM Users WHERE lower(Name) = lower(@n) LIMIT 1";
                AddParam(selName, "@n", req.Name.Trim());
                await using var rdr = await selName.ExecuteReaderAsync();
                if (await rdr.ReadAsync())
                {
                    var nameMatchId = rdr.GetString(0);
                    var existingKey = rdr.IsDBNull(1) ? null : rdr.GetString(1);
                    await rdr.CloseAsync();
                    if (!string.IsNullOrEmpty(existingKey) && existingKey != req.PublicKey)
                    {
                        await conn.CloseAsync();
                        return Results.Conflict("A soul with this name is already bound to a different node key. " +
                            "Unlink it from its owning device first (or restore from a soul backup) to re-register.");
                    }
                    existing = nameMatchId;
                }
            }

            string userId;
            if (existing is string existingId)
            {
                userId = existingId;
                // Update key + avatar in case they changed (covers re-registration with new keypair)
                await using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE Users SET Name=@n, AvatarSpriteKey=@av, PublicKey=@pk, KeepTelemetryExpanded=0 WHERE Id=@id";
                AddParam(upd, "@n",  req.Name);
                AddParam(upd, "@av", req.AvatarSpriteKey);
                AddParam(upd, "@pk", req.PublicKey);
                AddParam(upd, "@id", userId);
                await upd.ExecuteNonQueryAsync();

                // Keep the primary node row in sync so re-registering / key-rotation does not leave
                // a stale primary thumbprint or create duplicate IsPrimary rows.
                var thumb = NodeCrypto.Thumbprint(req.PublicKey);
                await using var delPrimary = conn.CreateCommand();
                delPrimary.CommandText = "DELETE FROM SoulNodeKeys WHERE UserId=@id AND IsPrimary=1";
                AddParam(delPrimary, "@id", userId);
                await delPrimary.ExecuteNonQueryAsync();

                await using var insPrimary = conn.CreateCommand();
                insPrimary.CommandText = "INSERT INTO SoulNodeKeys (UserId, NodeId, NodePublicKeyBase64, IsPrimary, Revoked, EnrolledAt) VALUES (@uid, @thumb, @pk, 1, 0, datetime('now'))";
                AddParam(insPrimary, "@uid",  userId);
                AddParam(insPrimary, "@thumb", thumb);
                AddParam(insPrimary, "@pk",  req.PublicKey);
                await insPrimary.ExecuteNonQueryAsync();
            }
            else
            {
                // Create new user with explicit GUID (not auto-increment — prevents enumeration)
                var newId = Guid.NewGuid().ToString();
                await using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO Users (Id, Name, AvatarSpriteKey, PublicKey, KeepTelemetryExpanded, CreatedAt) VALUES (@id, @n, @av, @pk, 0, datetime('now'))";
                AddParam(ins, "@id", newId);
                AddParam(ins, "@n",  req.Name);
                AddParam(ins, "@av", req.AvatarSpriteKey);
                AddParam(ins, "@pk", req.PublicKey);
                await ins.ExecuteNonQueryAsync();
                userId = newId;
            }

            await conn.CloseAsync();
            registry.NotifySoulRegistered(userId);
            return Results.Ok(new { serverSoulId = userId });
        });

        // Issues a fresh single-use challenge nonce for unlinking. The server generates and remembers
        // the nonce (2-min TTL) so the unlink signature can't be a replay of a client-chosen value.
        app.MapPost("/api/bridge/unlink-challenge", (UnlinkChallengeRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.PublicKey))
                return Results.BadRequest("publicKey required");
            var nonce = UnlinkChallengeStore.Issue(req.PublicKey);
            return Results.Ok(new { nonceBase64 = nonce });
        });

        // Clears the public key for a soul so the name can be reclaimed with a fresh keypair.
        // Also revokes every enrolled node and drops live connections, so a wiped/re-registered soul
        // cannot be rejoined by old device keys. Requires a signature over a server-issued
        // (single-use, short-TTL) nonce — proving both key ownership AND freshness, so a captured
        // request cannot be replayed.
        app.MapPost("/api/bridge/unlink-soul", async (UnlinkSoulRequest req,
            IDbContextFactory<AppDbContext> dbFactory,
            ModelBridgeRegistry registry,
            PendingEnrollmentService pendings) =>
        {
            if (string.IsNullOrWhiteSpace(req.PublicKey) ||
                string.IsNullOrWhiteSpace(req.SignatureBase64))
                return Results.BadRequest("publicKey and signatureBase64 required");

            // Consume the server-issued nonce for this key (single-use). Absent/expired → reject.
            var issued = UnlinkChallengeStore.Consume(req.PublicKey);
            if (issued == null)
                return Results.Problem(statusCode: 400, title: "No valid challenge — request /api/bridge/unlink-challenge first");

            // Verify the signature over the SERVER's nonce — proves the requester holds the private key.
            try
            {
                var pubKeyBytes = Convert.FromBase64String(req.PublicKey);
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(pubKeyBytes, out _);
                var nonce = Convert.FromBase64String(issued);
                var sig   = Convert.FromBase64String(req.SignatureBase64);
                if (!ecdsa.VerifyData(nonce, sig, HashAlgorithmName.SHA256))
                    return Results.Problem(statusCode: 401, title: "Signature verification failed");
            }
            catch
            {
                return Results.BadRequest("Invalid public key or signature format");
            }

            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.FirstOrDefaultAsync(u => u.PublicKey == req.PublicKey);
            if (user == null) return Results.Ok(new { ok = true, unlinked = 0 });

            // Revoke every enrolled node so old device keys cannot rejoin after re-registration.
            var nodeRows = await db.SoulNodeKeys
                .Where(k => k.UserId == user.Id && !k.Revoked)
                .ToListAsync();
            foreach (var node in nodeRows)
            {
                node.Revoked = true;
                node.RevokedAt = DateTime.UtcNow;
                registry.RemoveNode(user.Id, node.NodeId);
            }

            // Clear any pending enrollment requests for this soul.
            pendings.ClearForUser(user.Id);

            user.PublicKey = null;
            await db.SaveChangesAsync();
            registry.NotifySoulUnlinked(user.Id);

            return Results.Ok(new { ok = true, unlinked = 1, revokedNodes = nodeRows.Count });
        });

        // ── Key rotation (§ key-rotation) ────────────────────────────────────────────
        // The bridge generates a NEW keypair and calls here to swap the old public key for the new one.
        // Authentication is a DUAL signature over the server nonce:
        //   • NEW key  — proves the bridge actually holds the key it is installing.
        //   • OLD key  — proves authority over the soul, verified against the stored Users.PublicKey.
        // The GUID serverSoulId is NOT a secret (it travels on the wire, shows on the status page, appears
        // in logs), so it cannot be the only gate: requiring the old-key signature stops a GUID-only attacker
        // from rotating a victim's soul to their own key. A *leaked* key is a copy — the legitimate owner
        // still holds it and can sign — so the "rotate a possibly-compromised key" use case still works.
        // (Full key LOSS is unrecoverable by the key alone and needs an independent recovery factor; see §9.5.)
        app.MapPost("/api/bridge/rotation-challenge", (RotationChallengeRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.ServerSoulId) || string.IsNullOrWhiteSpace(req.NewPublicKey))
                return Results.BadRequest("serverSoulId and newPublicKey required");
            var nonce = RotationChallengeStore.Issue(req.ServerSoulId, req.NewPublicKey);
            return Results.Ok(new { nonceBase64 = nonce });
        });

        app.MapPost("/api/bridge/rotate-master-key", async (RotateMasterKeyRequest req,
            IDbContextFactory<AppDbContext> dbFactory, ModelBridgeRegistry registry) =>
        {
            if (string.IsNullOrWhiteSpace(req.ServerSoulId) || string.IsNullOrWhiteSpace(req.NewPublicKey)
                || string.IsNullOrWhiteSpace(req.SignatureBase64) || string.IsNullOrWhiteSpace(req.OldSignatureBase64))
                return Results.BadRequest("serverSoulId, newPublicKey, signatureBase64 and oldSignatureBase64 required");

            var nonce = RotationChallengeStore.Consume(req.ServerSoulId, req.NewPublicKey);
            if (nonce == null)
                return Results.Problem(statusCode: 400, title: "No valid challenge — call /api/bridge/rotation-challenge first");

            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == req.ServerSoulId);
            if (user == null) return Results.NotFound("Unknown soul");
            if (string.IsNullOrEmpty(user.PublicKey))
                return Results.Problem(statusCode: 409, title: "Soul has no key on record — register or import it instead of rotating");

            try
            {
                var nonceBytes = Convert.FromBase64String(nonce);
                // NEW key proves possession of the key being installed.
                using var newEc = ECDsa.Create();
                newEc.ImportSubjectPublicKeyInfo(Convert.FromBase64String(req.NewPublicKey), out _);
                if (!newEc.VerifyData(nonceBytes, Convert.FromBase64String(req.SignatureBase64), HashAlgorithmName.SHA256))
                    return Results.Problem(statusCode: 401, title: "New-key signature verification failed");
                // OLD key proves authority over the soul (defeats GUID-only takeover).
                using var oldEc = ECDsa.Create();
                oldEc.ImportSubjectPublicKeyInfo(Convert.FromBase64String(user.PublicKey), out _);
                if (!oldEc.VerifyData(nonceBytes, Convert.FromBase64String(req.OldSignatureBase64), HashAlgorithmName.SHA256))
                    return Results.Problem(statusCode: 401, title: "Current-key signature verification failed — not authorized to rotate this soul");
            }
            catch { return Results.BadRequest("Invalid key or signature format"); }

            // Revoke all existing node keys — they were enrolled against the old master key.
            await db.SoulNodeKeys
                .Where(k => k.UserId == req.ServerSoulId && !k.IsPrimary)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.Revoked, true).SetProperty(k => k.RevokedAt, DateTime.UtcNow));

            // Re-point the primary node row at the new key. Without this, the primary bridge would reconnect
            // under a new thumbprint and RegisterDirectBridge would ADD a second IsPrimary row (the multi-
            // primary corruption from bridge-remote-nodes-security.md §5) instead of reusing the existing one.
            if (user.PublicKey is { } oldKey)
            {
                var oldThumb    = NodeCrypto.Thumbprint(oldKey);
                var primaryRow  = await db.SoulNodeKeys.FirstOrDefaultAsync(
                    k => k.UserId == req.ServerSoulId && k.NodeId == oldThumb && k.IsPrimary);
                if (primaryRow != null)
                {
                    primaryRow.NodePublicKeyBase64 = req.NewPublicKey;
                    primaryRow.NodeId              = NodeCrypto.Thumbprint(req.NewPublicKey);
                    primaryRow.LastSeenAt          = DateTime.UtcNow;
                }
            }

            user.PublicKey = req.NewPublicKey;
            await db.SaveChangesAsync();
            registry.NotifySoulRegistered(req.ServerSoulId);
            return Results.Ok(new { ok = true });
        });

        return app;
    }
}

public record BridgeRegisterSoulRequest(string Name, string PublicKey, string? AvatarSpriteKey, string? AccentColor);
public record UnlinkChallengeRequest(string PublicKey);
public record UnlinkSoulRequest(string PublicKey, string SignatureBase64);
public record RotationChallengeRequest(string ServerSoulId, string NewPublicKey);
public record RotateMasterKeyRequest(string ServerSoulId, string NewPublicKey, string SignatureBase64, string OldSignatureBase64);
