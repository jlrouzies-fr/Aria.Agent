namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
    internal const string PanelChannels = """
        <!-- ── CHANNELS TAB ─────────────────────────────────────────── -->
        <div id="panel-channels" style="display:none">
          <div class="section-head"><div class="section-title">// Channels</div></div>
          <div class="card">
            <div class="card-header" style="display:flex;justify-content:space-between;align-items:center">
              <span>// Custom Providers</span>
              <span id="channels-status" style="font-size:11px;color:var(--text-dead);border-left:none">unknown</span>
            </div>
            <div class="card-body">
              <div class="section-lead">
                Point at any OpenAI-compatible endpoint you host — a local model (LM Studio, Ollama,
                llama.cpp) or a self-hosted gateway. The URL you set is the ONLY destination this
                channel may reach.
              </div>
              <div class="subsection-title">Configured Channels</div>
              <div id="channels-custom" style="font-size:12.5px;color:var(--text-muted);margin-bottom:22px"></div>
              <div>
                <div id="ch-form-label" class="subsection-title">Add New Channel</div>
                <input id="ch-name" placeholder="name (e.g. Local LLM - Mac)" style="width:100%;margin-bottom:8px">
                <input id="ch-url" placeholder="http://127.0.0.1:1234/v1" style="width:100%;margin-bottom:8px">
                <input id="ch-key" type="password" placeholder="API key (optional — leave blank to keep the existing key)" style="width:100%;margin-bottom:8px" autocomplete="off">
                <div class="section-lead" style="margin:0 0 8px">
                  Models are discovered automatically from the endpoint's <code>/models</code> API when you save —
                  no need to list them by hand.
                </div>
                <div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap">
                  <button class="btn primary" onclick="saveCustomChannel()" id="ch-save-btn">▶ SAVE PROVIDER</button>
                  <button class="btn ghost" onclick="testChannelUrl()">TEST CONNECTIVITY</button>
                  <button class="btn ghost" onclick="cancelChannelEdit()" id="ch-cancel-btn" style="display:none">CANCEL</button>
                  <span id="channels-msg" style="font-size:11.5px;color:var(--text-muted)"></span>
                </div>
              </div>
            </div>
          </div>
          <div class="card">
            <div class="card-header">// Cloud Providers</div>
            <div class="card-body">
              <div class="section-lead">
                Your keys never leave this node. Each provider's key is only ever sent to its fixed
                official endpoint — the server can neither read it nor redirect it. Enter a key to
                enable a provider; the web terminal shows only which providers have a key stored.
              </div>
              <div id="channels-public" style="font-size:12.5px;color:var(--text-muted)">Loading…</div>
            </div>
          </div>
        </div>

    """;

    internal const string ScriptChannels = """
        // ── Channels ──────────────────────────────────────────────────
        function esc(s) { return (s||'').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); }

        // Small fixed-position toast for one-off confirmations (channel/key saved, deleted, etc.) that
        // don't have a dedicated inline status element to write into.
        function ShowChannelNotice(text) {
          let el = document.getElementById('channel-notice-toast');
          if (!el) {
            el = document.createElement('div');
            el.id = 'channel-notice-toast';
            el.style.cssText = 'position:fixed;bottom:20px;right:20px;z-index:9999;' +
              'background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-bright);' +
              'padding:8px 14px;font-family:monospace;font-size:12px;box-shadow:0 2px 12px rgba(0,0,0,.4);' +
              'transition:opacity .25s ease;opacity:0';
            document.body.appendChild(el);
          }
          el.textContent = text;
          clearTimeout(el._hideTimer);
          requestAnimationFrame(() => { el.style.opacity = '1'; });
          el._hideTimer = setTimeout(() => { el.style.opacity = '0'; }, 3000);
        }

        async function refreshChannels() {
          const st = document.getElementById('channels-status');
          try {
            const r = await fetch('/channels');
            const d = await r.json();
            const list = d.channels || [];
            st.textContent = list.length + ' channel(s)';
            st.style.color = 'var(--text-muted)';

            const pub = list.filter(c => c.isPublic);
            const cust = list.filter(c => !c.isPublic);
            const btn = 'padding:5px 12px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.06em';

            document.getElementById('channels-public').innerHTML = pub.map(c => {
              const n = esc(c.name), nj = JSON.stringify(c.name);
              const state = c.hasKey
                ? '<span style="color:var(--success);font-size:11px">● key stored</span>'
                : '<span style="color:var(--text-dead);font-size:11px">○ no key</span>';
              return `<div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;padding:6px 0;border-bottom:1px solid var(--border-dim)">
                <span style="min-width:130px;color:var(--text-bright)">${n}</span>${state}
                <input id="key-${n}" type="password" placeholder="${c.hasKey ? 'replace key' : 'paste key'}" autocomplete="off"
                       style="flex:1;min-width:160px;background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:5px 8px;font-family:monospace;font-size:12px">
                <button onclick='saveProviderKey(${nj})' style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);${btn}">SAVE</button>
                ${c.hasKey ? `<button onclick='removeProviderKey(${nj})' class="btn-danger" style="${btn}">REMOVE</button>` : ''}
              </div>`;
            }).join('') || '<span style="color:var(--text-dead)">No public providers.</span>';

            document.getElementById('channels-custom').innerHTML = cust.map(c => {
              const n = esc(c.name), nj = JSON.stringify(c.name);
              const key = c.hasKey ? '<span style="color:var(--success);font-size:10px">key ✓</span>' : '';
              const uj = JSON.stringify(c.url);
              const modelCount = (c.models||[]).length;
              const modelLbl = modelCount > 0 ? `${modelCount} model(s)` :
                '<span style="color:var(--text-dead)">no models discovered yet</span>';
              const sid = 'chstatus-' + n.replace(/[^a-zA-Z0-9_-]/g, '_');
              return `<div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;padding:6px 0;border-bottom:1px solid var(--border-dim)">
                <span style="min-width:130px;color:var(--text-bright)">${n}</span>
                <span style="flex:1;min-width:160px;color:var(--text-muted);font-size:11px">${esc(c.url)} · ${modelLbl}</span>
                <div style="display:flex;align-items:center;gap:8px;margin-left:auto;flex-wrap:wrap">${key}
                  <button onclick='editCustomChannel(${nj},${uj})' style="background:transparent;border:1px solid var(--border-normal);color:var(--text-bright);${btn}">EDIT</button>
                  <button onclick='rediscoverChannelModels(${nj},${uj})' style="background:transparent;border:1px solid var(--border-normal);color:var(--text-bright);${btn}">⟳ MODELS</button>
                  <button onclick='testChannel(${nj},${uj})' style="background:transparent;border:1px solid var(--border-normal);color:var(--text-bright);${btn}">TEST</button>
                  <button onclick='deleteCustomChannel(${nj})' class="btn-danger" style="${btn}">DELETE</button>
                </div>
                <span id="${sid}" class="ch-status" style="font-size:11px;color:var(--text-muted);flex-basis:100%"></span>
              </div>`;
            }).join('') || '<span style="color:var(--text-dead)">No custom channels yet.</span>';
          } catch(e) {
            st.textContent = 'error'; st.style.color = 'var(--danger,#c05050)';
            document.getElementById('channels-public').textContent = 'Failed to load: ' + e.message;
          }
        }

        async function saveProviderKey(name) {
          const el = document.getElementById('key-' + name.replace(/[&<>"]/g,''));
          const key = el ? el.value.trim() : '';
          if (!key) return;
          try {
            const r = await fetch('/keys/' + encodeURIComponent(name), {
              method: 'PUT', headers: {'Content-Type':'application/json'}, body: JSON.stringify({ key })
            });
            if (!r.ok) {
              ShowChannelNotice('Failed to save key for ' + name + ': ' + (await r.text()));
              return;
            }
            if (el) el.value = '';
            ShowChannelNotice('Key saved for ' + name);
          } catch(e) { ShowChannelNotice('Error saving key for ' + name + ': ' + e.message); }
          await refreshChannels();
        }

        async function removeProviderKey(name) {
          try {
            const r = await fetch('/keys/' + encodeURIComponent(name), { method: 'DELETE' });
            if (!r.ok) { ShowChannelNotice('Failed to remove key for ' + name + ': ' + (await r.text())); return; }
            ShowChannelNotice('Key removed for ' + name);
          } catch(e) { ShowChannelNotice('Error removing key for ' + name + ': ' + e.message); }
          await refreshChannels();
        }

        let editingChannelName = null;

        function setChannelEditMode(name) {
          const nameInput = document.getElementById('ch-name');
          const cancelBtn = document.getElementById('ch-cancel-btn');
          const label = document.getElementById('ch-form-label');
          if (name) {
            editingChannelName = name;
            nameInput.disabled = true;
            nameInput.style.opacity = '0.6';
            cancelBtn.style.display = '';
            label.textContent = 'Editing Channel: ' + name;
          } else {
            editingChannelName = null;
            nameInput.disabled = false;
            nameInput.style.opacity = '';
            cancelBtn.style.display = 'none';
            label.textContent = 'Add New Channel';
          }
        }

        function editCustomChannel(name, url) {
          document.getElementById('ch-name').value = name;
          document.getElementById('ch-url').value = url;
          document.getElementById('ch-key').value = '';
          setChannelEditMode(name);
          document.getElementById('channels-msg').textContent = '';
        }

        function cancelChannelEdit() {
          document.getElementById('ch-name').value = '';
          document.getElementById('ch-url').value = '';
          document.getElementById('ch-key').value = '';
          document.getElementById('channels-msg').textContent = '';
          setChannelEditMode(null);
        }

        // Queries the endpoint's own /models API (via the bridge, so a stored key never reaches the
        // browser) and PUTs the discovered list back onto the channel. Best-effort: a channel with no
        // discoverable models still saves fine and can be re-tried later with the ⟳ MODELS button.
        async function discoverModelsForChannel(name, url) {
          try {
            const dr = await fetch('/llm/discover-models', {
              method: 'POST', headers: {'Content-Type':'application/json'},
              body: JSON.stringify({ url, keyRef: name })
            });
            const dd = await dr.json();
            if (!dr.ok || !dd.ok) return { ok: false, error: dd.error || 'discovery failed' };

            const pr = await fetch('/channels/' + encodeURIComponent(name), {
              method: 'PUT', headers: {'Content-Type':'application/json'},
              body: JSON.stringify({ url, models: dd.models, isBridged: true })
            });
            if (!pr.ok) return { ok: false, error: await pr.text() };
            return { ok: true, count: dd.models.length };
          } catch(e) { return { ok: false, error: e.message }; }
        }

        async function rediscoverChannelModels(name, url) {
          // The inline per-row status span gets wiped the instant refreshChannels() re-renders the row
          // below, so a message written there flashes and vanishes before it's readable. Use the fixed
          // toast instead — it lives outside the re-rendered subtree and survives the refresh.
          const sid = 'chstatus-' + esc(name).replace(/[^a-zA-Z0-9_-]/g, '_');
          const st = document.getElementById(sid);
          if (st) { st.textContent = 'Discovering models…'; st.style.color = 'var(--text-muted)'; }
          const result = await discoverModelsForChannel(name, url);
          await refreshChannels();
          ShowChannelNotice(result.ok
            ? `${name}: found ${result.count} model(s)`
            : `${name}: discovery failed — ${result.error}`);
        }

        async function saveCustomChannel() {
          const msg = document.getElementById('channels-msg');
          const name = document.getElementById('ch-name').value.trim();
          const url  = document.getElementById('ch-url').value.trim();
          const key  = document.getElementById('ch-key').value.trim();
          const targetName = editingChannelName || name;
          if (!targetName || !url) { msg.textContent = 'Name and URL are required.'; return; }

          // OpenAI-compatible servers (LM Studio, llama.cpp, Ollama) serve chat completions under /v1.
          // A base URL without it stores fine but returns a 200 "unexpected endpoint" help body at call
          // time instead of a reply — a silent-looking failure. Catch it at save and offer the fix.
          let finalUrl = url;
          if (!/\/v\d+$/.test(url.replace(/\/+$/, ''))) {
            const suggested = url.replace(/\/+$/, '') + '/v1';
            if (confirm(
                'This URL does not end in /v1.\n\n' +
                'Most OpenAI-compatible endpoints (LM Studio, llama.cpp, Ollama) serve chat completions ' +
                'under /v1 — without it, calls return a 200 help page instead of a reply.\n\n' +
                'Use "' + suggested + '" instead?')) {
              finalUrl = suggested;
              document.getElementById('ch-url').value = finalUrl;
            }
          }

          msg.textContent = 'Saving…';
          try {
            const r = await fetch('/channels/' + encodeURIComponent(targetName), {
              method: 'PUT', headers: {'Content-Type':'application/json'},
              body: JSON.stringify({ url: finalUrl, models: [], isBridged: true })
            });
            if (!r.ok) { msg.textContent = 'Error: ' + (await r.text()); return; }
            if (key) {
              const kr = await fetch('/keys/' + encodeURIComponent(targetName), {
                method: 'PUT', headers: {'Content-Type':'application/json'}, body: JSON.stringify({ key })
              });
              if (!kr.ok) {
                msg.textContent = 'Channel saved, but key failed: ' + (await kr.text());
                await refreshChannels();
                return;
              }
            }
            cancelChannelEdit();
            msg.textContent = 'Discovering models…';
            const discovered = await discoverModelsForChannel(targetName, finalUrl);
            ShowChannelNotice('Saved ' + targetName + (key ? ' (with key)' : '') +
              (discovered.ok ? ` — found ${discovered.count} model(s)` : ' — could not discover models, try ⟳ MODELS'));
            msg.textContent = '';
            await refreshChannels();
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        async function deleteCustomChannel(name) {
          try {
            await fetch('/channels/' + encodeURIComponent(name), { method: 'DELETE' });
            await fetch('/keys/' + encodeURIComponent(name), { method: 'DELETE' });
          } catch(e) { ShowChannelNotice('Error deleting ' + name + ': ' + e.message); }
          await refreshChannels();
        }

        // ── Web Search (Ollama) key ─────────────────────────────────────────────
        async function refreshWebSearchKey() {
          const st = document.getElementById('websearch-status');
          const removeBtn = document.getElementById('websearch-remove-btn');
          try {
            const r = await fetch('/keys');
            const d = await r.json();
            const hasKey = (d.providers || []).includes('OllamaWebSearch');
            st.textContent = hasKey ? '● key stored' : '○ no key';
            st.style.color = hasKey ? 'var(--success)' : 'var(--text-dead)';
            removeBtn.style.display = hasKey ? '' : 'none';
          } catch(e) {
            st.textContent = 'error'; st.style.color = 'var(--danger,#c05050)';
          }
        }

        async function saveWebSearchKey() {
          const msg = document.getElementById('websearch-msg');
          const el = document.getElementById('websearch-key');
          const key = el.value.trim();
          if (!key) { msg.textContent = 'Enter a key first.'; return; }
          try {
            const r = await fetch('/keys/OllamaWebSearch', {
              method: 'PUT', headers: {'Content-Type':'application/json'}, body: JSON.stringify({ key })
            });
            if (!r.ok) { msg.textContent = 'Failed to save key: ' + (await r.text()); return; }
            el.value = '';
            msg.textContent = 'Key saved.';
          } catch(e) { msg.textContent = 'Error saving key: ' + e.message; }
          await refreshWebSearchKey();
        }

        async function removeWebSearchKey() {
          const msg = document.getElementById('websearch-msg');
          try {
            const r = await fetch('/keys/OllamaWebSearch', { method: 'DELETE' });
            if (!r.ok) { msg.textContent = 'Failed to remove key: ' + (await r.text()); return; }
            msg.textContent = 'Key removed.';
          } catch(e) { msg.textContent = 'Error removing key: ' + e.message; }
          await refreshWebSearchKey();
        }

    """;
}
