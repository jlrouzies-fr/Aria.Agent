using System.Collections.Concurrent;
using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Infrastructure;
using Aria.Bridge.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// Layer B context approval (defense-in-depth plan §4). A human at the node grants this soul's
/// sensitive server-relayed operations (provider-key spend, shell, tool execution) a time-boxed pass;
/// while a grant is live the bridge stops re-prompting. Local-only surface (the daemon binds to
/// localhost), so only software on the user's own machine can approve.
/// </summary>
public static class ContextEndpoints
{
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromHours(8);

    public static void MapContextEndpoints(this WebApplication app)
    {
        // GET /context/approve[?session=…] — the local approval page a human sees (auto-opened when a
        // sensitive op is blocked under enforcement). With a session, the grant is scoped to that one
        // browser session; without, it's soul-wide (legacy).
        app.MapGet("/context/approve", async (BridgeDbContext db, string? session) =>
        {
            var soul   = await ActiveSoulAsync(db);
            var soulId = soul?.ServerSoulId ?? "";
            var ctxId  = ContextGrantStore.ContextId(soulId, session);
            var live   = soul != null && soulId.Length > 0 && await ContextGrantStore.HasValidGrantAsync(db, soul, ctxId);
            return Results.Content(RenderPage(soulId, session, live), "text/html");
        });

        // POST /context/approve — the human authorises; sign + store a time-boxed grant. Scope is the
        // session carried in the form (this browser session) or soul-wide when absent.
        app.MapPost("/context/approve", async (BridgeDbContext db, HttpRequest http) =>
        {
            var soul = await ActiveSoulAsync(db);
            if (soul == null || string.IsNullOrEmpty(soul.ServerSoulId))
                return Results.Content(Done("No linked soul on this node."), "text/html");
            var session = http.HasFormContentType && http.Form.TryGetValue("session", out var s) ? s.ToString() : null;
            var ctxId   = ContextGrantStore.ContextId(soul.ServerSoulId, session);
            var ok      = await ContextGrantStore.GrantAsync(db, soul, ctxId, GrantLifetime);
            var scopeMsg = string.IsNullOrEmpty(session) ? "for this soul" : "for this browser session";
            return Results.Content(Done(ok
                ? $"✓ Sensitive operations authorised {scopeMsg} for {GrantLifetime.TotalHours:0}h. You may return to the terminal."
                : "This node has no signing key — cannot issue a grant."), "text/html");
        });

        // POST /context/revoke — end the pass immediately (session-scoped when a session is supplied).
        app.MapPost("/context/revoke", async (BridgeDbContext db, HttpRequest http) =>
        {
            var soul = await ActiveSoulAsync(db);
            if (soul?.ServerSoulId is { } soulId)
            {
                var session = http.HasFormContentType && http.Form.TryGetValue("session", out var s) ? s.ToString() : null;
                await ContextGrantStore.RevokeAsync(db, ContextGrantStore.ContextId(soulId, session));
            }
            return Results.Ok(new { ok = true });
        });

        // GET /context/status — enforcement + whether a grant is live + when it lapses (for the status
        // page, diagnostics, and the header seal countdown). expiryUnix is the effective grant expiry
        // for this session (later of the session-scoped and soul-wide grants), or null when none is live.
        app.MapGet("/context/status", async (BridgeDbContext db, string? session) =>
        {
            var soul   = await ActiveSoulAsync(db);
            var soulId = soul?.ServerSoulId ?? "";
            var ctx    = ContextGrantStore.ContextId(soulId, session);
            var expiry = soul != null ? await ContextGrantStore.EffectiveGrantExpiryAsync(db, soul, session) : null;
            return Results.Ok(new
            {
                enforcementEnabled = ContextGrantStore.EnforcementEnabled,
                contextId          = ctx,
                granted            = expiry is { } e && e > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                expiryUnix         = expiry,
            });
        });

        // GET /context/enforcement — current Layer B enforcement state (for the bridge UI toggle).
        app.MapGet("/context/enforcement", () =>
            Results.Ok(new { enabled = ContextGrantStore.EnforcementEnabled }));

        // POST /context/enforcement — flip the Layer B enforcement toggle. Local-origin only (never on
        // the tunnel allowlist), so only a human at this machine can change the node's security posture.
        app.MapPost("/context/enforcement", async (EnforcementDto req, BridgeDbContext db, SecurityAuditLog audit) =>
        {
            await ContextGrantStore.SetEnforcementAsync(db, req.Enabled);
            audit.Record("layer-b", req.Enabled ? "enforcement-enabled" : "enforcement-disabled", allowed: true, capability: "context-grants");
            return Results.Ok(new { enabled = ContextGrantStore.EnforcementEnabled });
        });

        // GET /context/grants/export — the node's live, signed grants AND revocation tombstones, for
        // mesh replication. The server relays these to sibling nodes; it can't read anything sensitive
        // (a grant is just soul id + expiry + signature) and can't forge one — nor a revocation.
        // Tombstones ride as GrantType="revoke" entries: an old sibling rejects them as unverifiable
        // grants (fail closed) instead of reviving a revoked grant.
        app.MapGet("/context/grants/export", async (BridgeDbContext db) =>
        {
            var grants = await ContextGrantStore.ExportLiveGrantsAsync(db);
            var revocations = await ContextGrantStore.ExportRevocationsAsync(db);
            return Results.Ok(grants
                .Select(g => new ContextGrantDto(g.ContextId, g.GrantType, g.ExpiryUnix, g.SignatureBase64))
                .Concat(revocations.Select(r => new ContextGrantDto(
                    r.ContextId, ContextGrantStore.RevocationGrantType, r.GrantExpiryUnix, r.SignatureBase64))));
        });

        // POST /context/grants/import — accept grants and revocation tombstones from a sibling. Each
        // is stored only if its signature verifies under one of this soul's acceptable keys.
        app.MapPost("/context/grants/import", async (ContextGrantDto[] grants, BridgeDbContext db) =>
        {
            var soul = await ActiveSoulAsync(db);
            if (soul == null) return Results.Ok(new { imported = 0 });
            var imported = 0;
            foreach (var g in grants ?? [])
            {
                if (g.GrantType == ContextGrantStore.RevocationGrantType)
                {
                    if (await ContextGrantStore.ImportRevocationAsync(db, soul, g.ContextId, g.ExpiryUnix, g.SignatureBase64))
                        imported++;
                    continue;
                }
                var row = new ContextGrant { ContextId = g.ContextId, GrantType = g.GrantType, ExpiryUnix = g.ExpiryUnix, SignatureBase64 = g.SignatureBase64 };
                if (await ContextGrantStore.ImportGrantAsync(db, soul, row)) imported++;
            }
            return Results.Ok(new { imported });
        });

        // ── Server-driven in-chat approval ceremony (reactive approval, Phase 2B) ───────────────

        // POST /context/approve/request — server asks a node with a human present to approve a session-
        // scoped context grant. Stores a pending approval, opens the local page, returns the id.
        // kind="scope" (Wave 5) asks for a session path expansion instead: req.path is normalised and
        // carried on the pending approval; approving mints a node-signed path grant, not a context grant.
        app.MapPost("/context/approve/request", async (ContextApprovalRequestDto req, BridgeDbContext db, SecurityAuditLog audit) =>
        {
            PrunePending();
            var soul = await ActiveSoulAsync(db);
            if (soul == null || string.IsNullOrEmpty(soul.ServerSoulId))
                return Results.Ok(new { id = (string?)null, error = "No linked soul on this node." });

            // A scope expansion is always session-scoped and carries a signable absolute path.
            string? scopePath = null;
            if (req.kind == "scope")
            {
                if (string.IsNullOrEmpty(req.sessionId))
                    return Results.Ok(new { id = (string?)null, error = "A scope expansion needs a session id." });
                scopePath = NormalizeScopePath(req.path);
                if (scopePath == null)
                    return Results.Ok(new { id = (string?)null, error = "A scope expansion needs a valid absolute path (no '|')." });
            }

            // Concurrent callers (two chat tabs, chat + Explorer race) must share ONE pending ceremony.
            // Minting a second id would LaunchContextPage again → double popup; the human then approves
            // one page while the other waiter's poll never sees a verdict (or sees a different id).
            if (TryFindReusablePending(req.sessionId, req.kind, scopePath, req.expiryUnix, out var existing))
            {
                audit.Record("context-approval", "requested", allowed: true,
                    detail: req.kind == "scope"
                        ? $"Path expansion approval {existing.Id[..8]} reused for session {req.sessionId}: {scopePath}"
                        : $"Context approval {existing.Id[..8]} reused for session {req.sessionId ?? "(soul-wide)"}");
                return Results.Ok(new { id = existing.Id });
            }

            var id = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            _pending[id] = new PendingContextApproval
            {
                Id                 = id,
                SessionId          = req.sessionId,
                CreatedAt          = now,
                ExpiresAt          = now + PendingTtl,
                Status             = "pending",
                ExpiryUnixOverride = req.expiryUnix,
                Kind               = req.kind,
                TaskPreview        = req.taskPreview,
                SlotLabel          = req.slotLabel,
                Path               = scopePath,
            };

            var url = $"http://localhost:5741/context/approve/{id}"
                + (string.IsNullOrEmpty(req.sessionId) ? "" : $"?session={Uri.EscapeDataString(req.sessionId)}");
            LaunchContextPage(url);

            audit.Record("context-approval", "requested", allowed: true,
                detail: req.kind == "scope"
                    ? $"Path expansion approval {id[..8]} requested for session {req.sessionId}: {scopePath}"
                    : $"Context approval {id[..8]} requested for session {req.sessionId ?? "(soul-wide)"}");
            return Results.Ok(new { id });
        });

        // GET /context/approve/{id} — local approval page for the in-chat ceremony.
        app.MapGet("/context/approve/{id}", (string id, string? session) =>
        {
            PrunePending();
            if (!_pending.TryGetValue(id, out var p))
                return Results.Content(NotFoundPage(), "text/html");
            if (p.Status != "pending")
                return Results.Content(Done(p.Status == "approved"
                    ? "✓ Context authorised. You may return to the terminal."
                    : "Context approval refused."), "text/html");
            return Results.Content(RenderCeremonyPage(p), "text/html");
        });

        // POST /context/approve/{id}/approve — human authorises; sign + store the session-scoped grant.
        // With `Accept: application/json` (the ceremony page's fetch) this returns {ok, message} instead
        // of navigating to a result page — the tab stays on its FIRST document, so it keeps the
        // single-entry history that makes window.close() legal and can close itself after the verdict.
        app.MapPost("/context/approve/{id}/approve", async (string id, HttpRequest http, BridgeDbContext db, SecurityAuditLog audit) =>
        {
            var json = WantsJson(http);
            PrunePending();
            if (!_pending.TryGetValue(id, out var p) || p.Status != "pending")
                return json ? Results.Json(new { ok = false, message = "This approval has already been resolved or expired." })
                            : Results.Content(NotFoundPage(), "text/html");

            var soul = await ActiveSoulAsync(db);
            if (soul == null || string.IsNullOrEmpty(soul.ServerSoulId))
                return json ? Results.Json(new { ok = false, message = "No linked soul on this node." })
                            : Results.Content(Done("No linked soul on this node."), "text/html");

            // Wave 5 scope expansion: the human authorises ONE additional directory for ONE session.
            // Mints a node-signed path grant (not a context grant) — verified on every use, and
            // replicated to siblings through the same export/import channel as context grants.
            if (p.Kind == "scope")
            {
                if (string.IsNullOrEmpty(p.Path) || string.IsNullOrEmpty(p.SessionId))
                    return json ? Results.Json(new { ok = false, message = "This scope approval is missing its path or session." })
                                : Results.Content(Done("This scope approval is missing its path or session."), "text/html");
                var okScope = await ContextGrantStore.GrantPathAsync(db, soul, p.SessionId, p.Path, GrantLifetime);
                if (!okScope)
                    return json ? Results.Json(new { ok = false, message = "This node has no signing key — cannot issue a grant." })
                                : Results.Content(Done("This node has no signing key — cannot issue a grant."), "text/html");
                p.Status = "approved";
                audit.Record("context-approval", "approved", allowed: true, capability: "path-grant",
                    detail: $"Path expansion {id[..8]} granted for session {p.SessionId}: {p.Path}");
                var scopeMsg = $"✓ Path authorised for 8h — this browser session may now work under {p.Path}. You may close this.";
                return json ? Results.Json(new { ok = true, message = scopeMsg })
                            : Results.Content(Done(scopeMsg), "text/html");
            }

            var ctxId = ContextGrantStore.ContextId(soul.ServerSoulId, p.SessionId);

            // A Hive pre-authorisation lets the human choose on the page ("hours" field): "once" is a
            // one-shot pass — signed for a safe run window but flagged so the server revokes it the moment
            // the run ends, so the next launch re-asks; a number (1–8) holds the seal open that long so
            // back-to-back launches don't re-prompt. A vigil carries a fixed absolute slot-end expiry;
            // a normal in-chat approval is the flat 8h window.
            long? chosenExpiry = null;
            double chosenHours = 0;
            bool oneShot = false;
            if (http.HasFormContentType && http.Form.TryGetValue("hours", out var hv))
            {
                if (string.Equals(hv, "once", StringComparison.OrdinalIgnoreCase))
                {
                    oneShot      = true;                 // revoked on run completion (see server orchestrator)
                    chosenExpiry = DateTimeOffset.UtcNow.Add(GrantLifetime).ToUnixTimeSeconds();  // safety cap
                }
                else if (double.TryParse(hv, System.Globalization.CultureInfo.InvariantCulture, out var hours) && hours > 0)
                {
                    chosenHours  = Math.Min(hours, 168); // cap at one week
                    chosenExpiry = DateTimeOffset.UtcNow.AddHours(chosenHours).ToUnixTimeSeconds();
                }
            }

            var effectiveExpiry = chosenExpiry ?? p.ExpiryUnixOverride;
            var ok = effectiveExpiry is { } slotExpiry
                ? await ContextGrantStore.GrantAtAsync(db, soul, ctxId, slotExpiry)
                : await ContextGrantStore.GrantAsync(db, soul, ctxId, GrantLifetime);
            if (!ok)
            {
                var failMsg = effectiveExpiry is { } e && e <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    ? "That slot has already passed — nothing to authorise."
                    : "This node has no signing key — cannot issue a grant.";
                return json ? Results.Json(new { ok = false, message = failMsg })
                            : Results.Content(Done(failMsg), "text/html");
            }

            p.Status  = "approved";
            p.OneShot = oneShot;
            audit.Record("context-approval", "approved", allowed: true, capability: "context-grant",
                detail: $"Context approval {id[..8]} granted for {(p.Kind == "vigil" ? $"vigil {p.SessionId}" : $"session {p.SessionId ?? "(soul-wide)"}")}"
                      + (oneShot ? " (one-shot)" : chosenHours > 0 ? $" ({chosenHours:0.#}h)" : ""));
            var hiveWindow = oneShot ? "this run only" : chosenHours > 0 ? $"{chosenHours:0.#}h" : "its run window";
            var okMsg = p.Kind switch
            {
                "vigil" => "✓ Vigil pre-authorised for its scheduled window. It will run unattended. You may close this.",
                "hive"  => $"✓ Collective pre-authorised for {hiveWindow}. Its drones will act unattended. You may close this.",
                _       => "✓ Context authorised for 8h. You may return to the terminal.",
            };
            return json ? Results.Json(new { ok = true, message = okMsg })
                        : Results.Content(Done(okMsg), "text/html");
        });

        // POST /context/approve/{id}/reject — human refuses. Same JSON negotiation as /approve.
        app.MapPost("/context/approve/{id}/reject", (string id, HttpRequest http, SecurityAuditLog audit) =>
        {
            PrunePending();
            if (_pending.TryGetValue(id, out var p) && p.Status == "pending")
            {
                p.Status = "rejected";
                audit.Record("context-approval", "rejected", allowed: false, capability: "context-grant",
                    detail: $"Context approval {id[..8]} rejected for session {p.SessionId ?? "(soul-wide)"}");
            }
            return WantsJson(http)
                ? Results.Json(new { ok = true, message = "Context approval refused." })
                : Results.Content(Done("Context approval refused."), "text/html");
        });

        // POST /context/approve/{id}/poll — server polls for the verdict. `oneShot` tells the server this
        // was a "this run only" Hive grant, so it revokes it when the run ends (see grants/revoke).
        app.MapPost("/context/approve/{id}/poll", (string id) =>
        {
            PrunePending();
            if (!_pending.TryGetValue(id, out var p)) return Results.Ok(new { status = "expired", oneShot = false });
            return Results.Ok(new { status = p.Status, oneShot = p.OneShot });
        });

        // POST /context/grants/revoke — end a session's grant immediately (JSON, tunnel-callable — unlike
        // the form-based /context/revoke). Used to retire a "this run only" Hive seal when its run ends.
        app.MapPost("/context/grants/revoke", async (RevokeSessionDto req, BridgeDbContext db) =>
        {
            var soul = await ActiveSoulAsync(db);
            if (soul?.ServerSoulId is not { Length: > 0 } soulId) return Results.Ok(new { revoked = false });
            await ContextGrantStore.RevokeAsync(db, ContextGrantStore.ContextId(soulId, req.Session));
            return Results.Ok(new { revoked = true });
        });

        // ── Session path expansions (Wave 5, "/scope" chat command) ──────────────────────────────
        // Read-only status/list plus a narrowing revoke. None of these can widen anything: the grant
        // itself is only ever minted by the local approval ceremony above, signed by this node.

        // GET /scope/status?session=…&path=… — is a live, verified path grant in force for this exact
        // path and session? Lets the server skip a redundant ceremony (idempotent /scope add).
        app.MapGet("/scope/status", async (BridgeDbContext db, string? session, string? path) =>
        {
            var soul = await ActiveSoulAsync(db);
            var norm = NormalizeScopePath(path);
            var live = norm != null && await ContextGrantStore.HasLivePathGrantAsync(db, soul, session, norm);
            return Results.Ok(new { granted = live, path = norm });
        });

        // GET /scope/list?session=… — the session's live path expansions (drives the chat /scope display).
        app.MapGet("/scope/list", async (BridgeDbContext db, string? session) =>
        {
            var soul   = await ActiveSoulAsync(db);
            var grants = await ContextGrantStore.GetLiveSessionPathGrantsAsync(db, soul, session);
            return Results.Ok(grants.Select(g => new { path = g.Path, expiryUnix = g.ExpiryUnix }));
        });

        // POST /scope/revoke — end a session's path expansion immediately. The node signs a revocation
        // tombstone that replicates to siblings through the same export/import channel as the grants.
        app.MapPost("/scope/revoke", async (ScopeRevokeDto req, BridgeDbContext db, SecurityAuditLog audit) =>
        {
            var soul = await ActiveSoulAsync(db);
            var norm = NormalizeScopePath(req.Path);
            var revoked = false;
            if (soul?.ServerSoulId is { Length: > 0 } soulId && norm != null && !string.IsNullOrEmpty(req.SessionId))
            {
                await ContextGrantStore.RevokePathAsync(db, soulId, req.SessionId!, norm);
                revoked = true;
                audit.Record("context-approval", "path-grant-revoked", allowed: true, capability: "path-grant",
                    detail: $"Path expansion revoked for session {req.SessionId}: {norm}");
            }
            return Results.Ok(new { revoked });
        });
    }

