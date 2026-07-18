using System.Security.Cryptography;
using System.Text;
using Aria.Bridge.Data;
using Aria.Bridge.Infrastructure;
using Aria.Shared;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// Bridge-side primitives for the remote-nodes enrollment flow (§9.3 of the plan).
/// The primary bridge signs with the soul key; additional bridges generate their own node key
/// via /soul/join and must be enrolled (their node pubkey added to the server allow-list).
/// </summary>
public static class NodeEndpoints
{
    public static void MapNodeEndpoints(this WebApplication app)
    {
        // GET /node/info — this bridge's node identity (for display / enrollment).
        app.MapGet("/node/info", async (BridgeDbContext db) =>
        {
            var soul = await PrimarySoul(db);
            if (soul is null) return Results.NotFound("No soul");
            var (pub, _) = EffectiveKey(soul);
            if (pub is null) return Results.NotFound("No node key");
            return Results.Ok(new
            {
                serverSoulId  = soul.ServerSoulId,
                nodePublicKey = pub,
                nodeId        = Thumbprint(pub),
                label         = soul.NodeLabel ?? Environment.MachineName,
                platform      = Platform(),
                isPrimary     = soul.NodePublicKeyBase64 is null,   // primary signs with the soul key
            });
        });

        // POST /node/sign-enrollment — this bridge (an existing authorized node) signs an enrollment
        // certificate authorizing a NEW node's public key. Body: { serverSoulId, newNodePublicKey, label, expiryUnix }.
        app.MapPost("/node/sign-enrollment", async (SignEnrollmentRequest req, BridgeDbContext db) =>
        {
            var soul = await PrimarySoul(db);
            if (soul is null) return Results.NotFound("No soul");
            var (pub, priv) = EffectiveKey(soul);
            if (pub is null || priv is null) return Results.NotFound("No signing key");

            // Must byte-match Aria.Web NodeCrypto.EnrollPayload.
            var payload = Encoding.UTF8.GetBytes(
                $"enroll|{req.ServerSoulId}|{req.NewNodePublicKey}|{req.Label}|{req.ExpiryUnix}");
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(priv), out _);
            var cert = Convert.ToBase64String(ecdsa.SignData(payload, HashAlgorithmName.SHA256));

            // Deliver the sync DEK (§11): wrap it to the new node's public key so it can decrypt the
            // replicated dataset. Null if this approver hasn't got a DEK yet (the new node will fetch
            // it later once the primary has minted one).
            var wrappedDek = soul.DataKeyBase64 is { } dek
                ? SyncCrypto.WrapDek(dek, req.NewNodePublicKey)
                : null;
            return Results.Ok(new { approverPublicKey = pub, certificate = cert, wrappedDek });
        });

