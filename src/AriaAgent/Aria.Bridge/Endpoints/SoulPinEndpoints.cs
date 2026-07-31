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
/// the human reads the primary device's fingerprint off that machine's own bridge (Soul panel →
/// Copy, or <c>/soul/fingerprint</c>) and pastes it here as the last step of joining. The node pins
/// the key only if the typed fingerprint matches what the server is currently claiming, so a server
/// that nominates its own key as "primary" fails the comparison and is never pinned.
///
/// Deliberately NOT on <see cref="Aria.Shared.TunnelAllowlist"/>: the server must never be able to
/// drive pinning. Like every other mutating bridge endpoint it is also local-origin only.
/// </summary>
public static class SoulPinEndpoints
{
    public static void MapSoulPinEndpoints(this WebApplication app)
    {
        // GET /soul/pin-status — is this node joined, and is its soul key pinned? Never returns the
        // candidate fingerprint on a joined node: revealing it would let the human confirm the
        // server's own claim instead of comparing against the primary. On the primary, returning
        // this machine's own fingerprint is safe — it is read straight from local key material.
        app.MapGet("/soul/pin-status", async (BridgeDbContext db) =>
        {
            var soul = await ActiveSoulAsync(db);
            if (soul is null) return Results.NotFound("No soul");

            var joined = !string.IsNullOrEmpty(soul.NodePublicKeyBase64);
            string? fingerprint = null;
            if (!string.IsNullOrEmpty(soul.PublicKeyBase64)
                && (soul.SoulKeyPinnedAt != null || !joined))
            {
                fingerprint = NodeCrypto.FormatThumbprint(soul.PublicKeyBase64);
            }

            return Results.Ok(new
            {
                joined,
                pinned      = soul.SoulKeyPinnedAt != null,
                pinnedAt    = soul.SoulKeyPinnedAt,
                fingerprint,
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
                return Results.BadRequest(new { error = "Paste the primary device's fingerprint." });

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
            SiblingRoster.ForgetTrust(serverSoulId);

            var grouped = NodeCrypto.FormatThumbprint(candidate);
            audit.Record("soul-pin", "pinned", allowed: true, capability: "soul-key-pin",
                detail: $"Soul master key pinned for {serverSoulId} (fingerprint {grouped})");
            return Results.Ok(new { ok = true, fingerprint = grouped });
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
            if (soul.ServerSoulId is { Length: > 0 } sid) SiblingRoster.ForgetTrust(sid);

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

            return Results.Content(FingerprintPage(NodeCrypto.FormatThumbprint(soul.PublicKeyBase64)), "text/html");
        });

        // GET /soul/pin — the local ceremony page (also offered inline on the Soul panel as the last
        // join step; this standalone page remains for deep links / Devices-panel warnings).
        app.MapGet("/soul/pin", async (BridgeDbContext db) =>
        {
            var soul = await ActiveSoulAsync(db);
            if (soul is null) return Results.Content(Page("No soul on this bridge."), "text/html");
            if (string.IsNullOrEmpty(soul.NodePublicKeyBase64))
                return Results.Content(Page("This is the primary bridge — it holds the soul master key, nothing to pin."), "text/html");
            if (soul.SoulKeyPinnedAt != null)
                return Results.Content(Page(
                    $"✓ Soul key already pinned ({NodeCrypto.FormatThumbprint(soul.PublicKeyBase64!)})."), "text/html");

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
        .copy{background:#0a0806;color:#f0d060;border:1px solid #6a5010;flex:0 0 auto;padding:12px 18px}
        .warn{border:1px solid #6a5010;background:#100a02;padding:10px;font-size:11px;color:#e8c060;margin-top:14px}
        .msg{margin-top:14px;font-size:12px;min-height:18px}
        .fp{margin:18px 0;padding:16px;border:1px solid #6a5010;background:#0a0806;color:#f0d060;
            font-size:22px;letter-spacing:4px;text-align:center;word-break:break-all;user-select:all}
        """;

    private static string Page(string message) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Soul Key</title>
        <style>{{Style}}</style></head><body>
          <div class="card"><h1>⛨ SOUL KEY</h1><p>{{System.Net.WebUtility.HtmlEncode(message)}}</p></div>
        </body></html>
        """;

    private static string FingerprintPage(string fingerprint) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Soul Key Fingerprint</title>
        <style>{{Style}}</style></head><body>
          <div class="card">
            <h1>⛨ SOUL MASTER KEY FINGERPRINT</h1>
            <p>Copy this value and paste it into the last join step on the device you are adding
               (Soul panel on that machine). Read it from <b>this</b> machine's bridge — never from Aria.Web.</p>
            <div class="fp" id="fp">{{System.Net.WebUtility.HtmlEncode(fingerprint)}}</div>
            <div class="row">
              <button class="ok copy" onclick="copyFp()">⧉ COPY</button>
            </div>
            <div class="msg" id="msg"></div>
            <div class="warn">A value shown by the hosted server is not a valid reference — checking it
              against itself would prove nothing.</div>
          </div>
        <script>
          async function copyFp() {
            const v = document.getElementById('fp').textContent.trim();
            const msg = document.getElementById('msg');
            try {
              await navigator.clipboard.writeText(v);
              msg.style.color = '#7ed07e';
              msg.textContent = '✓ Copied — paste it on the joining machine.';
            } catch (e) {
              msg.style.color = '#e08080';
              msg.textContent = 'Copy failed — select the fingerprint and copy manually.';
            }
          }
        </script>
        </body></html>
        """;

    private static string CeremonyPage() => $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Join — Confirm Soul Key</title>
        <style>{{Style}}</style></head><body>
          <div class="card">
            <h1>⛨ JOIN · CONFIRM MASTER KEY</h1>
            <p>Last step of joining. This machine is enrolled, but it still needs the fingerprint of
               your soul's master key — read from the <b>primary</b> bridge, not from Aria.Web — before
               it will honour approvals signed on your other devices.</p>
            <ol>
              <li>On the primary machine, open the bridge status page → <b>Soul</b>.</li>
              <li>Click <b>⧉ COPY</b> next to the master-key fingerprint
                  (or open <b>http://localhost:5741/soul/fingerprint</b>).</li>
              <li>Paste it below and confirm.</li>
            </ol>
            <input id="fp" placeholder="paste abcd-efgh-ijkl-mnop" autocomplete="off" spellcheck="false">
            <div class="row">
              <button class="ok" onclick="pin()">⛨ CONFIRM &amp; FINISH JOIN</button>
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
                msg.textContent = '✓ Join complete (' + d.fingerprint + '). This node now accepts grants from your soul.';
              } else {
                msg.style.color = '#e08080';
                msg.textContent = d.error || 'Confirmation failed.';
              }
            } catch (e) {
              msg.style.color = '#e08080';
              msg.textContent = 'Request failed: ' + e.message;
            }
          }
          document.getElementById('fp').focus();
        </script>
        </body></html>
        """;
}

public record PinSoulKeyRequest(string Fingerprint);
