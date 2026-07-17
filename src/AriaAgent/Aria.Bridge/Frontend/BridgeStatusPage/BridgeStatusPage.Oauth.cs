namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
    internal const string PanelOauth = """
        <!-- ── OAUTH TAB ───────────────────────────────────────────────── -->
        <div id="panel-oauth" style="display:none">
          <div class="section-head"><div class="section-title">// OAuth Integrations</div></div>
          <div class="card">
            <div class="card-header">// App Credentials</div>
            <div class="card-body" style="display:flex;flex-direction:column;gap:16px">
              <div class="section-lead">
                The Azure AD / Google Cloud app registration itself is still done in their respective
                consoles (see the Aria setup docs) — this is only where the resulting tenant/client id/secret
                are entered so this bridge can use them. Values are encrypted at rest and never leave this node.
              </div>
              <div>
                <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
                  <span style="color:var(--text-bright)">Microsoft</span>
                  <span id="oauthcfg-microsoft-status" style="font-size:10px;color:var(--text-dead)">unknown</span>
                </div>
                <input id="oauthcfg-ms-tenant" placeholder="Tenant ID (e.g. consumers)"
                       style="width:100%;margin-bottom:8px">
                <input id="oauthcfg-ms-client" placeholder="Application (client) ID"
                       style="width:100%;margin-bottom:8px">
                <input id="oauthcfg-ms-secret" type="password" placeholder="Client secret (leave blank to keep the existing one)"
                       autocomplete="off" style="width:100%;margin-bottom:8px">
                <div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap">
                  <button class="btn primary" onclick="saveMsOAuthConfig()">▶ SAVE</button>
                  <button class="btn-danger" id="oauthcfg-ms-reset-btn" onclick="resetOAuthConfig('microsoft')" style="display:none;padding:5px 12px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.06em">RESET TO APPSETTINGS</button>
                </div>
              </div>
              <div style="border-top:1px solid var(--border-dim);padding-top:14px">
                <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
                  <span style="color:var(--text-bright)">Google</span>
                  <span id="oauthcfg-google-status" style="font-size:10px;color:var(--text-dead)">unknown</span>
                </div>
                <div style="font-size:11px;color:var(--text-dead);margin-bottom:8px">
                  Paste the whole OAuth client JSON downloaded from Google Cloud Console (Desktop app credential) — client id and secret are extracted from it automatically.
                </div>
                <textarea id="oauthcfg-google-json" rows="4" placeholder='{"installed":{"client_id":"...","client_secret":"...", ...}}'
                          style="width:100%;margin-bottom:8px;font-family:monospace;font-size:11px"></textarea>
                <div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap">
                  <button class="btn primary" onclick="saveGoogleOAuthConfig()">▶ SAVE</button>
                  <button class="btn-danger" id="oauthcfg-google-reset-btn" onclick="resetOAuthConfig('google')" style="display:none;padding:5px 12px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.06em">RESET TO APPSETTINGS</button>
                </div>
              </div>
              <div id="oauthcfg-msg" style="font-size:11px;color:var(--text-muted);min-height:16px"></div>
            </div>
          </div>
          <div class="card">
            <div class="card-header">// Integrations</div>
            <div class="card-body" style="display:flex;flex-direction:column;gap:16px">
              <div>
                <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
                  <span style="color:var(--text-bright)">Microsoft 365</span>
                  <span id="oauth-microsoft-status" style="font-size:10px;color:var(--text-dead)">unknown</span>
                </div>
                <div style="font-size:11px;color:var(--text-dead);margin-bottom:10px">
                  Outlook email and calendar access. The token is stored on this bridge only.
                </div>
                <div style="display:flex;gap:8px">
                  <button onclick="connectOAuth('microsoft')" style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">▶ CONNECT</button>
                  <button onclick="disconnectOAuth('microsoft')" class="btn-danger">▶ DISCONNECT</button>
                </div>
              </div>
              <div style="border-top:1px solid var(--border-dim);padding-top:14px">
                <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
                  <span style="color:var(--text-bright)">Google Workspace</span>
                  <span id="oauth-google-status" style="font-size:10px;color:var(--text-dead)">unknown</span>
                </div>
                <div style="font-size:11px;color:var(--text-dead);margin-bottom:10px">
                  Gmail and Google Calendar access. The token is stored on this bridge only.
                </div>
                <div style="display:flex;gap:8px">
                  <button onclick="connectOAuth('google')" style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">▶ CONNECT</button>
                  <button onclick="disconnectOAuth('google')" class="btn-danger">▶ DISCONNECT</button>
                </div>
              </div>
              <div id="oauth-msg" style="font-size:11px;color:var(--text-muted);min-height:16px"></div>
            </div>
          </div>
        </div>
    """;

    internal const string ScriptOauth = """
        // ── OAuth integrations ────────────────────────────────────────────────
        function connectOAuth(provider) {
          const popup = window.open(`/oauth/${provider}/connect`, 'aria_oauth', 'width=600,height=700');
          const timer = setInterval(() => {
            if (popup && popup.closed) {
              clearInterval(timer);
              refreshOAuth();
            }
          }, 500);
        }

        async function disconnectOAuth(provider) {
          const msg = document.getElementById('oauth-msg');
          msg.textContent = 'Disconnecting…';
          try {
            const r = await fetch(`/oauth/${provider}`, { method: 'DELETE' });
            if (!r.ok) { msg.textContent = 'Error: ' + await r.text(); return; }
            msg.textContent = '✓ Disconnected.';
            refreshOAuth();
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        async function refreshOAuth() {
          for (const provider of ['microsoft', 'google']) {
            const el = document.getElementById(`oauth-${provider}-status`);
            if (!el) continue;
            try {
              const r = await fetch(`/oauth/${provider}/status`);
              if (!r.ok) { el.textContent = 'error'; el.style.color = 'var(--text-dead)'; continue; }
              const d = await r.json();
              if (d.connected) {
                el.textContent = d.email ? `● ${d.email}` : '● connected';
                el.style.color = 'var(--success)';
              } else {
                el.textContent = '○ not connected';
                el.style.color = 'var(--text-dead)';
              }
            } catch {
              el.textContent = 'error';
              el.style.color = 'var(--text-dead)';
            }
          }
        }

        // ── OAuth app credentials (Microsoft tenant/client id/secret, Google client JSON) ──────
        async function refreshOAuthConfig() {
          try {
            const r = await fetch('/oauth-config');
            const d = await r.json();

            const msStatus = document.getElementById('oauthcfg-microsoft-status');
            msStatus.textContent = d.microsoft.hasSecret ? (d.microsoft.overridden ? '● bridge override' : '● from appsettings') : '○ not configured';
            msStatus.style.color = d.microsoft.hasSecret ? 'var(--success)' : 'var(--text-dead)';
            document.getElementById('oauthcfg-ms-tenant').value = d.microsoft.tenantId || '';
            document.getElementById('oauthcfg-ms-client').value = d.microsoft.clientId || '';
            document.getElementById('oauthcfg-ms-reset-btn').style.display = d.microsoft.overridden ? '' : 'none';

            const gStatus = document.getElementById('oauthcfg-google-status');
            gStatus.textContent = d.google.hasSecret ? (d.google.overridden ? '● bridge override' : '● from appsettings') : '○ not configured';
            gStatus.style.color = d.google.hasSecret ? 'var(--success)' : 'var(--text-dead)';
            document.getElementById('oauthcfg-google-reset-btn').style.display = d.google.overridden ? '' : 'none';
          } catch(e) {
            document.getElementById('oauthcfg-msg').textContent = 'Failed to load: ' + e.message;
          }
        }

        async function saveMsOAuthConfig() {
          const msg = document.getElementById('oauthcfg-msg');
          const tenantId = document.getElementById('oauthcfg-ms-tenant').value.trim();
          const clientId = document.getElementById('oauthcfg-ms-client').value.trim();
          const clientSecret = document.getElementById('oauthcfg-ms-secret').value.trim();
          if (!clientId) { msg.textContent = 'Application (client) ID is required.'; return; }
          try {
            const r = await fetch('/oauth-config/microsoft', {
              method: 'PUT', headers: {'Content-Type':'application/json'},
              body: JSON.stringify({ tenantId, clientId, clientSecret: clientSecret || null })
            });
            if (!r.ok) { msg.textContent = 'Failed to save: ' + (await r.text()); return; }
            document.getElementById('oauthcfg-ms-secret').value = '';
            msg.textContent = 'Microsoft credentials saved.';
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
          await refreshOAuthConfig();
        }

        async function saveGoogleOAuthConfig() {
          const msg = document.getElementById('oauthcfg-msg');
          const json = document.getElementById('oauthcfg-google-json').value.trim();
          if (!json) { msg.textContent = 'Paste the downloaded credentials JSON first.'; return; }
          try {
            const r = await fetch('/oauth-config/google', {
              method: 'PUT', headers: {'Content-Type':'application/json'},
              body: JSON.stringify({ credentialsJson: json })
            });
            if (!r.ok) { msg.textContent = 'Failed to save: ' + (await r.text()); return; }
            document.getElementById('oauthcfg-google-json').value = '';
            msg.textContent = 'Google credentials saved.';
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
          await refreshOAuthConfig();
        }

        async function resetOAuthConfig(provider) {
          const msg = document.getElementById('oauthcfg-msg');
          try {
            const r = await fetch('/oauth-config/' + encodeURIComponent(provider), { method: 'DELETE' });
            if (!r.ok) { msg.textContent = 'Failed to reset: ' + (await r.text()); return; }
            msg.textContent = provider + ' reverted to appsettings.json.';
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
          await refreshOAuthConfig();
        }

    """;
}
