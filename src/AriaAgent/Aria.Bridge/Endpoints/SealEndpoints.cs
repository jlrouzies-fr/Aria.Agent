using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Services.Logging;
using Aria.Bridge.Services.Security;
using Aria.Shared;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// The Inquisitorial Seal — node-side, soul-signed authorisation for high-stakes tool calls.
/// The server requests a seal over the tunnel; the node shows a LOCAL approval page and only signs
/// the server's nonce with the soul private key after a human clicks Approve here. The server then
/// verifies that signature against the soul public key it holds — proof a human at the node consented
/// that the server cannot forge.
/// </summary>
public static class SealEndpoints
{
    private sealed class PendingSeal
    {
        public string   Id            = "";
        public string   ToolName      = "";
        public string   ArgsPreview   = "";
        public string   Reason        = "";
        public byte[]   Nonce         = [];
        public bool     SignStatement = true; // false = durable grant, sign raw nonce bytes
        public string   Status        = "pending"; // pending | approved | rejected | consumed
        public string?  Signature;
        public string?  Statement;   // the exact human-readable statement the human approved
        public DateTime CreatedAt     = DateTime.UtcNow;
        public DateTime ExpiresAt     = DateTime.UtcNow;
    }

    private static readonly ConcurrentDictionary<string, PendingSeal> _pending = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    // Test hook: replace with a no-op to avoid opening browser tabs during automated tests.
    internal static Action<string> LaunchSealPage = LaunchBrowserImpl;

