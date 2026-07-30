using Aria.Bridge.Data;
using Aria.Bridge.Services.Security;
using Aria.Bridge.Services.Trust;
using Microsoft.EntityFrameworkCore;
using NodeCrypto = Aria.Shared.NodeCrypto;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// The soul-master-key pinning ceremony for a JOINED bridge node (Layer B trust anchor).
///
/// A joined node holds only its own node keypair. Every candidate for the soul master key reaches it
/// through the hosted server, which the threat model treats as untrusted — so there is no way for the
/// node to recognise the real key by itself. This ceremony supplies the missing anchor out of band:
/// the human reads the primary device's fingerprint off a device they already trust (Aria.Web →
/// DEVICES, the ★ primary row, or <c>/node/info</c> on the primary machine) and types it here. The
/// node pins the key only if the typed fingerprint matches what the server is currently claiming, so
/// a server that nominates its own key as "primary" fails the comparison and is never pinned.
///
/// Deliberately NOT on <see cref="Aria.Shared.TunnelAllowlist"/>: the server must never be able to
/// drive pinning. Like every other mutating bridge endpoint it is also local-origin only.
/// </summary>
public static class SoulPinEndpoints
{
    public static void MapSoulPinEndpoints(this WebApplication app)
    {
        // GET /soul/pin-status — is this node joined, and is its soul key pinned? Never returns the
        // candidate fingerprint: revealing it would let the human confirm the server's own claim
        // instead of comparing against the primary, which is the entire point of the ceremony.
        app.MapGet("/soul/pin-status", async (BridgeDbContext db) =>
        {
            var soul = await ActiveSoulAsync(db);
            if (soul is null) return Results.NotFound("No soul");

            var joined = !string.IsNullOrEmpty(soul.NodePublicKeyBase64);
            return Results.Ok(new
            {
                joined,
                pinned      = soul.SoulKeyPinnedAt != null,
                pinnedAt    = soul.SoulKeyPinnedAt,
                // Safe to show: it's the key the human already confirmed.
                fingerprint = soul.SoulKeyPinnedAt != null && !string.IsNullOrEmpty(soul.PublicKeyBase64)
                    ? NodeCrypto.Thumbprint(soul.PublicKeyBase64)
                    : null,
                candidateAvailable = joined && soul.ServerSoulId is { Length: > 0 } sid
                                     && SiblingRoster.PinCandidate(sid) != null,
            });
        });

        // POST /soul/pin-key — body { fingerprint }. Pins the soul master key iff the human-supplied
        // fingerprint matches the one the server is currently claiming for the primary node.
        app.MapPost("/soul/pin-key", async (PinSoulKeyRequest req, BridgeDbContext db, SecurityAuditLog audit) =>
        {
            var soul = await ActiveSoulAsync(db);
            if (soul is null) return Results.NotFound("No soul");
            if (string.IsNullOrEmpty(soul.NodePublicKeyBase64))
                return Results.BadRequest(new { error = "This is the primary bridge — it holds the soul master key already." });
            if (soul.ServerSoulId is not { Length: > 0 } serverSoulId)
                return Results.BadRequest(new { error = "This node is not linked to a soul yet." });
            if (soul.SoulKeyPinnedAt != null)
                return Results.BadRequest(new { error = "A soul key is already pinned. Unpin it first to re-pin." });

            var candidate = SiblingRoster.PinCandidate(serverSoulId);
            if (string.IsNullOrEmpty(candidate))
                return Results.BadRequest(new { error = "No soul key seen yet — wait for this node to connect to the server, then retry." });

            var typed    = Normalize(req.Fingerprint);
            var expected = Normalize(NodeCrypto.Thumbprint(candidate));
            if (typed.Length == 0)
                return Results.BadRequest(new { error = "Enter the primary device's fingerprint." });

            if (!string.Equals(typed, expected, StringComparison.OrdinalIgnoreCase))
            {
                // Either a typo or the server is presenting a key that is not the real primary's.
                audit.Record("soul-pin", "rejected", allowed: false, capability: "soul-key-pin",
                    detail: $"Fingerprint mismatch while pinning soul key for {serverSoulId}");
                return Results.Json(new
                {
                    ok = false,
                    error = "That fingerprint does not match the key this server is presenting. Check for a typo — "
                          + "and if it still fails, the server may be presenting a key that is not your primary "
                          + "device's. Do not pin; investigate first.",
                }, statusCode: StatusCodes.Status409Conflict);
            }

            soul.PublicKeyBase64 = candidate;
            soul.SoulKeyPinnedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            audit.Record("soul-pin", "pinned", allowed: true, capability: "soul-key-pin",
                detail: $"Soul master key pinned for {serverSoulId} (fingerprint {expected})");
            return Results.Ok(new { ok = true, fingerprint = NodeCrypto.Thumbprint(candidate) });
        });

        // POST /soul/unpin-key — drop the pin so a human can re-pin, e.g. after a legitimate
        // /soul/rotate-key on the primary. Grants signed by the old key stop verifying immediately.
        app.MapPost("/soul/unpin-key", async (BridgeDbContext db, SecurityAuditLog audit) =>
        {
            var soul = await ActiveSoulAsync(db);
            if (soul is null) return Results.NotFound("No soul");
            if (string.IsNullOrEmpty(soul.NodePublicKeyBase64))
                return Results.BadRequest(new { error = "This is the primary bridge — its soul key cannot be unpinned." });

            soul.PublicKeyBase64 = null;
            soul.SoulKeyPinnedAt = null;
            await db.SaveChangesAsync();

            audit.Record("soul-pin", "unpinned", allowed: true, capability: "soul-key-pin",
                detail: $"Soul master key unpinned for {soul.ServerSoulId}");
            return Results.Ok(new { ok = true });
        });

        // GET /soul/fingerprint — this soul's master-key fingerprint, rendered for a human to read
        // aloud or copy. Served by the bridge itself on the primary machine, so the value does NOT
        // pass through the hosted server: that independence is what makes it usable as the reference
        // when pinning on a joined node.
        app.MapGet("/soul/fingerprint", async (BridgeDbContext db) =>
        {
            var soul = await ActiveSoulAsync(db);
            if (soul is null) return Results.Content(Page("No soul on this bridge."), "text/html");
            if (!string.IsNullOrEmpty(soul.NodePublicKeyBase64))
                return Results.Content(Page(
                    "This is a joined node, not the primary — read the fingerprint on the machine that "
                  + "holds the soul master key."), "text/html");
            if (string.IsNullOrEmpty(soul.PublicKeyBase64))
                return Results.Content(Page("This soul has no keypair yet."), "text/html");

            return Results.Content(FingerprintPage(NodeCrypto.Thumbprint(soul.PublicKeyBase64)), "text/html");
        });

        // GET /soul/pin — the local ceremony page.
        app.MapGet("/soul/pin", async (BridgeDbContext db) =>
        {
            var soul = await ActiveSoulAsync(db);
            if (soul is null) return Results.Content(Page("No soul on this bridge."), "text/html");
            if (string.IsNullOrEmpty(soul.NodePublicKeyBase64))
                return Results.Content(Page("This is the primary bridge — it holds the soul master key, nothing to pin."), "text/html");
            if (soul.SoulKeyPinnedAt != null)
                return Results.Content(Page(
                    $"✓ Soul key already pinned ({NodeCrypto.Thumbprint(soul.PublicKeyBase64!)})."), "text/html");

            return Results.Content(CeremonyPage(), "text/html");
        });
    }

