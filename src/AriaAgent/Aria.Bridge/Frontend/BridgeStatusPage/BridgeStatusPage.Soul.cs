namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
    internal const string PanelSoul = """
        <!-- ── SOUL TAB ─────────────────────────────────────────────── -->
        <div id="panel-soul" style="display:none">
          <div class="section-head"><div class="section-title">// Soul</div></div>
          <div class="card">
            <div class="card-header">// Soul Identity</div>
            <div class="card-body">
              <div id="join-code-banner" style="display:none;margin-bottom:12px;padding:8px 12px;border:1px solid var(--border-glow);background:rgba(192,174,130,0.08);font-family:monospace;font-size:11px;letter-spacing:.06em;color:var(--text-normal)"></div>
              <div id="session-code-banner" style="display:none;margin-bottom:12px;padding:8px 12px;border:1px solid var(--border-glow);background:rgba(224,123,57,0.08);font-family:monospace;font-size:11px;letter-spacing:.06em;color:var(--text-normal)"></div>
              <div id="soul-section" style="font-size:12px;color:var(--text-muted)">Loading…</div>
            </div>
          </div>
          <div class="card">
            <div class="card-header" style="display:flex;justify-content:space-between;align-items:center">
              <span>// Soul Backup</span>
              <span id="export-status" style="font-size:10px;color:var(--text-dead)">ready</span>
            </div>
            <div class="card-body" style="display:flex;flex-direction:column;gap:14px">
              <div style="font-size:11px;color:var(--text-dead)">
                Export an encrypted backup of the soul master key. This requires an Inquisitorial
                Seal approved here on this node — the hosted server cannot trigger it. Store the blob
                somewhere safe; it is the only recovery path if this machine is lost.
              </div>
              <div id="export-seal-section">
                <button onclick="requestSoulExportSeal()" style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">▶ REQUEST BACKUP</button>
              </div>
              <div id="export-passphrase-section" style="display:none">
                <div style="font-size:11px;color:var(--success);margin-bottom:8px">✓ Seal granted. Choose a strong passphrase to encrypt the backup.</div>
                <input id="export-passphrase" type="password" placeholder="passphrase (min 8 chars)" autocomplete="off"
                       style="width:100%;max-width:280px;background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:6px 8px;font-family:monospace;font-size:12px;margin-bottom:8px">
                <div style="display:flex;gap:8px;flex-wrap:wrap">
                  <button onclick="performSoulExport()" style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">▶ EXPORT</button>
                  <button onclick="cancelSoulExport()" style="background:transparent;border:1px solid var(--border-dim);color:var(--text-dead);padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">CANCEL</button>
                </div>
              </div>
              <div id="export-result-section" style="display:none">
                <div style="font-size:11px;color:var(--text-dead);margin-bottom:6px">Encrypted backup blob:</div>
                <textarea id="export-blob" readonly style="width:100%;height:120px;background:var(--bg-base);border:1px solid var(--border-normal);color:var(--text-bright);padding:8px;font-family:monospace;font-size:11px;resize:vertical"></textarea>
                <div style="display:flex;gap:8px;margin-top:8px;flex-wrap:wrap">
                  <button onclick="copyExportBlob()" style="background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:5px 12px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">COPY</button>
                  <button onclick="downloadExportBlob()" style="background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:5px 12px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">DOWNLOAD</button>
                  <button onclick="resetSoulExport()" style="background:transparent;border:1px solid var(--border-dim);color:var(--text-dead);padding:5px 12px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">DONE</button>
                </div>
              </div>
              <div id="export-msg" style="font-size:11px;color:var(--text-muted);min-height:16px"></div>
            </div>
          </div>
        </div>

    """;

    internal const string ScriptSoul = """
        // ── Soul state ────────────────────────────────────────────────────────
        let soul = null;

        // True while the user is typing in / selecting text inside a section — don't rebuild it then,
        // or polling (every few seconds) would wipe what they're typing or clear their selection.
        function uiBusy(el) {
          if (!el) return false;
          const a = document.activeElement;
          if (a && el.contains(a) && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA')) return true;
          const sel = window.getSelection && window.getSelection();
          if (sel && !sel.isCollapsed && sel.anchorNode && el.contains(sel.anchorNode)) return true;
          return false;
        }
        let _lastSoulSig = null;
        let _lastLogSig = null;

        function renderSoul() {
          const el = document.getElementById('soul-section');
          if (!el) return;
          // Skip the rebuild if nothing changed or the user is mid-interaction (preserves input/selection).
          const sig = JSON.stringify(soul || null);
          if (el.dataset.r === '1' && (sig === _lastSoulSig || uiBusy(el))) return;
          _lastSoulSig = sig;
          el.dataset.r = '1';
          if (!soul || !soul.name) {
            // either no soul at all, or auto-created placeholder with empty name
            const isPlaceholder = soul && !soul.name;
            el.innerHTML = `
              <div class="metric-label" style="margin-bottom:10px">${isPlaceholder ? 'Soul record exists but has no name — choose one to activate local identity.' : 'No soul configured. Create a new one, or join an existing soul running on another machine.'}</div>
              <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
                <input id="soul-name" placeholder="Soul name…" style="background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:6px 10px;font-family:monospace;font-size:12px;flex:1;min-width:160px">
                <button onclick="createSoul()" style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">▶ ${isPlaceholder ? 'NAME SOUL' : 'CREATE SOUL'}</button>
              </div>
              <div id="soul-msg" style="margin-top:8px;font-size:11px;color:var(--text-muted)"></div>
              ${isPlaceholder ? '' : `
              <div style="margin-top:18px;padding-top:14px;border-top:1px solid var(--border-dim)">
                <div class="metric-label" style="margin-bottom:8px">Join an existing soul (this machine becomes an additional device):</div>
                <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
                  <input id="join-url" placeholder="Server URL (e.g. https://your-app.fly.dev)" style="background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:6px 10px;font-family:monospace;font-size:11px;flex:2;min-width:200px">
                  <input id="join-id" placeholder="Server Soul ID (from Aria.Web → Devices)" style="background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:6px 10px;font-family:monospace;font-size:11px;flex:1;min-width:260px">
                  <input id="join-label" placeholder="Label (e.g. Work PC)" style="background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:6px 10px;font-family:monospace;font-size:11px;flex:1;min-width:140px">
                  <button onclick="joinSoul()" style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">▶ JOIN</button>
                </div>
                <div style="margin-top:10px;padding:10px;border:1px solid var(--border-dim);background:var(--bg-surface);font-size:11px;color:var(--text-muted);line-height:1.6">
                  <strong style="color:var(--text-bright)">How to join from another machine:</strong>
                  <ol style="margin:6px 0 0 18px;padding:0">
                    <li>On your main computer, open Aria.Web in the browser and select the soul.</li>
                    <li>Open <strong>Devices</strong> in the sidebar and copy the <strong>Soul ID</strong> shown there.</li>
                    <li>On this machine, paste that ID above and click <strong>JOIN</strong>.</li>
                    <li>This bridge will appear as a pending device. Note the pairing code shown below.</li>
                    <li>Back on your main computer, in Aria.Web → Devices, enter the code and click <strong>APPROVE</strong>.</li>
                  </ol>
                </div>
                <div id="join-msg" style="margin-top:8px;font-size:11px;color:var(--text-muted)"></div>
              </div>`}`;
            return;
          }
          const linked = soul.serverSoulId != null;
          el.innerHTML = `
            <div style="display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-bottom:14px">
              <div class="metric"><div class="metric-label">Name</div><div class="metric-value" style="font-size:14px">${esc(soul.name)}</div></div>
              <div class="metric"><div class="metric-label">Keypair</div><div class="metric-value" style="font-size:13px;color:${soul.hasKeypair?'var(--success)':'var(--text-dead)'}">${soul.hasKeypair?'PRESENT':'MISSING'}</div></div>
              <div class="metric"><div class="metric-label">Created</div><div class="metric-value" style="font-size:11px">${new Date(soul.createdAt).toLocaleDateString()}</div></div>
              ${soul.hasKeypair ? `
              <div class="metric">
                <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:4px">
                  <div class="metric-label" style="margin-bottom:0">Public Key</div>
                  <button onclick="copyPubKey()" class="btn ghost sm" id="copy-key-btn">⧉ COPY</button>
                </div>
                <div style="font-size:10px;color:var(--text-dead);font-family:monospace;word-break:break-all;line-height:1.5" id="pubkey-display">fetching…</div>
              </div>` : ''}
            </div>
            <div style="margin-top:14px;padding-top:14px;border-top:1px solid var(--border-dim)">
              ${soul.serverLinks && soul.serverLinks.length > 0 ? `
              <div class="metric-label" style="margin-bottom:8px">Saved servers:</div>
              <div style="display:flex;flex-direction:column;gap:8px;margin-bottom:14px">
                ${soul.serverLinks.map(l => `
                <div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;padding:8px;border:1px solid var(--border-dim);background:var(--bg-surface)">
                  <div style="flex:1;min-width:180px">
                    <div style="font-size:12px;color:var(--text-bright);word-break:break-all">${esc(l.serverUrl)}</div>
                    <div style="font-size:10px;color:var(--text-dead)">soul ${esc(l.serverSoulId.slice(0,8))}… · ${new Date(l.createdAt).toLocaleDateString()}</div>
                  </div>
                  ${soul.serverSoulId === l.serverSoulId ? `
                    <span style="font-size:10px;color:var(--success);letter-spacing:.08em">● ACTIVE</span>
                  ` : `
                    <button onclick="switchServer('${l.serverSoulId}')" style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);padding:4px 12px;cursor:pointer;font-family:monospace;font-size:10px;letter-spacing:.08em">▶ SWITCH</button>
                  `}
                  <button onclick="removeServerLink('${l.id}')" style="background:var(--bg-surface);border:1px solid #8b0000;color:#c05050;padding:4px 10px;cursor:pointer;font-family:monospace;font-size:10px;letter-spacing:.08em">✕</button>
                </div>
                `).join('')}
              </div>` : ''}
              <div class="metric-label" style="margin-bottom:8px">Add / link another Aria.Web server:</div>
              <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
                <input id="server-url" value="http://localhost:5129" style="background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:6px 10px;font-family:monospace;font-size:11px;flex:1;min-width:200px">
                <button onclick="linkServer()" style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">▶ LINK SERVER</button>
                ${linked ? `<button class="btn-danger" onclick="unlinkServer()" style="background:var(--bg-surface);border:1px solid #8b0000;color:#c05050;padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">▶ DISCONNECT</button>` : ''}
              </div>
              <div id="soul-msg" style="margin-top:8px;font-size:11px;color:var(--text-muted)"></div>
            </div>
            <div style="margin-top:14px;padding-top:14px;border-top:1px solid var(--border-dim)">
              <div class="metric-label" style="margin-bottom:8px">Join an existing soul on another machine instead (wipes this bridge's local identity, then enrolls this machine as an additional device):</div>
              <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
                <input id="wj-url" placeholder="Server URL (e.g. https://your-app.fly.dev)" style="background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:6px 10px;font-family:monospace;font-size:11px;flex:2;min-width:200px">
                <input id="wj-id" placeholder="Server Soul ID (COPY GUID in Aria.Web)" style="background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:6px 10px;font-family:monospace;font-size:11px;flex:1;min-width:260px">
                <input id="wj-label" placeholder="Label (e.g. Work PC)" style="background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:6px 10px;font-family:monospace;font-size:11px;flex:1;min-width:140px">
                <button onclick="wipeAndJoin()" style="background:var(--bg-surface);border:1px solid #8b0000;color:#c05050;padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">▶ WIPE &amp; JOIN</button>
              </div>
              <div id="wj-msg" style="margin-top:8px;font-size:11px;color:var(--text-muted)"></div>
            </div>
            ${linked ? `
            <div style="margin-top:12px">
              <button class="btn-danger" onclick="rotateKey()" title="Generate a new keypair and re-register it with the server. Use if your private key was compromised.">▶ ROTATE KEYPAIR</button>
              <span id="rotate-msg" style="margin-left:10px;font-size:11px;color:var(--text-muted)"></span>
            </div>` : ''}`;

          // Populate public key display after render
          if (soul.hasKeypair) loadPubKey();
          updateHeaderServerUrl();
        }

        function updateHeaderServerUrl() {
          const el = document.getElementById('header-server-url');
          if (!el) return;
          if (soul && soul.serverSoulId && soul.serverUrl) {
            // Strip the scheme for a compact badge (e.g. "⬡ localhost:5129").
            const host = soul.serverUrl.replace(/^https?:\/\//, '').replace(/\/$/, '');
            el.textContent = '⬡ ' + host;
            el.setAttribute('data-tip', 'Linked to ' + soul.serverUrl + '  ·  soul ' + soul.serverSoulId);
            el.classList.add('linked');
          } else {
            el.textContent = '○ Not linked';
            el.setAttribute('data-tip', 'This node is not linked to any Aria.Web server');
            el.classList.remove('linked');
          }
        }

        async function loadPubKey() {
          try {
            const r = await fetch('/soul/pubkey');
            const d = await r.json();
            const el = document.getElementById('pubkey-display');
            if (el) el.textContent = d.publicKey ? d.publicKey.slice(0,40) + '…' : '—';
            window._pubKey = d.publicKey;
          } catch {}
        }

        async function copyPubKey() {
          try {
            await navigator.clipboard.writeText(window._pubKey || '');
            const btn = document.getElementById('copy-key-btn');
            if (btn) { btn.textContent = '✓ COPIED'; setTimeout(() => { btn.textContent = '⧉ COPY KEY'; }, 2000); }
          } catch(e) { alert('Copy failed: ' + e.message); }
        }

        function esc(s) { const d = document.createElement('div'); d.textContent = s||''; return d.innerHTML; }

        async function createSoul() {
          const name = document.getElementById('soul-name').value.trim();
          if (!name) return;
          const msgEl = document.getElementById('soul-msg');
          msgEl.textContent = 'Saving…';
          try {
            // If a placeholder soul exists, use PUT to update the name; otherwise POST to create.
            const method = (soul && !soul.name) ? 'PUT' : 'POST';
            const r = await fetch('/soul', { method, headers:{'Content-Type':'application/json'},
              body: JSON.stringify({ name, avatarSpriteKey: null, accentColor: 'theme-gold' }) });
            if (r.status === 409) { msgEl.textContent = 'Soul already exists.'; return; }
            if (!r.ok) { msgEl.textContent = 'Error: ' + (await r.json().catch(() => ({}))).detail || await r.text(); return; }
            soul = await r.json();
            renderSoul();
          } catch(e) { msgEl.textContent = 'Error: ' + e.message; }
        }

        async function linkServer() {
          const url = document.getElementById('server-url').value.trim();
          if (!url) return;
          const msg = document.getElementById('soul-msg');
          msg.textContent = 'Requesting seal…';
          try {
            await withSeal(
              'soul-link-server',
              'Register this cogitator node with a new Aria.Web server',
              'Server URL: ' + url,
              async (sealId) => {
                msg.textContent = 'Linking…';
                const r = await fetch('/soul/link-server', { method:'POST', headers:{'Content-Type':'application/json'},
                  body: JSON.stringify({ serverUrl: url, sealId }) });
                const d = await r.json();
                if (!r.ok) { msg.textContent = 'Error: ' + (d.detail || JSON.stringify(d)); return; }
                msg.textContent = '✓ Linked — Server Soul ID: ' + d.serverSoulId;
                await refreshSoul();
              }
            );
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        async function unlinkServer() {
          if (!await ariaConfirm('DISCONNECT FROM SERVER?\n\nThis clears the active server link but keeps the saved server list, local soul identity, cogitations, and keys.\n\nProceed?', false)) return;
          const msg = document.getElementById('soul-msg');
          msg.textContent = 'Requesting seal…';
          try {
            await withSeal(
              'soul-unlink',
              'Disconnect this cogitator node from the active Aria.Web server',
              'This clears the active server association and disconnects the tunnel.',
              async (sealId) => {
                msg.textContent = 'Disconnecting…';
                const r = await fetch('/soul/unlink', { method:'POST', headers:{'Content-Type':'application/json'},
                  body: JSON.stringify({ sealId }) });
                if (!r.ok) { msg.textContent = 'Error: ' + await r.text(); return; }
                msg.textContent = '✓ Disconnected';
                await refreshSoul();
              }
            );
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        async function switchServer(serverSoulId) {
          const msg = document.getElementById('soul-msg');
          msg.textContent = 'Requesting seal…';
          try {
            await withSeal(
              'soul-switch-server',
              'Switch this cogitator node to a previously saved Aria.Web server',
              'Server Soul ID: ' + serverSoulId,
              async (sealId) => {
                msg.textContent = 'Switching…';
                const r = await fetch('/soul/switch-server', { method:'POST', headers:{'Content-Type':'application/json'},
                  body: JSON.stringify({ serverSoulId, sealId }) });
                const d = await r.json();
                if (!r.ok) { msg.textContent = 'Error: ' + (d.detail || JSON.stringify(d)); return; }
                msg.textContent = '✓ Switched to ' + d.serverUrl;
                await refreshSoul();
              }
            );
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        async function removeServerLink(id) {
          if (!await ariaConfirm('REMOVE SAVED SERVER?\n\nThis deletes the saved server entry. If it is the active link, the bridge will disconnect.\n\nProceed?', false)) return;
          const msg = document.getElementById('soul-msg');
          msg.textContent = 'Removing…';
          try {
            const r = await fetch('/soul/server-link?id=' + encodeURIComponent(id), { method:'DELETE' });
            const d = await r.json();
            if (!r.ok) { msg.textContent = 'Error: ' + (d.detail || JSON.stringify(d)); return; }
            msg.textContent = '✓ Removed';
            await refreshSoul();
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        async function joinSoul() {
          const url = document.getElementById('join-url').value.trim();
          const id  = document.getElementById('join-id').value.trim();
          const label = document.getElementById('join-label').value.trim();
          const msg = document.getElementById('join-msg');
          if (!url || !id) { msg.textContent = 'Server URL and Soul ID are required.'; return; }
          msg.textContent = 'Joining…';
          try {
            const r = await fetch('/soul/join', { method:'POST', headers:{'Content-Type':'application/json'},
              body: JSON.stringify({ serverUrl: url, serverSoulId: id, label: label || null }) });
            const d = await r.json();
            if (!r.ok) { msg.textContent = 'Error: ' + (d.detail || JSON.stringify(d)); return; }
            msg.style.color = 'var(--success)';
            msg.textContent = '✓ Joined. Awaiting approval — watch for the pairing code below.';
            await refreshSoul();
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        // "Wipe & join" from the has-soul view: this bridge already holds a soul (e.g. leftover from a
        // previous install), but the user wants this machine to become an additional device of a soul
        // living elsewhere. /soul/join requires a fresh bridge, so wipe the local identity first, then
        // join in one flow — no restart needed (both endpoints reset the tunnel).
        async function wipeAndJoin() {
          const url   = document.getElementById('wj-url').value.trim();
          const id    = document.getElementById('wj-id').value.trim();
          const label = document.getElementById('wj-label').value.trim();
          const msg   = document.getElementById('wj-msg');
          if (!url || !id) { msg.textContent = 'Server URL and Soul ID are required.'; return; }
          if (!await ariaConfirm('WIPE THIS BRIDGE AND JOIN?\n\nThis permanently deletes the local soul identity, keys, chats, contacts and memories on THIS machine, then joins the soul above as a new device (pending approval from an existing device).\n\nProceed?', false)) return;
          msg.style.color = 'var(--text-muted)';
          msg.textContent = 'Wiping local identity…';
          try {
            const w = await fetch('/db/soul', { method:'DELETE' });
            if (!w.ok) { msg.textContent = 'Wipe failed: ' + await w.text(); return; }
            msg.textContent = 'Joining…';
            const r = await fetch('/soul/join', { method:'POST', headers:{'Content-Type':'application/json'},
              body: JSON.stringify({ serverUrl: url, serverSoulId: id, label: label || null }) });
            const d = await r.json();
            if (!r.ok) { msg.textContent = 'Error: ' + (d.detail || JSON.stringify(d)); return; }
            msg.style.color = 'var(--success)';
            msg.textContent = '✓ Joined. Awaiting approval — watch for the pairing code banner above.';
            await refreshSoul();
            await refreshJoinCode();
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        // Poll for the pairing code shown while this device awaits enrollment approval.
        async function refreshJoinCode() {
          const banner = document.getElementById('join-code-banner');
          if (!banner) return;
          try {
            const r = await fetch('/node/join-code');
            const d = r.ok ? await r.json() : null;
            if (d && d.display) {
              banner.style.display = 'block';
              banner.innerHTML = `⟁ AWAITING ENROLLMENT — approve this device in Aria.Web → Devices with code <strong style="color:var(--text-title);letter-spacing:.2em">${d.display}</strong>`;
            } else {
              banner.style.display = 'none';
            }
          } catch { banner.style.display = 'none'; }
        }

        // Show this bridge's browser-pairing session code (§12 fallback). A browser on another machine
        // that loads Aria.Web over plain HTTP can't auto-verify; the user reads this code here and pastes
        // it into Aria.Web → soul panel to unlock that browser. Loaded once (code is stable per process).
        let _sessionCode = null;
        async function loadSessionCode() {
          const banner = document.getElementById('session-code-banner');
          if (!banner) return;
          try {
            const r = await fetch('/node/session-code');
            const d = r.ok ? await r.json() : null;
            if (d && d.display) {
              _sessionCode = d.display;
              banner.style.display = 'flex';
              banner.style.alignItems = 'center';
              banner.style.gap = '8px';
              banner.innerHTML = `<span style="flex:1">⟁ BROWSER SESSION CODE — paste in Aria.Web to unlock a browser on this machine: <strong style="color:var(--text-title);letter-spacing:.2em">${d.display}</strong></span>` +
                `<button onclick="copySessionCode()" id="copy-session-btn" style="background:var(--bg-surface);border:1px solid var(--border-dim);color:var(--text-dead);padding:3px 10px;cursor:pointer;font-family:monospace;font-size:9px;letter-spacing:.08em;border-radius:2px;flex-shrink:0">⧉ COPY</button>`;
            }
          } catch { /* leave hidden */ }
        }

        async function copySessionCode() {
          try {
            await navigator.clipboard.writeText(_sessionCode || '');
            const btn = document.getElementById('copy-session-btn');
            if (btn) { btn.textContent = '✓ COPIED'; setTimeout(() => { btn.textContent = '⧉ COPY'; }, 2000); }
          } catch (e) { alert('Copy failed: ' + e.message); }
        }

        // ── Soul export ceremony ───────────────────────────────────────────────
        let _exportSealId = null;
        let _exportBlob = null;

        async function requestSoulExportSeal() {
          const msg = document.getElementById('export-msg');
          const status = document.getElementById('export-status');
          msg.textContent = '';
          status.textContent = 'awaiting seal…';
          try {
            const nonce = Array.from(crypto.getRandomValues(new Uint8Array(32)))
              .map(b => b.toString(16).padStart(2, '0')).join('');
            const r = await fetch('/seal/request', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({
                toolName: 'soul-export',
                reason: 'Back up the soul master key to an encrypted blob',
                argsPreview: 'This will export the soul private key encrypted with the passphrase you type below. Store the blob somewhere safe.',
                nonceBase64: btoa(nonce),
                openHere: true
              })
            });
            const d = await r.json();
            if (!r.ok) { throw new Error(d.error || JSON.stringify(d)); }
            _exportSealId = d.id;
            window.open('http://localhost:5741/seal/' + d.id, '_blank');
            msg.textContent = 'Approve the Inquisitorial Seal that opened in your browser…';
            await pollSoulExportSeal();
          } catch (e) {
            status.textContent = 'ready';
            msg.textContent = 'Error: ' + e.message;
          }
        }

        async function pollSoulExportSeal() {
          if (!_exportSealId) return;
          const msg = document.getElementById('export-msg');
          const status = document.getElementById('export-status');
          const maxAttempts = 60; // ~3 minutes at 3s interval
          for (let i = 0; i < maxAttempts; i++) {
            await new Promise(r => setTimeout(r, 3000));
            try {
              const r = await fetch('/seal/poll', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ id: _exportSealId })
              });
              const d = await r.json();
              if (d.status === 'approved') {
                document.getElementById('export-seal-section').style.display = 'none';
                document.getElementById('export-passphrase-section').style.display = 'block';
                status.textContent = 'seal granted';
                msg.textContent = '';
                return;
              }
              if (d.status === 'rejected') {
                status.textContent = 'ready';
                msg.textContent = 'Seal refused — export cancelled.';
                _exportSealId = null;
                return;
              }
              if (d.status === 'expired') {
                status.textContent = 'ready';
                msg.textContent = 'Seal expired — try again.';
                _exportSealId = null;
                return;
              }
            } catch (e) { /* keep polling */ }
          }
          status.textContent = 'ready';
          msg.textContent = 'Seal approval timed out.';
          _exportSealId = null;
        }

        async function performSoulExport() {
          const msg = document.getElementById('export-msg');
          const status = document.getElementById('export-status');
          const passphrase = document.getElementById('export-passphrase').value;
          if (!passphrase || passphrase.length < 8) {
            msg.textContent = 'Passphrase must be at least 8 characters.';
            return;
          }
          if (!_exportSealId) {
            msg.textContent = 'No approved seal. Request backup again.';
            return;
          }
          status.textContent = 'exporting…';
          try {
            const r = await fetch('/soul/export', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ sealId: _exportSealId, passphrase })
            });
            const d = await r.json();
            if (!r.ok) { throw new Error(d.error || JSON.stringify(d)); }
            _exportBlob = d.blob;
            document.getElementById('export-passphrase-section').style.display = 'none';
            document.getElementById('export-result-section').style.display = 'block';
            document.getElementById('export-blob').value = d.blob;
            document.getElementById('export-passphrase').value = '';
            status.textContent = 'ready';
            msg.textContent = '✓ Backup exported. Store the blob somewhere safe.';
          } catch (e) {
            status.textContent = 'ready';
            msg.textContent = 'Error: ' + e.message;
          }
        }

        function cancelSoulExport() {
          _exportSealId = null;
          document.getElementById('export-seal-section').style.display = 'block';
          document.getElementById('export-passphrase-section').style.display = 'none';
          document.getElementById('export-msg').textContent = 'Export cancelled.';
          document.getElementById('export-status').textContent = 'ready';
        }

        function resetSoulExport() {
          _exportSealId = null;
          _exportBlob = null;
          document.getElementById('export-seal-section').style.display = 'block';
          document.getElementById('export-passphrase-section').style.display = 'none';
          document.getElementById('export-result-section').style.display = 'none';
          document.getElementById('export-blob').value = '';
          document.getElementById('export-msg').textContent = '';
          document.getElementById('export-status').textContent = 'ready';
        }

        async function copyExportBlob() {
          try {
            await navigator.clipboard.writeText(_exportBlob || '');
            const msg = document.getElementById('export-msg');
            msg.textContent = '✓ Copied to clipboard.';
          } catch (e) { alert('Copy failed: ' + e.message); }
        }

        function downloadExportBlob() {
          const blob = _exportBlob || '';
          const a = document.createElement('a');
          a.href = 'data:text/plain;charset=utf-8,' + encodeURIComponent(blob);
          a.download = 'aria-soul-backup.txt';
          document.body.appendChild(a);
          a.click();
          document.body.removeChild(a);
        }

        // ── Generic seal helper for local-human-only actions (F-5) ────────────
        // Requests an Inquisitorial Seal, opens the approval page, polls for the
        // human verdict, and invokes onApproved(sealId) only after a fresh approval.
        async function withSeal(toolName, reason, argsPreview, onApproved) {
          const nonce = new Uint8Array(16);
          crypto.getRandomValues(nonce);
          const nonceB64 = btoa(String.fromCharCode(...nonce));

          const req = await fetch('/seal/request', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ toolName, reason, argsPreview, nonceBase64: nonceB64, openHere: true })
          });
          const d = await req.json();
          if (!req.ok) throw new Error(d.error || 'seal request failed');

          window.open('http://localhost:5741/seal/' + d.id, '_blank');

          const maxAttempts = 120; // ~3 minutes at 1.5s
          for (let i = 0; i < maxAttempts; i++) {
            await new Promise(res => setTimeout(res, 1500));
            const poll = await fetch('/seal/poll', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ id: d.id })
            });
            const pd = await poll.json();
            if (pd.status === 'approved') return await onApproved(d.id);
            if (pd.status === 'rejected') throw new Error('Seal refused.');
            if (pd.status === 'expired') throw new Error('Seal expired.');
          }
          throw new Error('Seal approval timed out.');
        }

        async function refreshSoul() {
          try {
            const r = await fetch('/soul');
            // 404 = no soul at all; otherwise parse (may have empty name)
            soul = r.ok ? await r.json() : (r.status === 404 ? null : soul);
            renderSoul();
            refreshOverview();
            refreshSecurity();
          } catch { soul = null; renderSoul(); }
        }

    """;
}