    private static void LaunchBrowserImpl(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* no browser — the page is still reachable manually */ }
    }

    // Reuse the bridge's favicon (an inline data-URI <link> in the status page) so the seal page
    // carries the same icon. Extracted once at runtime — no duplication of the base64 blob.
    private static string? _faviconLink;
    private static string FaviconLink()
    {
        if (_faviconLink != null) return _faviconLink;
        var html = BridgeStatusPage.Build();
        var i = html.IndexOf("<link rel=\"icon\"", StringComparison.Ordinal);
        var j = i >= 0 ? html.IndexOf('>', i) : -1;
        _faviconLink = (i >= 0 && j > i) ? html.Substring(i, j - i + 1) : "";
        return _faviconLink;
    }

    /// <summary>True if the given seal id exists and has been locally approved.</summary>
    public static bool IsSealApproved(string id)
    {
        Prune();
        return _pending.TryGetValue(id, out var s) && s.Status == "approved";
    }

    /// <summary>
    /// Consumes an approved seal if it exists and was requested for <paramref name="expectedToolName"/>.
    /// Returns true and marks the seal "consumed" on success; returns false if the seal is missing,
    /// not approved, for the wrong capability, or already consumed. Consumed seals cannot be replayed.
    /// </summary>
    public static bool TryConsumeSeal(string id, string expectedToolName)
    {
        Prune();
        if (!_pending.TryGetValue(id, out var s)) return false;
        if (s.Status != "approved") return false;
        if (!string.Equals(s.ToolName, expectedToolName, StringComparison.OrdinalIgnoreCase)) return false;

        s.Status = "consumed";
        return true;
    }

    /// <summary>
    /// Builds the canonical, human-readable statement that the node signs on approval. The server
    /// reconstructs this exact text to verify the signature, and the approval page renders it verbatim
    /// so the human sees exactly what they are authorising (F-6).
    /// </summary>
    private static string BuildStatement(PendingSeal s) =>
        SealStatement.Build(s.ToolName, s.Reason, s.ArgsPreview, s.ExpiresAt, s.Nonce);

    public static void MapSealEndpoints(this WebApplication app)
    {
        // POST /seal/request — server asks for a seal. Stores it, opens the local approval page,
        // returns the id immediately (well under the tunnel's 15s budget).
        app.MapPost("/seal/request", (SealRequestDto req) =>
        {
            Prune();
            var id = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            _pending[id] = new PendingSeal
            {
                Id            = id,
                ToolName      = req.ToolName ?? "",
                ArgsPreview   = req.ArgsPreview ?? "",
                Reason        = req.Reason ?? "",
                Nonce         = SafeFromBase64(req.NonceBase64),
                SignStatement = req.SignStatement,
                CreatedAt     = now,
                ExpiresAt     = now + Ttl,
            };

            // The local bridge UI opens the page itself (window.open); only auto-launch for tunnel callers
            // that have no browser of their own, so the human never gets two identical seal windows.
            if (!req.OpenHere)
                LaunchSealPage($"http://localhost:5741/seal/{id}");

            return Results.Ok(new { id });
        });

        // POST /seal/poll — server polls for the verdict. Returns the signed human-readable statement
        // so the server can verify the signature covers exactly what the human saw (F-6).
        app.MapPost("/seal/poll", (SealPollDto req) =>
        {
            if (req.Id != null && _pending.TryGetValue(req.Id, out var s))
                return Results.Ok(new { status = s.Status, signatureBase64 = s.Signature, statement = s.Statement, expiresAt = s.ExpiresAt, signStatement = s.SignStatement });
            return Results.Ok(new { status = "expired", signatureBase64 = (string?)null, statement = (string?)null, expiresAt = (DateTime?)null, signStatement = (bool?)null });
        });

        // GET /seal/{id} — the local approval page the human sees.
        app.MapGet("/seal/{id}", (string id) =>
        {
            if (!_pending.TryGetValue(id, out var s))
                return Results.Content(NotFoundPage(), "text/html");
            // Once resolved, show a styled result page instead of stale approve/reject buttons.
            if (s.Status != "pending")
                return Results.Content(ResolvedPage(s), "text/html");
            return Results.Content(RenderPage(s), "text/html");
        });

        // POST /seal/{id}/approve — human authorises; the node signs the canonical human-readable
        // statement with the soul key. The signature covers exactly what the human saw (F-6).
        // With `Accept: application/json` (the seal page's fetch) this returns {ok, message} instead
        // of navigating to an HTML result page — the tab stays on its FIRST document, so it remains
        // script-closable (single-entry-history rule) and the page can window.close() itself.
        app.MapPost("/seal/{id}/approve", async (string id, HttpRequest httpReq, BridgeDbContext db, SecurityAuditLog audit) =>
        {
            var json = WantsJson(httpReq);
            if (!_pending.TryGetValue(id, out var s))
                return json ? Results.Json(new { ok = false, message = "Seal not found or expired." })
                            : Results.Content(NotFoundPage(), "text/html");
            if (s.Status != "pending")
                return json ? Results.Json(new { ok = false, message = "This seal has already been resolved." })
                            : Results.Content(Done("This seal has already been resolved.", success: false), "text/html");

            var soul = await db.Souls.AsNoTracking().FirstOrDefaultAsync(x => x.Name != "")
                       ?? await db.Souls.AsNoTracking().FirstOrDefaultAsync();
            if (soul?.PrivateKeyBase64 is null)
                return json ? Results.Json(new { ok = false, message = "No soul keypair on this node." })
                            : Results.Content(Done("No soul keypair on this node.", success: false), "text/html");

            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(soul.PrivateKeyBase64), out _);

                if (s.SignStatement)
                {
                    s.Statement = BuildStatement(s);
                    var statementBytes = Encoding.UTF8.GetBytes(s.Statement);
                    s.Signature = Convert.ToBase64String(ecdsa.SignData(statementBytes, HashAlgorithmName.SHA256));
                }
                else
                {
                    // Durable grant: signature covers the caller's exact payload bytes (defense-in-depth §5).
                    s.Signature = Convert.ToBase64String(ecdsa.SignData(s.Nonce, HashAlgorithmName.SHA256));
                }

                s.Status = "approved";
                audit.Record("seal", "approved", allowed: true, capability: s.ToolName,
                    detail: $"Seal {id[..8]} approved for {s.ToolName}: {s.Reason}");
                return json ? Results.Json(new { ok = true, message = "SEAL GRANTED — you may return to Aria." })
                            : Results.Content(Done("SEAL GRANTED — you may return to Aria.", success: true), "text/html");
            }
            catch (Exception ex)
            {
                return json ? Results.Json(new { ok = false, message = $"Signing failed: {ex.Message}" })
                            : Results.Content(Done($"Signing failed: {ex.Message}", success: false), "text/html");
            }
        });

        // POST /seal/{id}/reject — human refuses. Same JSON negotiation as /approve.
        app.MapPost("/seal/{id}/reject", (string id, HttpRequest httpReq, SecurityAuditLog audit) =>
        {
            if (_pending.TryGetValue(id, out var s) && s.Status == "pending")
            {
                s.Status = "rejected";
                audit.Record("seal", "rejected", allowed: false, capability: s.ToolName,
                    detail: $"Seal {id[..8]} rejected for {s.ToolName}: {s.Reason}");
            }
            return WantsJson(httpReq)
                ? Results.Json(new { ok = true, message = "SEAL REFUSED — the action was blocked." })
                : Results.Content(Done("SEAL REFUSED — the action was blocked.", success: false), "text/html");
        });
    }

    private static void Prune()
    {
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var kv in _pending)
            if (kv.Value.CreatedAt < cutoff) _pending.TryRemove(kv.Key, out _);
    }

    private static byte[] SafeFromBase64(string? b64)
    {
        try { return string.IsNullOrEmpty(b64) ? [] : Convert.FromBase64String(b64); }
        catch { return []; }
    }

    private static string E(string v) => System.Net.WebUtility.HtmlEncode(v);

    // The seal page resolves via fetch() with this header; plain form posts (no header) get HTML.
    private static bool WantsJson(HttpRequest req) =>
        req.Headers.Accept.Any(v => v?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Attempts to close the seal tab after 2 s. Only works for tabs opened by script
    /// (window.open / Process.Start from the browser); if the browser blocks it, the page stays
    /// open with a manual-close fallback message.</summary>
    private static string AutoCloseScript() =>
        """
        <script>
          (function() {
            setTimeout(function() {
              try { window.close(); } catch (e) {}
              // Some browsers require navigating to a blank page before allowing close.
              try { window.open('', '_self').close(); } catch (e) {}
            }, 2000);
          })();
        </script>
        """;

    private static string CloseButton() =>
        """
        <button class="btn close" type="button" onclick="try { window.close(); } catch (e) {} try { window.open('', '_self').close(); } catch (e) {}">
          ✕ Close this page
        </button>
        """;

    private static string RenderPage(PendingSeal s)
    {
        return $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Inquisitorial Seal</title>
        <meta name="viewport" content="width=device-width, initial-scale=1">
        {{FaviconLink()}}
        {{SealStyles()}}
        </head><body>
          <div class="card seal-request">
            <div class="seal-watermark">⛨</div>
            <div class="seal-badge">⛨</div>
            <h1>INQUISITORIAL SEAL REQUESTED</h1>
            <div class="subtitle">A high-stakes action requires your explicit consent on this node.</div>
            <div class="field"><span class="field-label">Capability</span><span class="field-value tool">{{E(s.ToolName)}}</span></div>
            <div class="field"><span class="field-label">Reason</span><span class="field-value">{{E(s.Reason)}}</span></div>
            <pre>{{E(s.ArgsPreview)}}</pre>
            <div class="field" style="margin-top:18px"><span class="field-label">Statement you are authorising</span></div>
            <pre class="statement">{{E(BuildStatement(s))}}</pre>
            <div class="actions">
              <button class="btn ok" id="seal-approve" type="button">⛨ GRANT SEAL &amp; CLOSE</button>
              <button class="btn no" id="seal-reject" type="button">✕ REFUSE &amp; CLOSE</button>
            </div>
            <div class="note">This authorisation is signed by your soul private key, which never leaves this machine.
              No remote server — including the hosted Aria.Web — can grant it on your behalf.</div>
          </div>
          <script>
            // Resolve via fetch instead of a form navigation: the tab stays on its FIRST document, so
            // it keeps the single-entry session history that makes window.close() legal for OS-opened
            // tabs. Navigating to a result page (the old flow) added a second entry and every close
            // attempt after that was silently blocked by the browser.
            (function() {
              var approveBtn = document.getElementById('seal-approve');
              var rejectBtn  = document.getElementById('seal-reject');

              function resolveSeal(action) {
                approveBtn.disabled = rejectBtn.disabled = true;
                fetch('/seal/{{s.Id}}/' + action, { method: 'POST', headers: { 'Accept': 'application/json' } })
                  .then(function(r) { return r.json(); })
                  .then(function(d) {
                    var granted = action === 'approve' && d.ok;
                    var failed  = action === 'approve' && !d.ok;
                    showVerdict(granted ? '✓' : '✕',
                                granted ? 'SEAL GRANTED' : (failed ? 'SEAL NOT GRANTED' : 'SEAL REFUSED'),
                                d.message || '', granted ? 'success' : 'error', !failed);
                  })
                  .catch(function() {
                    showVerdict('✕', 'REQUEST FAILED', 'Could not reach the node — is the bridge still running?', 'error', false);
                  });
              }

              // Swaps the card to the verdict in place, then self-closes (only when the action actually
              // resolved — errors stay open so the human can read why). If the browser still refuses to
              // close, the text falls back to a manual hint.
              function showVerdict(icon, title, message, kind, autoClose) {
                var card = document.querySelector('.card');
                card.className = 'card ' + kind;
                card.innerHTML =
                  '<div class="seal-badge">' + icon + '</div>' +
                  '<h1>' + title + '</h1>' +
                  '<div class="subtitle" id="seal-verdict-sub"></div>';
                var sub = document.getElementById('seal-verdict-sub');
                sub.textContent = message + (autoClose ? ' This tab will close itself.' : '');
                if (!autoClose) return;
                setTimeout(function() {
                  try { window.close(); } catch (e) {}
                  try { window.open('', '_self').close(); } catch (e) {}
                  setTimeout(function() { sub.textContent = message + ' You can close this tab now.'; }, 400);
                }, 900);
              }

              approveBtn.addEventListener('click', function() { resolveSeal('approve'); });
              rejectBtn.addEventListener('click',  function() { resolveSeal('reject');  });
            })();
          </script>
        </body></html>
        """;
    }

    private static string Done(string msg, bool success)
    {
        string e = System.Net.WebUtility.HtmlEncode(msg);
        var kind = success ? "success" : "error";
        var icon = success ? "✓" : "✕";
        var subtitle = success
            ? "This tab will close automatically. If it stays open, use the button below."
            : "The action was blocked. You may close this page.";
        return $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Seal — {{kind}}</title>
        <meta name="viewport" content="width=device-width, initial-scale=1">
        {{FaviconLink()}}
        {{SealStyles()}}
        {{(success ? AutoCloseScript() : "")}}
        </head><body>
          <div class="card {{kind}}">
            <div class="seal-badge">{{icon}}</div>
            <h1>{{e}}</h1>
            <div class="subtitle">{{subtitle}}</div>
            <div class="actions single">
              {{CloseButton()}}
            </div>
          </div>
        </body></html>
        """;
    }

    private static string ResolvedPage(PendingSeal s)
    {
        var approved = s.Status is "approved" or "consumed";
        var kind     = approved ? "success" : "error";
        var icon     = approved ? "⛨" : "✕";
        var title    = approved ? "SEAL ALREADY GRANTED" : "SEAL ALREADY REFUSED";
        var subtitle = approved
            ? "This seal has already been approved. Each seal may be used only once; if it has been consumed, request a fresh seal from Aria."
            : "This seal was refused. The requesting action was blocked at this node.";
        return $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Seal — {{kind}}</title>
        <meta name="viewport" content="width=device-width, initial-scale=1">
        {{FaviconLink()}}
        {{SealStyles()}}
        {{(approved ? AutoCloseScript() : "")}}
        </head><body>
          <div class="card {{kind}}">
            <div class="seal-badge">{{icon}}</div>
            <h1>{{title}}</h1>
            <div class="subtitle">{{subtitle}}</div>
            <div class="field"><span class="field-label">Capability</span><span class="field-value tool">{{E(s.ToolName)}}</span></div>
            <div class="actions single">
              {{CloseButton()}}
            </div>
            <div class="note">This page is shown because the seal request was already resolved. If you did not perform that action, return to Aria and check your node status.</div>
          </div>
        </body></html>
        """;
    }

    private static string NotFoundPage()
    {
        return $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Seal — not found</title>
        <meta name="viewport" content="width=device-width, initial-scale=1">
        {{FaviconLink()}}
        {{SealStyles()}}
        </head><body>
          <div class="card error">
            <div class="seal-badge">✕</div>
            <h1>SEAL NOT FOUND OR EXPIRED</h1>
            <div class="subtitle">This seal request has already been resolved, timed out, or was never created.</div>
            <div class="actions single">
              {{CloseButton()}}
            </div>
            <div class="note">If you expected an approval prompt, the requesting action may have been cancelled. Return to Aria and retry if needed.</div>
          </div>
        </body></html>
        """;
    }

    private static string SealStyles()
    {
        return """
        <style>
          :root{
            --bg:#0a0806;--card:#160b0b;--gold:#b09040;--gold-bright:#f0d060;
            --red:#ff5050;--red-dim:#c05050;--green:#5faf5f;--amber:#d8c89a;
            --text:#d8c89a;--muted:#a09070;--dim:#6d5d45;--border:#533;--shadow:rgba(139,0,0,.45);
          }
          *{box-sizing:border-box}
          body{
            background:var(--bg);color:var(--text);font-family:ui-monospace,Menlo,Consolas,monospace;
            display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0;
            padding:20px;background-image:radial-gradient(circle at 50% 0%,rgba(176,144,64,.08),transparent 60%)
          }
          .card{
            border:1px solid var(--gold);background:var(--card);padding:36px 40px;max-width:560px;width:100%;
            box-shadow:0 0 40px var(--shadow);border-radius:2px;position:relative;overflow:hidden;
            -webkit-font-smoothing:antialiased;-moz-osx-font-smoothing:grayscale
          }
          .card::before{
            content:"";position:absolute;top:0;left:0;right:0;height:3px;
            background:linear-gradient(90deg,var(--gold),var(--red),var(--gold))
          }
          .card.error{--gold:var(--red-dim);--gold-bright:var(--red);--shadow:rgba(139,0,0,.55)}
          .card.error::before{background:linear-gradient(90deg,var(--red-dim),var(--red),var(--red-dim))}
          .card.success{--gold:var(--green);--gold-bright:#8fdf8f;--shadow:rgba(80,160,80,.35)}
          .card.success::before{background:linear-gradient(90deg,var(--green),#8fdf8f,var(--green))}
          @keyframes pulse{
            0%,100%{box-shadow:0 0 0 0 rgba(176,144,64,.28)}
            50%{box-shadow:0 0 0 14px rgba(176,144,64,0)}
          }
          .seal-request .seal-badge{animation:pulse 2.6s infinite}
          .seal-watermark{
            position:absolute;right:-18px;bottom:-28px;font-size:150px;opacity:.035;
            color:var(--gold);pointer-events:none;user-select:none;line-height:1;z-index:0
          }
          .seal-badge{
            width:52px;height:52px;border:1px solid var(--gold);border-radius:50%;display:flex;
            align-items:center;justify-content:center;font-size:24px;color:var(--gold-bright);
            margin-bottom:18px;background:rgba(176,144,64,.08)
          }
          .card.error .seal-badge{border-color:var(--red);color:var(--red);background:rgba(139,0,0,.12)}
          .card.success .seal-badge{border-color:var(--green);color:#8fdf8f;background:rgba(80,160,80,.12)}
          h1{font-size:16px;letter-spacing:2.5px;color:var(--gold-bright);margin:0 0 10px;font-weight:600}
          .subtitle{font-size:12px;color:var(--muted);margin-bottom:24px;line-height:1.5}
          .field{display:flex;flex-direction:column;gap:4px;margin-bottom:14px}
          .field-label{font-size:10px;letter-spacing:1.5px;color:var(--dim);text-transform:uppercase}
          .field-value{font-size:13px;color:var(--text)}
          .tool{font-weight:600;color:var(--gold-bright);letter-spacing:.5px}
          pre{
            background:#0a0606;border:1px solid var(--border);padding:12px;font-size:12px;
            white-space:pre-wrap;word-break:break-all;color:var(--muted);border-radius:2px;margin:18px 0 0
          }
          pre.statement{
            border-color:var(--gold);color:var(--text);background:#0f0a08;
            font-size:11px;line-height:1.6
          }
          .actions{display:flex;gap:14px;margin-top:26px}
          .actions.single{justify-content:center}
          .btn{
            flex:1;padding:13px 10px;font-family:inherit;font-size:11px;letter-spacing:1.5px;
            cursor:pointer;border-radius:2px;border:1px solid transparent;transition:filter .15s,transform .05s;
            text-transform:uppercase;font-weight:600
          }
          .btn:hover{filter:brightness(1.15)}
          .btn:active{transform:translateY(1px)}
          .btn.ok{background:var(--gold);color:#160b0b}
          .btn.no{background:transparent;border-color:var(--red-dim);color:var(--red-dim)}
          .btn.no:hover{background:rgba(139,0,0,.12)}
          .btn.close{
            background:transparent;border-color:var(--dim);color:var(--muted);
            max-width:220px;flex:0 1 auto
          }
          .btn.close:hover{background:rgba(109,93,69,.15);border-color:var(--muted);color:var(--text)}
          .note{font-size:10px;color:var(--dim);margin-top:24px;line-height:1.6;border-top:1px solid var(--border);padding-top:14px}
          @media (max-width:560px){
            .card{padding:26px 22px}
            .actions{flex-direction:column}
          }
        </style>
        """;
    }

    // OpenHere: when true, the caller (the local bridge status page) opens the approval page itself via
    // window.open, so the node must NOT also launch it — otherwise two identical seal windows appear.
    // Tunnel callers (Aria.Web has no browser here) leave it false so the node launches the page.
    public record SealRequestDto(string? ToolName, string? ArgsPreview, string? Reason, string? NonceBase64, bool SignStatement = true, bool OpenHere = false);
    public record SealPollDto(string? Id);
}