    private static async Task<BridgeSoul?> ActiveSoulAsync(BridgeDbContext db) =>
        await db.Souls.AsNoTracking().FirstOrDefaultAsync(x => x.Name != "")
        ?? await db.Souls.AsNoTracking().FirstOrDefaultAsync();

    // The ceremony pages resolve via fetch() with this header; plain form posts (none of our pages
    // use them anymore, but external callers might) still get the HTML result pages.
    private static bool WantsJson(HttpRequest req) =>
        req.Headers.Accept.Any(v => v?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// Shared resolver script for both ceremony pages. Buttons carry <c>data-ctx-action</c>
    /// (approve/reject) and optionally <c>data-ctx-hours</c> (the Hive "once"/1/2/4/8 pass). Resolving
    /// via fetch instead of a form navigation keeps the tab on its FIRST document — the single-entry
    /// session history that makes window.close() legal for OS-opened tabs — so the page can show the
    /// verdict in place and then close itself. Errors stay open so the human can read why.
    /// </summary>
    private static string CeremonyResolverScript(string approvalId) => $$"""
          <script>
            (function() {
              var buttons = document.querySelectorAll('[data-ctx-action]');

              function resolveCtx(action, hours) {
                buttons.forEach(function(b) { b.disabled = true; });
                var opts = { method: 'POST', headers: { 'Accept': 'application/json' } };
                if (hours) {
                  opts.headers['Content-Type'] = 'application/x-www-form-urlencoded';
                  opts.body = 'hours=' + encodeURIComponent(hours);
                }
                fetch('/context/approve/{{approvalId}}/' + action, opts)
                  .then(function(r) { return r.json(); })
                  .then(function(d) {
                    var granted = action === 'approve' && d.ok;
                    var failed  = action === 'approve' && !d.ok;
                    showVerdict(granted ? '✓' : '✕',
                                granted ? 'AUTHORISED' : (failed ? 'NOT AUTHORISED' : 'REFUSED'),
                                d.message || '', !failed);
                  })
                  .catch(function() {
                    showVerdict('✕', 'REQUEST FAILED', 'Could not reach the node — is the bridge still running?', false);
                  });
              }

              function showVerdict(icon, title, message, autoClose) {
                var card = document.querySelector('.card');
                card.innerHTML =
                  '<h1>' + icon + ' ' + title + '</h1>' +
                  '<div class="ctx" id="ctx-verdict-sub"></div>';
                var sub = document.getElementById('ctx-verdict-sub');
                sub.textContent = message + (autoClose ? ' This tab will close itself.' : '');
                if (!autoClose) return;
                setTimeout(function() {
                  try { window.close(); } catch (e) {}
                  try { window.open('', '_self').close(); } catch (e) {}
                  setTimeout(function() { sub.textContent = message + ' You can close this tab now.'; }, 400);
                }, 900);
              }

              buttons.forEach(function(b) {
                b.addEventListener('click', function() {
                  resolveCtx(b.getAttribute('data-ctx-action'), b.getAttribute('data-ctx-hours'));
                });
              });
            })();
          </script>
        """;

    private static string RenderPage(string soulId, string? session, bool live)
    {
        string E(string v) => System.Net.WebUtility.HtmlEncode(v);
        var hasSession = !string.IsNullOrEmpty(session);
        // The scope line the user must read before approving — it's what the grant will cover.
        var scopeLine = hasSession
            ? $"<div class=\"scope\">Scope: <b>THIS BROWSER SESSION</b> · <span class=\"sid\">{E(session!)}</span></div>"
            : "<div class=\"scope\">Scope: <b>THIS SOUL</b> (all sessions on this node)</div>";
        var status = live
            ? "<div class=\"live\">● A grant is currently active for this scope. Re-approve to extend, or revoke below.</div>"
            : "";
        var sessionField = hasSession ? $"<input type=\"hidden\" name=\"session\" value=\"{E(session!)}\">" : "";
        return $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Context Approval</title>
        <style>
          body{background:#0a0806;color:#d8c89a;font-family:ui-monospace,Menlo,monospace;
               display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
          .card{border:1px solid #b09040;border-left:4px solid #d4a020;background:#160f04;
                padding:28px 32px;max-width:560px;box-shadow:0 0 32px rgba(180,140,20,.35)}
          h1{font-size:15px;letter-spacing:3px;color:#f0d060;margin:0 0 14px}
          .ctx{color:#a98;font-size:12px;margin-bottom:8px}
          .scope{color:#e8c060;font-size:12px;margin-bottom:12px;border:1px solid #6a5010;
                 background:#100a02;padding:8px 10px;letter-spacing:1px}
          .scope b{color:#f0d060}
          .sid{color:#a98;word-break:break-all}
          .window{color:#f0d060;font-size:13px;font-weight:bold;letter-spacing:2px;margin:6px 0 12px}
          .live{color:#8fce8f;font-size:12px;margin-bottom:12px}
          .row{display:flex;gap:12px;margin-top:20px}
          button{flex:1;padding:12px;font-family:inherit;font-size:12px;letter-spacing:2px;cursor:pointer;border:none}
          .ok{background:#b09040;color:#160404;font-weight:bold}
          .no{background:#220;color:#c99;border:1px solid #533}
          .note{font-size:10px;color:#765;margin-top:16px;line-height:1.5}
        </style></head><body>
          <div class="card">
            <h1>⛨ AUTHORISE SENSITIVE OPERATIONS</h1>
            <div class="ctx">Soul: {{E(soulId)}}</div>
            {{scopeLine}}
            <div class="window">⏱ VALID FOR 8 HOURS · {{(hasSession ? "THIS BROWSER SESSION" : "THIS SOUL")}}</div>
            {{status}}
            <div>Allow the terminal to run sensitive operations on this machine — provider-key spend,
              shell commands, and tool execution — for the next 8 hours?</div>
            <div class="row">
              <form method="post" action="/context/approve">{{sessionField}}<button class="ok" type="submit">⛨ AUTHORISE 8h</button></form>
              <form method="post" action="/context/revoke">{{sessionField}}<button class="no" type="submit">✕ REVOKE</button></form>
            </div>
            <div class="note">This decision is made locally on your node. The hosted server cannot grant
              it on your behalf. The grant expires automatically after 8 hours.</div>
          </div>
        </body></html>
        """;
    }

    // Server-driven in-chat approval ceremony state.
    private sealed class PendingContextApproval
    {
        public string   Id        = "";
        public string?  SessionId;
        public string   Status    = "pending"; // pending | approved | rejected
        public bool     OneShot;                // hive "this run only" — revoke the grant when the run ends
        public DateTime CreatedAt = DateTime.UtcNow;
        public DateTime ExpiresAt = DateTime.UtcNow;
        // Vigil pre-authorisation extras (null for a normal in-chat session approval).
        public long?    ExpiryUnixOverride;    // absolute slot-end expiry to sign the grant with
        public string?  Kind;                  // "vigil" | "hive" | "scope" | null
        public string?  TaskPreview;           // the scheduled task, shown so the human knows what they authorise
        public string?  SlotLabel;             // human-readable slot, e.g. "Thu 14 Jul · 07:00 UTC"
        public string?  Path;                  // "scope": the normalised absolute directory being authorised
    }

    private static readonly ConcurrentDictionary<string, PendingContextApproval> _pending = new();
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(5);

    // Test hook: replace with a no-op to avoid opening browser tabs during automated tests.
    internal static Action<string> LaunchContextPage = LaunchBrowserImpl;

    private static void LaunchBrowserImpl(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* no browser — the page is still reachable manually */ }
    }

    private static void PrunePending()
    {
        var cutoff = DateTime.UtcNow - PendingTtl;
        foreach (var kv in _pending)
            if (kv.Value.CreatedAt < cutoff) _pending.TryRemove(kv.Key, out _);
    }

    /// <summary>
    /// Finds a still-pending ceremony that covers the same grant the caller would mint. Normal
    /// session grants and same-path scope expansions coalesce; vigil/hive always mint fresh (each
    /// carries its own task/slot/expiry the human must see).
    /// </summary>
    private static bool TryFindReusablePending(
        string? sessionId, string? kind, string? scopePath, long? expiryUnix,
        out PendingContextApproval existing)
    {
        existing = null!;
        // Vigil/hive pre-auths are not interchangeable — different task text / slot / absolute expiry.
        if (kind is "vigil" or "hive") return false;

        var now = DateTime.UtcNow;
        foreach (var kv in _pending)
        {
            var p = kv.Value;
            if (p.Status != "pending" || p.ExpiresAt < now) continue;
            if (!string.Equals(p.SessionId, sessionId, StringComparison.Ordinal)) continue;
            // Reuse only when the grant that would be minted is identical. The expiry override is what
            // /approve actually signs, so a differing one must never be silently inherited.
            if (p.ExpiryUnixOverride != expiryUnix) continue;

            if (string.IsNullOrEmpty(kind) && string.IsNullOrEmpty(p.Kind))
            {
                existing = p;
                return true;
            }

            if (kind == "scope" && p.Kind == "scope"
                && string.Equals(p.Path, scopePath, StringComparison.Ordinal))
            {
                existing = p;
                return true;
            }
        }

        return false;
    }

    private static string RenderCeremonyPage(PendingContextApproval p)
    {
        if (p.Kind is "vigil" or "hive") return RenderUnattendedCeremonyPage(p);
        if (p.Kind == "scope") return RenderScopeCeremonyPage(p);

        string E(string v) => System.Net.WebUtility.HtmlEncode(v);
        var hasSession = !string.IsNullOrEmpty(p.SessionId);
        var scopeLine = hasSession
            ? $"<div class=\"scope\">Scope: <b>THIS BROWSER SESSION</b> · <span class=\"sid\">{E(p.SessionId!)}</span></div>"
            : "<div class=\"scope\">Scope: <b>THIS SOUL</b> (all sessions on this node)</div>";
        return $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Context Approval</title>
        <style>
          body{background:#0a0806;color:#d8c89a;font-family:ui-monospace,Menlo,monospace;
               display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
          .card{border:1px solid #b09040;border-left:4px solid #d4a020;background:#160f04;
                padding:28px 32px;max-width:560px;box-shadow:0 0 32px rgba(180,140,20,.35)}
          h1{font-size:15px;letter-spacing:3px;color:#f0d060;margin:0 0 14px}
          .ctx{color:#a98;font-size:12px;margin-bottom:8px}
          .scope{color:#e8c060;font-size:12px;margin-bottom:12px;border:1px solid #6a5010;
                 background:#100a02;padding:8px 10px;letter-spacing:1px}
          .scope b{color:#f0d060}
          .sid{color:#a98;word-break:break-all}
          .window{color:#f0d060;font-size:13px;font-weight:bold;letter-spacing:2px;margin:6px 0 12px}
          .row{display:flex;gap:12px;margin-top:20px}
          button{flex:1;padding:12px;font-family:inherit;font-size:12px;letter-spacing:2px;cursor:pointer;border:none}
          .ok{background:#b09040;color:#160404;font-weight:bold}
          .no{background:#220;color:#c99;border:1px solid #533}
          .note{font-size:10px;color:#765;margin-top:16px;line-height:1.5}
        </style></head><body>
          <div class="card">
            <h1>⛨ AUTHORISE SENSITIVE OPERATIONS</h1>
            <div class="ctx">Approval request from your terminal</div>
            {{scopeLine}}
            <div class="window">⏱ VALID FOR 8 HOURS · {{(hasSession ? "THIS BROWSER SESSION" : "THIS SOUL")}}</div>
            <div>Allow the terminal to run sensitive operations on this machine — provider-key spend,
              shell commands, and tool execution — for the next 8 hours?</div>
            <div class="row">
              <button class="ok" data-ctx-action="approve" type="button">⛨ AUTHORISE 8h &amp; CLOSE</button>
              <button class="no" data-ctx-action="reject"  type="button">✕ REFUSE &amp; CLOSE</button>
            </div>
            <div class="note">This decision is made locally on your node. The hosted server cannot grant
              it on your behalf. The grant expires automatically after 8 hours.</div>
          </div>
          {{CeremonyResolverScript(p.Id)}}
        </body></html>
        """;
    }

    // Wave 5 "/scope add" page: the human authorises ONE additional directory, outside the declared
    // Terminal projects, for ONE browser session — time-boxed to 8h. The path they are granting is the
    // most prominent thing on the page; approving mints a node-signed path grant, never a context grant.
    private static string RenderScopeCeremonyPage(PendingContextApproval p)
    {
        string E(string v) => System.Net.WebUtility.HtmlEncode(v);
        return $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Path Expansion</title>
        <style>
          body{background:#0a0806;color:#d8c89a;font-family:ui-monospace,Menlo,monospace;
               display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
          .card{border:1px solid #b09040;border-left:4px solid #d4a020;background:#160f04;
                padding:28px 32px;max-width:560px;box-shadow:0 0 32px rgba(180,140,20,.35)}
          h1{font-size:15px;letter-spacing:3px;color:#f0d060;margin:0 0 14px}
          .ctx{color:#a98;font-size:12px;margin-bottom:8px}
          .scope{color:#e8c060;font-size:12px;margin-bottom:12px;border:1px solid #6a5010;
                 background:#100a02;padding:8px 10px;letter-spacing:1px;word-break:break-all}
          .scope b{color:#f0d060}
          .sid{color:#a98;word-break:break-all}
          .window{color:#f0d060;font-size:13px;font-weight:bold;letter-spacing:2px;margin:6px 0 12px}
          .row{display:flex;gap:12px;margin-top:20px}
          button{flex:1;padding:12px;font-family:inherit;font-size:12px;letter-spacing:2px;cursor:pointer;border:none}
          .ok{background:#b09040;color:#160404;font-weight:bold}
          .no{background:#220;color:#c99;border:1px solid #533}
          .note{font-size:10px;color:#765;margin-top:16px;line-height:1.5}
        </style></head><body>
          <div class="card">
            <h1>⛨ AUTHORISE PATH EXPANSION</h1>
            <div class="ctx">Approval request from your terminal</div>
            <div class="scope">Path: <b>{{E(p.Path ?? "")}}</b></div>
            <div class="scope">Scope: <b>THIS BROWSER SESSION</b> · <span class="sid">{{E(p.SessionId ?? "")}}</span></div>
            <div class="window">⏱ VALID FOR 8 HOURS · THIS SESSION · THIS DIRECTORY ONLY</div>
            <div>Allow the terminal to read, write, and run commands under this directory — outside your
              declared Terminal projects — for the next 8 hours?</div>
            <div class="row">
              <button class="ok" data-ctx-action="approve" type="button">⛨ AUTHORISE PATH 8h &amp; CLOSE</button>
              <button class="no" data-ctx-action="reject"  type="button">✕ REFUSE &amp; CLOSE</button>
            </div>
            <div class="note">This decision is made locally on your node. The hosted server cannot grant
              it on your behalf, and it covers only this directory, only this session. The grant expires
              automatically after 8 hours.</div>
          </div>
          {{CeremonyResolverScript(p.Id)}}
        </body></html>
        """;
    }

    // Pre-authorisation page for an unattended run (a scheduled vigil, or a Hive collective launched to
    // run across drones). The human authorises ONE such run — so it names the task/objective and the exact
    // window the grant covers, and says plainly that no one need be present while it runs.
    private static string RenderUnattendedCeremonyPage(PendingContextApproval p)
    {
        string E(string v) => System.Net.WebUtility.HtmlEncode(v);
        var isHive = p.Kind == "hive";
        var task = string.IsNullOrWhiteSpace(p.TaskPreview) ? "(no task text)" : p.TaskPreview!;
        var slot = string.IsNullOrWhiteSpace(p.SlotLabel) ? (isHive ? "this collective" : "its scheduled slot") : p.SlotLabel!;
        var until = p.ExpiryUnixOverride is { } e
            ? DateTimeOffset.FromUnixTimeSeconds(e).UtcDateTime.ToString("ddd dd MMM · HH:mm 'UTC'")
            : "the run window end";
        var title   = isHive ? "PRE-AUTHORISE HIVE COLLECTIVE" : "PRE-AUTHORISE SCHEDULED VIGIL";
        var intro   = isHive
            ? "A collective will run its Overmind and drones <b>unattended</b> — some possibly on remote nodes. Authorise their sensitive operations now so the run can proceed without a human present."
            : "A vigil will run <b>unattended</b>. Authorise its sensitive operations now so it can act without a human present when its slot arrives.";
        var runLine = isHive ? "Collective:" : "⏱ Runs at:";
        var scopeWord = isHive ? "this collective run" : "this vigil";
        var confirm = isHive ? "⛨ PRE-AUTHORISE COLLECTIVE" : "⛨ PRE-AUTHORISE VIGIL";

        // A vigil is bound to one fixed slot, so its seal is a single confirm button. A Hive is launched
        // repeatedly, so the human picks: "this run only" (a one-shot pass, revoked the moment the run ends
        // so the next launch re-asks) or a time window (1–8h) that spares them re-approving every launch.
        // The clicked button's "hours" value ("once", or a number) is read at approve.
        var windowLine = isHive
            ? "<div class=\"win\">This seal covers <b>only this collective</b>. Approve just this run, or keep it valid for a while so back-to-back launches don't re-ask.</div>"
            : $"<div class=\"win\">Seal valid until: <b>{E(until)}</b> — only this window, only {scopeWord}.</div>";
        var actionRow = isHive
            ? """
              <div class="seal-choices">
                <button class="ok once" data-ctx-action="approve" data-ctx-hours="once" type="button">
                  ⛨ AUTHORISE — THIS RUN ONLY<span class="sub">one-shot · re-asks on the next launch</span>
                </button>
                <div class="dur-lbl">— or hold the seal open for —</div>
                <div class="dur-row">
                  <button class="ok dur" data-ctx-action="approve" data-ctx-hours="1" type="button">1h</button>
                  <button class="ok dur" data-ctx-action="approve" data-ctx-hours="2" type="button">2h</button>
                  <button class="ok dur" data-ctx-action="approve" data-ctx-hours="4" type="button">4h</button>
                  <button class="ok dur" data-ctx-action="approve" data-ctx-hours="8" type="button">8h</button>
                </div>
              </div>
              <div class="seal-refuse"><button class="no" data-ctx-action="reject" type="button">✕ REFUSE &amp; CLOSE</button></div>
              """
            : $$"""
              <button class="ok" data-ctx-action="approve" type="button">{{confirm}} &amp; CLOSE</button>
              <button class="no" data-ctx-action="reject"  type="button">✕ REFUSE &amp; CLOSE</button>
              """;
        return $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Pre-Authorisation</title>
        <style>
          body{background:#0a0806;color:#d8c89a;font-family:ui-monospace,Menlo,monospace;
               display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
          .card{border:1px solid #b09040;border-left:4px solid #d4a020;background:#160f04;
                padding:28px 32px;max-width:580px;box-shadow:0 0 32px rgba(180,140,20,.35)}
          h1{font-size:15px;letter-spacing:3px;color:#f0d060;margin:0 0 14px}
          .ctx{color:#a98;font-size:12px;margin-bottom:8px}
          .task{color:#e8c060;font-size:12px;margin:10px 0;border:1px solid #6a5010;background:#100a02;
                padding:10px 12px;white-space:pre-wrap;word-break:break-word;max-height:140px;overflow:auto}
          .win{color:#f0d060;font-size:12px;font-weight:bold;letter-spacing:1px;margin:6px 0 4px;
               border:1px solid #6a5010;background:#100a02;padding:8px 10px}
          .win b{color:#f5df80}
          .row{display:flex;gap:12px;margin-top:12px}
          .actions{display:flex;flex-direction:column;gap:10px;margin-top:20px}
          button{font-family:inherit;font-size:12px;letter-spacing:2px;cursor:pointer;border:none}
          .ok{background:#b09040;color:#160404;font-weight:bold}
          .no{background:#220;color:#c99;border:1px solid #533}
          .note{font-size:10px;color:#765;margin-top:16px;line-height:1.5}
          /* Hive: one-shot ("this run only") plus a row of duration passes. */
          .seal-choices{display:flex;flex-direction:column;gap:10px}
          .seal-choices .once{display:flex;flex-direction:column;align-items:center;gap:3px;
               padding:13px;width:100%;border-left:4px solid #6a5010}
          .seal-choices .once .sub{font-size:9px;font-weight:normal;letter-spacing:.5px;color:#5a3d08}
          .dur-lbl{text-align:center;font-size:9px;letter-spacing:2px;color:#8a7038;margin:2px 0}
          .dur-row{display:flex;gap:8px}
          .dur-row .dur{flex:1;padding:11px 0;background:#100a02;color:#f0d060;
               border:1px solid #6a5010;font-weight:bold}
          .dur-row .dur:hover{background:#1c1204;border-color:#b09040}
          .seal-refuse{margin-top:2px}
          .seal-refuse .no{width:100%;padding:11px}
        </style></head><body>
          <div class="card">
            <h1>⛨ {{title}}</h1>
            <div class="ctx">{{intro}}</div>
            <div class="task">{{E(task)}}</div>
            <div class="win">{{runLine}} <b>{{E(slot)}}</b></div>
            {{windowLine}}
            <div class="actions">
              {{actionRow}}
            </div>
            <div class="note">This decision is made locally on your node. The hosted server cannot grant it on
              your behalf, and this seal covers only {{scopeWord}} — nothing else.</div>
          </div>
          {{CeremonyResolverScript(p.Id)}}
        </body></html>
        """;
    }

    private static string NotFoundPage()
    {
        return """
        <!doctype html><html><head><meta charset="utf-8"><title>Context Approval</title>
        <style>body{background:#0a0806;color:#d8c89a;font-family:ui-monospace,Menlo,monospace;
        display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
        .m{border:1px solid #b09040;padding:24px 30px;letter-spacing:1px}</style></head>
        <body><div class="m">Context approval request not found or expired.</div></body></html>
        """;
    }

    public record ContextGrantDto(string ContextId, string GrantType, long ExpiryUnix, string? SignatureBase64);
    public record ContextApprovalRequestDto(
        string? sessionId, long? expiryUnix = null, string? kind = null,
        string? taskPreview = null, string? slotLabel = null, string? path = null);
    public record EnforcementDto(bool Enabled);
    public record RevokeSessionDto(string? Session);
    public record ScopeRevokeDto(string? SessionId, string? Path);

    /// <summary>
    /// Normalises a requested expansion path to the canonical absolute form the grant is minted for —
    /// same expansion rules as the built-in tools (~, stray quotes/whitespace), trailing separators
    /// trimmed. Returns null for empty or '|'-carrying paths: '|' is the grant-payload field separator,
    /// so a path containing it can never become a signed claim.
    /// </summary>
    internal static string? NormalizeScopePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var full = BuiltinTools.Expand(raw).TrimEnd('/', '\\');
            return full.Length == 0 || full.Contains('|') ? null : full;
        }
        catch { return null; }
    }

    private static string Done(string msg)
    {
        var e = System.Net.WebUtility.HtmlEncode(msg);
        return $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Context</title>
        <style>body{background:#0a0806;color:#d8c89a;font-family:ui-monospace,Menlo,monospace;
        display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
        .m{border:1px solid #b09040;padding:24px 30px;letter-spacing:1px}</style></head>
        <body><div class="m">{{e}}</div></body></html>
        """;
    }
}