        // POST /node/sign-revocation — this bridge (an authorized node) signs a revocation of a
        // target node's key. Body: { serverSoulId, targetNodePublicKey, nowUnix }.
        app.MapPost("/node/sign-revocation", async (SignRevocationRequest req, BridgeDbContext db) =>
        {
            var soul = await PrimarySoul(db);
            if (soul is null) return Results.NotFound("No soul");
            var (pub, priv) = EffectiveKey(soul);
            if (pub is null || priv is null) return Results.NotFound("No signing key");

            // Must byte-match Aria.Web NodeCrypto.RevokePayload.
            var payload = Encoding.UTF8.GetBytes(
                $"revoke|{req.ServerSoulId}|{req.TargetNodePublicKey}|{req.NowUnix}");
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(priv), out _);
            var sig = Convert.ToBase64String(ecdsa.SignData(payload, HashAlgorithmName.SHA256));
            return Results.Ok(new { approverPublicKey = pub, signature = sig });
        });

        // POST /node/attest — sign a server-issued payload (attest|userId|token|nonce) with this
        // machine's effective key (node key, or the soul key on the primary). The browser relays this
        // from its own localhost so the Aria.Web circuit can prove "this browser controls a bridge
        // enrolled for the soul" (per-circuit auth, §12). Body: { payloadBase64 }.
        app.MapPost("/node/attest", async (AttestRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.PayloadBase64)) return Results.BadRequest("payloadBase64 required");
            var soul = await PrimarySoul(db);
            if (soul is null) return Results.NotFound("No soul");
            var (pub, priv) = EffectiveKey(soul);
            if (pub is null || priv is null) return Results.NotFound("No signing key");

            byte[] payload;
            try { payload = Convert.FromBase64String(req.PayloadBase64); }
            catch { return Results.BadRequest("payloadBase64 not valid base64"); }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(priv), out _);
            var sig = Convert.ToBase64String(ecdsa.SignData(payload, HashAlgorithmName.SHA256));
            return Results.Ok(new { publicKey = pub, signature = sig });
        });

        // GET /node/join-code — the pairing code shown while this (joined) node awaits enrollment, or
        // null once it's enrolled & connected. Polled by the status page to guide the human.
        app.MapGet("/node/join-code", () =>
        {
            var code = DirectTunnel.CurrentJoinCode;
            return Results.Ok(new { code, display = code is null ? null : $"{code[..3]}-{code[3..]}" });
        });

        // GET /node/session-code — this bridge's per-process BROWSER session code (§12 fallback). Shown
        // on the local status page (always a secure localhost context). A remote browser that can't do
        // the automatic loopback attestation (insecure http://LAN-IP context) instead pastes this code
        // into Aria.Web; the server fetches it from this node over the tunnel and compares. Reading it
        // requires being at THIS machine's localhost — that's the co-location proof.
        app.MapGet("/node/session-code", () =>
            Results.Ok(new { code = SessionCode, display = $"{SessionCode[..4]}-{SessionCode[4..]}" }));

        // POST /soul/join — make THIS bridge an additional node of an existing soul. Generates a node
        // keypair (NO soul master key). Body: { serverUrl, serverSoulId, name?, label? }.
        app.MapPost("/soul/join", async (JoinSoulRequest req, BridgeDbContext db, DirectTunnel tunnel) =>
        {
            if (string.IsNullOrWhiteSpace(req.ServerUrl) || string.IsNullOrWhiteSpace(req.ServerSoulId))
                return Results.BadRequest("serverUrl and serverSoulId required");
            if (await db.Souls.AnyAsync(s => s.Name != ""))
                return Results.Conflict("A soul already exists on this bridge — join requires a fresh bridge");

            var (pub, priv) = GenerateKeypair();
            var soul = new BridgeSoul
            {
                Name                 = string.IsNullOrWhiteSpace(req.Name) ? "Joined Soul" : req.Name.Trim(),
                ServerSoulId         = req.ServerSoulId,
                ServerUrl            = req.ServerUrl.TrimEnd('/'),
                NodePublicKeyBase64  = pub,
                NodePrivateKeyBase64 = priv,
                NodeId               = Thumbprint(pub),
                NodeLabel            = req.Label ?? Environment.MachineName,
                // No PrivateKeyBase64/PublicKeyBase64 — this node never holds the soul master key.
            };
            db.Souls.Add(soul);
            await db.SaveChangesAsync();

            // Drop any live tunnel (e.g. still connected under a previous, since-wiped identity) so the
            // reconnect loop picks up the joined soul now instead of after a restart/network blip.
            tunnel.RequestReconnect();

            return Results.Ok(new
            {
                nodePublicKey = pub,
                nodeId        = soul.NodeId,
                label         = soul.NodeLabel,
                platform      = Platform(),
                note          = "Enroll this node from an existing device, then this bridge will connect automatically.",
            });
        });
    }

    // Stable per-process browser-pairing code (§12 fallback). Rotates only on bridge restart. 8 chars
    // from a 32-symbol unambiguous alphabet ≈ 40 bits — infeasible to guess over the rate-limited tunnel,
    // and only visible to someone at this machine's localhost.
    public static readonly string SessionCode = GenerateSessionCode();

    static string GenerateSessionCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O/1/I
        var bytes = RandomNumberGenerator.GetBytes(8);
        var sb = new StringBuilder(8);
        foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }

    static async Task<BridgeSoul?> PrimarySoul(BridgeDbContext db) =>
        await db.Souls.FirstOrDefaultAsync(s => s.Name != "") ?? await db.Souls.FirstOrDefaultAsync();

    // The key this bridge signs with: its own node key, or the soul key (primary bridge).
    static (string? pub, string? priv) EffectiveKey(BridgeSoul s) =>
        (s.NodePublicKeyBase64 ?? s.PublicKeyBase64, s.NodePrivateKeyBase64 ?? s.PrivateKeyBase64);

    static string Platform() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsLinux() ? "Linux" : "Unknown";

    static (string pub, string priv) GenerateKeypair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()));
    }

    // Must byte-match Aria.Web NodeCrypto.Thumbprint.
    static string Thumbprint(string pubB64)
    {
        var hash = SHA256.HashData(Convert.FromBase64String(pubB64));
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=')[..16];
    }
}

public record AttestRequest(string PayloadBase64);
public record SignEnrollmentRequest(string ServerSoulId, string NewNodePublicKey, string Label, long ExpiryUnix);
public record SignRevocationRequest(string ServerSoulId, string TargetNodePublicKey, long NowUnix);
public record JoinSoulRequest(string ServerUrl, string ServerSoulId, string? Name, string? Label);