    private static string Normalize(string? s) =>
        new((s ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());

    private static async Task<BridgeSoul?> ActiveSoulAsync(BridgeDbContext db) =>
        await db.Souls.FirstOrDefaultAsync(s => s.Name != "") ?? await db.Souls.FirstOrDefaultAsync();

    private const string Style = """
        body{background:#0a0806;color:#d8c89a;font-family:ui-monospace,Menlo,monospace;
             display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0}
        .card{border:1px solid #b09040;border-left:4px solid #d4a020;background:#160f04;
              padding:28px 32px;max-width:620px;box-shadow:0 0 32px rgba(180,140,20,.35)}
        h1{font-size:15px;letter-spacing:3px;color:#f0d060;margin:0 0 14px}
        p{font-size:12px;line-height:1.7;color:#c8b890}
        ol{font-size:12px;line-height:1.8;color:#c8b890;padding-left:18px}
        b{color:#f0d060}
        input{width:100%;box-sizing:border-box;margin-top:14px;padding:11px;background:#0a0806;
              border:1px solid #6a5010;color:#f0d060;font-family:inherit;font-size:14px;letter-spacing:2px}
        .row{display:flex;gap:12px;margin-top:16px}
        button{flex:1;padding:12px;font-family:inherit;font-size:12px;letter-spacing:2px;cursor:pointer;border:none}
        .ok{background:#b09040;color:#160404;font-weight:bold}
        .warn{border:1px solid #6a5010;background:#100a02;padding:10px;font-size:11px;color:#e8c060;margin-top:14px}
        .msg{margin-top:14px;font-size:12px;min-height:18px}
        """;

    private static string Page(string message) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Soul Key</title>
        <style>{{Style}}</style></head><body>
          <div class="card"><h1>⛨ SOUL KEY</h1><p>{{System.Net.WebUtility.HtmlEncode(message)}}</p></div>
        </body></html>
        """;

    private static string FingerprintPage(string fingerprint) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Soul Key Fingerprint</title>
        <style>{{Style}}
          .fp{margin:18px 0;padding:16px;border:1px solid #6a5010;background:#0a0806;color:#f0d060;
              font-size:22px;letter-spacing:4px;text-align:center;word-break:break-all}
        </style></head><body>
          <div class="card">
            <h1>⛨ SOUL MASTER KEY FINGERPRINT</h1>
            <p>This is the fingerprint of your soul's master key, read straight from this machine's
               own bridge. Type it into the pinning page on a device you are adding.</p>
            <div class="fp">{{System.Net.WebUtility.HtmlEncode(fingerprint)}}</div>
            <div class="warn">Read it from this page, on this machine. A value shown to you by the
              hosted server is not a valid reference — checking it against itself would prove nothing.</div>
          </div>
        </body></html>
        """;

    private static string CeremonyPage() => $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Pin Soul Key</title>
        <style>{{Style}}</style></head><body>
          <div class="card">
            <h1>⛨ CONFIRM YOUR SOUL'S MASTER KEY</h1>
            <p>This machine joined your soul as an additional node. It holds its own node key but has
               never been told which master key belongs to your soul — and it cannot work that out on
               its own, because everything it hears comes through the server.</p>
            <p>Until you confirm the key here, this node <b>refuses</b> grants signed elsewhere, so
               approvals made on your other devices will not carry over to this one.</p>
            <ol>
              <li>Go to the machine that holds your soul's master key (your primary bridge).</li>
              <li>On that machine, open <b>http://localhost:5741/soul/fingerprint</b> and read the
                  fingerprint shown there.</li>
              <li>Type it below. Take it from that machine's own page, not from Aria.Web — a value
                  the server showed you would only be checked against the server's own claim, which
                  proves nothing.</li>
            </ol>
            <input id="fp" placeholder="fingerprint from the primary machine" autocomplete="off" spellcheck="false">
            <div class="row">
              <button class="ok" onclick="pin()">⛨ PIN THIS KEY</button>
            </div>
            <div class="msg" id="msg"></div>
            <div class="warn">If the fingerprint keeps failing to match, stop. The server may be
              presenting a key that is not your primary device's — pinning it would let it approve
              sensitive actions on this machine without asking you.</div>
          </div>
        <script>
          async function pin() {
            const msg = document.getElementById('msg');
            msg.style.color = '#c8b890';
            msg.textContent = 'Checking…';
            try {
              const r = await fetch('/soul/pin-key', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ fingerprint: document.getElementById('fp').value })
              });
              const d = await r.json();
              if (r.ok && d.ok) {
                msg.style.color = '#7ed07e';
                msg.textContent = '✓ Pinned (' + d.fingerprint + '). This node now accepts grants from your soul.';
              } else {
                msg.style.color = '#e08080';
                msg.textContent = d.error || 'Pinning failed.';
              }
            } catch (e) {
              msg.style.color = '#e08080';
              msg.textContent = 'Request failed: ' + e.message;
            }
          }
        </script>
        </body></html>
        """;
}

public record PinSoulKeyRequest(string Fingerprint);
