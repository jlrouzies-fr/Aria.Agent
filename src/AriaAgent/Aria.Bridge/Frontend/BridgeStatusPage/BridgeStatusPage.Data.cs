namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
    internal const string PanelData = """
        <!-- ── DATA TAB ─────────────────────────────────────────────── -->
        <div id="panel-data" style="display:none">
          <div class="section-head"><div class="section-title">// Data</div></div>
          <div class="card">
            <div class="card-header">// Local SQLite Store</div>
            <div class="card-body">
              <div id="db-info-section" style="font-size:12px;color:var(--text-muted)">Loading…</div>
            </div>
          </div>
          <div class="card">
            <div class="card-header">// Noosphere Memory</div>
            <div class="card-body">
              <div id="noosphere-info-section" style="font-size:12px;color:var(--text-muted)">Loading…</div>
            </div>
          </div>
          <div class="card">
            <div class="card-header" style="color:#c05050">// Data Management</div>
            <div class="card-body" style="display:flex;flex-direction:column;gap:14px">
              <div>
                <div class="metric-label" style="margin-bottom:6px">Wipe all messages</div>
                <div style="color:var(--text-dead);font-size:11px;margin-bottom:8px">Removes all stored message content. Cogitation records are kept.</div>
                <button class="btn-danger" onclick="wipeMessages()">▶ WIPE MESSAGES</button>
              </div>
              <div style="border-top:1px solid var(--border-dim);padding-top:14px">
                <div class="metric-label" style="margin-bottom:6px">Wipe all cogitations</div>
                <div style="color:var(--text-dead);font-size:11px;margin-bottom:8px">Removes all cogitations and their messages. Soul identity is preserved.</div>
                <button class="btn-danger" onclick="wipeCogitations()">▶ WIPE COGITATIONS + MESSAGES</button>
              </div>
              <div style="border-top:1px solid var(--border-dim);padding-top:14px">
                <div class="metric-label" style="margin-bottom:6px">Wipe Noosphere memory</div>
                <div style="color:var(--text-dead);font-size:11px;margin-bottom:8px">Removes all engrams, entities, relations, anchors, and pending ingests. Soul identity and cogitations are preserved.</div>
                <button class="btn-danger" onclick="wipeNoosphere()">▶ WIPE NOOSPHERE</button>
              </div>
              <div style="border-top:1px solid var(--border-dim);padding-top:14px">
                <div class="metric-label" style="margin-bottom:6px;color:#c05050">Reset soul identity</div>
                <div style="color:var(--text-dead);font-size:11px;margin-bottom:8px">Deletes keypair, name, server link, all cogitations, and all stored LLM API keys. Use this to start completely fresh or before handing over the machine.</div>
                <button class="btn-danger" style="border-color:#8b0000;color:#c05050" onclick="wipeSoul()">▶ WIPE SOUL + ALL DATA</button>
              </div>
              <div id="wipe-msg" style="font-size:11px;color:var(--text-muted);min-height:16px"></div>
            </div>
          </div>
        </div>

    """;

    internal const string ScriptDataAndMemory = """
        // ── DB info ───────────────────────────────────────────────────────────
        async function refreshData() {
          const el = document.getElementById('db-info-section');
          try {
            const r = await fetch('/db-info');
            if (!r.ok) { el.textContent = 'Failed to load DB info.'; return; }
            const d = await r.json();
            el.innerHTML = `
              <div style="margin-bottom:14px">
                <div class="metric-label" style="margin-bottom:4px">Database File</div>
                <div style="font-size:11px;color:var(--text-normal);word-break:break-all;margin-bottom:2px">${esc(d.path)}</div>
                <div style="font-size:11px;color:var(--text-dead)">${fmtBytes(d.sizeBytes)}</div>
              </div>
              <div style="display:grid;grid-template-columns:1fr 1fr;gap:10px">
                <div class="metric"><div class="metric-label">Cogitations</div><div class="metric-value">${d.cogitations}</div></div>
                <div class="metric"><div class="metric-label">Messages</div><div class="metric-value">${d.messages}</div></div>
              </div>`;
          } catch(e) { el.textContent = 'Error: ' + e.message; }
        }

        // ── Noosphere info ──────────────────────────────────────────────────────
        async function refreshNoosphere() {
          const el = document.getElementById('noosphere-info-section');
          try {
            const r = await fetch('/memory/stats');
            if (!r.ok) { el.textContent = 'Failed to load Noosphere stats.'; return; }
            const d = await r.json();
            el.innerHTML = `
              <div style="display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-bottom:14px">
                <div class="metric"><div class="metric-label">Engrams</div><div class="metric-value">${d.engrams}</div></div>
                <div class="metric"><div class="metric-label">Entities</div><div class="metric-value">${d.entities}</div></div>
                <div class="metric"><div class="metric-label">Relations</div><div class="metric-value">${d.links}</div></div>
                <div class="metric"><div class="metric-label">Pending</div><div class="metric-value">${d.pendingIngests}</div></div>
              </div>
              <div style="font-size:11px;color:var(--text-dead);margin-bottom:${d.rawIngests > 0 ? '10px' : '0'}">
                Extraction: ${d.extractionConfigured ? '<span style="color:var(--text-normal)">configured</span>' : 'not configured'} ·
                Embeddings: ${d.embeddingsConfigured ? `<span style="color:var(--text-normal)">configured (${d.embeddedCount}/${d.engrams} embedded)</span>` : 'not configured — keyword/graph probe only'}
              </div>
              ${d.rawIngests > 0 ? `
              <div style="border-top:1px solid var(--border-dim);padding-top:10px;display:flex;align-items:center;gap:10px;flex-wrap:wrap">
                <span style="font-size:11px;color:#c09050">${d.rawIngests} engram(s) stored unstructured (raw) — no entities/relations extracted, likely from before a config/model fix.</span>
                <button onclick="reprocessRawIngests()" style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);padding:5px 12px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.06em">▶ REPROCESS RAW</button>
              </div>` : ''}`;
          } catch(e) { el.textContent = 'Error: ' + e.message; }
        }

        async function reprocessRawIngests() {
          try {
            const r = await fetch('/memory/reprocess-raw', { method: 'POST' });
            if (!r.ok) { ShowChannelNotice('Reprocess failed: ' + (await r.text())); return; }
            const d = await r.json();
            ShowChannelNotice('Requeued ' + d.requeued + ' raw ingest(s) for re-extraction.');
          } catch(e) { ShowChannelNotice('Reprocess error: ' + e.message); }
          await refreshNoosphere();
        }

        // ── Custom confirm modal ───────────────────────────────────────────────
        let _ariaResolver = null;
        function ariaConfirm(message, isDangerous = false) {
          return new Promise(resolve => {
            _ariaResolver = resolve;
            document.getElementById('aria-confirm-msg').textContent = message;
            document.getElementById('aria-confirm-ok').classList.toggle('danger', isDangerous);
            document.getElementById('aria-confirm-overlay').classList.add('active');
          });
        }
        function _ariaClose(result) {
          document.getElementById('aria-confirm-overlay').classList.remove('active');
          if (_ariaResolver) { const r = _ariaResolver; _ariaResolver = null; r(result); }
        }
        document.getElementById('aria-confirm-cancel').addEventListener('click', () => _ariaClose(false));
        document.getElementById('aria-confirm-ok').addEventListener('click', () => _ariaClose(true));
        document.getElementById('aria-confirm-overlay').addEventListener('click', function(e) {
          if (e.target === this) _ariaClose(false);
        });

        async function wipeCogitations() {
          if (!await ariaConfirm('Wipe ALL cogitations and their messages from the local bridge store?\n\nSoul identity is preserved. This cannot be undone.', true)) return;
          const msg = document.getElementById('wipe-msg');
          msg.textContent = 'Wiping…';
          try {
            const r = await fetch('/db/cogitations', { method: 'DELETE' });
            if (!r.ok) { msg.textContent = 'Error: ' + await r.text(); return; }
            msg.style.color = 'var(--success)';
            msg.textContent = '✓ Cogitations wiped.';
            await refreshData();
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        // Channels fetched by the last refreshMemoryConfig() call — the model dropdowns filter this by
        // the currently-selected channel name, so it needs to survive between renders.
        let _noosphereChannels = [];

        // Name-based heuristic: no provider we talk to (OpenAI-compatible /v1/models, Ollama /api/tags)
        // reliably tags a model as "embedding" vs "chat" in its listing, so this is the only signal
        // available across providers. Covers the common embedding-model naming families.
        function isEmbeddingModelName(id) {
          const s = (id || '').toLowerCase();
          return /(^|[-_./])embed(ding)?s?([-_./]|$)/.test(s) ||
                 /bge-|gte-|e5-|nomic-embed|arctic-embed|instructor-xl|uae-large|multilingual-e5|m3e-/.test(s);
        }

        function modelsForChannel(name) {
          const ch = _noosphereChannels.find(c => c.name === name);
          return (ch && ch.models) || [];
        }

        // True when a specific (non-Auto) model was saved for a slot but no longer appears on that
        // channel's current model list — e.g. it was unloaded/renamed/removed on the local server since
        // it was picked. Drives both the "!" badge on the Memory nav item and the inline panel warning.
        function isModelMissingOnChannel(channels, model, channelName) {
          if (!model) return false;
          const ch = channels.find(c => c.name === channelName);
          return !ch || !(ch.models || []).includes(model);
        }

        async function memoryModelMissing() {
          try {
            const r = await fetch('/memory/config');
            if (!r.ok) return false;
            const d = await r.json();
            const channels = d.channels || [];
            return isModelMissingOnChannel(channels, d.extractionModel, d.extractionChannelName) ||
                   isModelMissingOnChannel(channels, d.embeddingsModel, d.embeddingsChannelName);
          } catch { return false; }
        }

        function setModelMissingWarning(kind, model, channelName) {
          const el = document.getElementById('noosphere-' + kind + '-model-warn');
          if (!el) return;
          const missing = isModelMissingOnChannel(_noosphereChannels, model, channelName);
          el.style.display = missing ? '' : 'none';
          if (missing) el.textContent = `⚠ "${model}" is no longer on this channel — pick another model or Auto.`;
        }

        // Builds the model picker for a channel's Extraction/Embeddings slot. Embeddings is restricted
        // to models that look embedding-shaped; Extraction excludes them (a chat call to an
        // embedding-only model just errors). Falls back to the full list when the heuristic finds
        // nothing in that direction, rather than leaving the user with an empty dropdown.
        function buildNoosphereModelDropdown(kind, channelName, selectedModel) {
          const all = modelsForChannel(channelName);
          const filtered = kind === 'embeddings'
            ? all.filter(isEmbeddingModelName)
            : all.filter(m => !isEmbeddingModelName(m));
          const list = filtered.length > 0 ? filtered : all;
          const autoLabel = filtered.length > 0
            ? 'Auto — first ' + (kind === 'embeddings' ? 'embedding' : 'chat') + ' model'
            : 'Auto — first listed model';
          populateCustomSelect('noosphere-' + kind + '-model-wrapper',
            [{ value: '', label: autoLabel }].concat(list.map(m => ({ value: m, label: m }))),
            selectedModel || '');
        }

        async function refreshMemoryConfig() {
          const enabledBox = document.getElementById('noosphere-embeddings-enabled');
          const msg = document.getElementById('noosphere-config-msg');
          try {
            const r = await fetch('/memory/config');
            if (!r.ok) throw new Error('Failed to load config');
            const d = await r.json();
            _noosphereChannels = d.channels || [];

            buildNoosphereDropdown('extraction', _noosphereChannels, d.extractionChannelName);
            buildNoosphereDropdown('embeddings', _noosphereChannels, d.embeddingsChannelName);
            buildNoosphereModelDropdown('extraction', d.extractionChannelName, d.extractionModel);
            buildNoosphereModelDropdown('embeddings', d.embeddingsChannelName, d.embeddingsModel);
            setModelMissingWarning('extraction', d.extractionModel, d.extractionChannelName);
            setModelMissingWarning('embeddings', d.embeddingsModel, d.embeddingsChannelName);
            setNavBadge('memory',
              isModelMissingOnChannel(_noosphereChannels, d.extractionModel, d.extractionChannelName) ||
              isModelMissingOnChannel(_noosphereChannels, d.embeddingsModel, d.embeddingsChannelName));

            enabledBox.checked = !!d.embeddingsEnabled;
            msg.textContent = '';
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        function populateCustomSelect(wrapperId, items, selectedValue, onChange) {
          const wrapper = document.getElementById(wrapperId);
          const trigger = wrapper.querySelector('.custom-select-trigger');
          const list = wrapper.querySelector('.custom-select-options');
          const hidden = wrapper.querySelector('input[type="hidden"]');
          selectedValue = selectedValue == null ? '' : String(selectedValue);
          const selectedItem = items.find(i => String(i.value) === selectedValue) || items[0];
          hidden.value = selectedItem.value;
          trigger.textContent = selectedItem.label;

          list.innerHTML = '';
          items.forEach(i => {
            const div = document.createElement('div');
            div.className = 'custom-select-option' + (String(i.value) === selectedValue ? ' selected' : '');
            div.textContent = i.label;
            div.dataset.value = i.value;
            div.addEventListener('click', function(e) {
              e.stopPropagation();
              selectCustomOption(wrapperId, i.value, i.label, onChange);
            });
            list.appendChild(div);
          });
        }

        function setCustomSelectValue(wrapperId, value) {
          const wrapper = document.getElementById(wrapperId);
          const list = wrapper.querySelector('.custom-select-options');
          const hidden = wrapper.querySelector('input[type="hidden"]');
          const trigger = wrapper.querySelector('.custom-select-trigger');
          value = value == null ? '' : String(value);
          const option = list.querySelector('.custom-select-option[data-value="' + value + '"]')
                      || list.querySelector('.custom-select-option');
          if (!option) return;
          hidden.value = option.dataset.value;
          trigger.textContent = option.textContent;
          list.querySelectorAll('.custom-select-option').forEach(el => {
            el.classList.toggle('selected', el.dataset.value === hidden.value);
          });
        }

        // The currently-open custom dropdown, so scroll/resize can REPOSITION it (follow its trigger)
        // instead of closing it. Closing on scroll made the list vanish the instant you tried to
        // scroll it — and a fixed list positioned once could end up over a neighbouring control.
        let _openCustomSelect = null;

        function positionCustomDropdown(trigger, list) {
          const rect = trigger.getBoundingClientRect();
          const viewportHeight = window.innerHeight;
          const spaceBelow = viewportHeight - rect.bottom;
          const spaceAbove = rect.top;
          // Only open upward when there genuinely isn't room below AND there's more room above —
          // otherwise the list would jump up over the dropdowns stacked above it.
          const openAbove = spaceBelow < 160 && spaceAbove > spaceBelow;
          const desiredHeight = Math.min(240, Math.max(80, (openAbove ? spaceAbove : spaceBelow) - 8));

          // Anchor the EDGE nearest the trigger: opening down pins the list top just under the
          // trigger; opening up pins the list BOTTOM just above it (via `bottom`). Pinning the top
          // with a fixed desiredHeight offset — as before — left a big gap under a short list when
          // opening upward, so it appeared to float over a neighbouring dropdown.
          list.style.left = rect.left + 'px';
          list.style.width = rect.width + 'px';
          list.style.maxHeight = desiredHeight + 'px';
          if (openAbove) {
            list.style.top = 'auto';
            list.style.bottom = (window.innerHeight - rect.top + 4) + 'px';
          } else {
            list.style.bottom = 'auto';
            list.style.top = (rect.bottom + 4) + 'px';
          }
        }

        function toggleCustomDropdown(trigger) {
          const wrapper = trigger.closest('.custom-select');
          const list = wrapper.querySelector('.custom-select-options');
          const isOpen = list.style.display !== 'none';
          document.querySelectorAll('.custom-select-options').forEach(el => el.style.display = 'none');
          if (isOpen) { _openCustomSelect = null; return; }

          positionCustomDropdown(trigger, list);
          list.style.display = 'block';
          _openCustomSelect = { trigger, list };
        }

        // Keep the open list glued to its trigger while the user scrolls the page/panel (capture:true
        // so scrolls of any ancestor reach us). Scrolling the list's own options just repositions it
        // to the same spot — harmless — so internal scrolling no longer closes it.
        function repositionOpenCustomSelect() {
          if (_openCustomSelect && _openCustomSelect.list.style.display !== 'none') {
            positionCustomDropdown(_openCustomSelect.trigger, _openCustomSelect.list);
          } else {
            _openCustomSelect = null;
          }
        }

        function selectCustomOption(wrapperId, value, label, onChange) {
          const wrapper = document.getElementById(wrapperId);
          const trigger = wrapper.querySelector('.custom-select-trigger');
          const list = wrapper.querySelector('.custom-select-options');
          const hidden = wrapper.querySelector('input[type="hidden"]');
          value = value == null ? '' : String(value);
          hidden.value = value;
          trigger.textContent = label;
          list.querySelectorAll('.custom-select-option').forEach(el => {
            el.classList.toggle('selected', el.dataset.value === value);
          });
          list.style.display = 'none';
          _openCustomSelect = null;
          if (onChange) onChange(value);
        }

        function buildNoosphereDropdown(kind, channels, selectedName) {
          populateCustomSelect('noosphere-' + kind + '-wrapper',
            [{ value: '', label: 'Auto — first local channel' }].concat(
              channels.map(c => ({ value: c.name, label: (c.kind === 'public' ? c.name + ' (cloud)' : c.name) }))
            ),
            selectedName || '',
            // Switching channels changes the available model list — rebuild the model dropdown for
            // this slot and reset it to Auto rather than carrying over a model name from the old channel.
            (newChannelName) => buildNoosphereModelDropdown(kind, newChannelName, ''));
        }

        // Close custom dropdowns only when clicking OUTSIDE them. Scroll/resize reposition (never
        // close) so the user can freely scroll the page — or the option list itself — while it's open.
        document.addEventListener('click', function(e) {
          if (!e.target.closest('.custom-select')) {
            document.querySelectorAll('.custom-select-options').forEach(el => el.style.display = 'none');
            _openCustomSelect = null;
          }
        });
        window.addEventListener('scroll', repositionOpenCustomSelect, true);
        window.addEventListener('resize', repositionOpenCustomSelect);


        async function saveNoosphereConfig() {
          const extractionInput = document.getElementById('noosphere-extraction');
          const embeddingsInput = document.getElementById('noosphere-embeddings');
          const enabledBox = document.getElementById('noosphere-embeddings-enabled');
          const modelInput = document.getElementById('noosphere-embeddings-model');
          const extractionModelInput = document.getElementById('noosphere-extraction-model');
          const msg = document.getElementById('noosphere-config-msg');
          msg.textContent = 'Saving…';
          try {
            const r = await fetch('/memory/config', {
              method: 'PUT', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({
                extractionChannelName: extractionInput.value || null,
                embeddingsChannelName: embeddingsInput.value || null,
                embeddingsEnabled: enabledBox.checked,
                embeddingsModel: modelInput.value.trim() || null,
                extractionModel: extractionModelInput.value.trim() || null
              })
            });
            if (!r.ok) throw new Error(await r.text());
            msg.style.color = 'var(--success)';
            msg.textContent = '✓ Memory channel configuration saved.';
            await refreshMemoryConfig();
          } catch(e) { msg.style.color = 'var(--text-muted)'; msg.textContent = 'Error: ' + e.message; }
        }

        async function wipeNoosphere() {
          if (!await ariaConfirm('Wipe ALL Noosphere memory (engrams, entities, relations, anchors)?\n\nSoul identity and cogitations are preserved. This cannot be undone.', true)) return;
          const msg = document.getElementById('wipe-msg');
          msg.textContent = 'Wiping…';
          try {
            const r = await fetch('/db/noosphere', { method: 'DELETE' });
            if (!r.ok) { msg.textContent = 'Error: ' + await r.text(); return; }
            msg.style.color = 'var(--success)';
            msg.textContent = '✓ Noosphere memory wiped.';
            await refreshNoosphere();
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        async function rotateKey() {
          if (!await ariaConfirm('ROTATE KEYPAIR?\n\nThis generates a new private key and re-registers it with the server. All enrolled node keys on the server will be revoked.\n\nUse this if your private key was compromised. Your soul identity (ID, name, cogitations) is preserved.\n\nProceed?', false)) return;
          const msg = document.getElementById('rotate-msg');
          if (msg) { msg.style.color = 'var(--text-muted)'; msg.textContent = 'Requesting seal…'; }
          try {
            await withSeal(
              'soul-rotate-key',
              'Rotate the soul master keypair on this cogitator node',
              'This generates a new private key and revokes all enrolled node keys on the server.',
              async (sealId) => {
                if (msg) { msg.style.color = 'var(--text-muted)'; msg.textContent = 'Rotating…'; }
                const r = await fetch('/soul/rotate-key', { method: 'POST', headers:{'Content-Type':'application/json'},
                  body: JSON.stringify({ sealId }) });
                const d = await r.json();
                if (!r.ok) { if (msg) { msg.style.color = '#c05050'; msg.textContent = 'Error: ' + (d.detail || JSON.stringify(d)); } return; }
                if (msg) { msg.style.color = 'var(--success)'; msg.textContent = '✓ ' + d.note; }
                await refreshSoul();
              }
            );
          } catch(e) { if (msg) { msg.style.color = '#c05050'; msg.textContent = 'Error: ' + e.message; } }
        }

        async function wipeSoul() {
          if (!await ariaConfirm('RESET SOUL IDENTITY?\n\nThis will delete your keypair, name, server link, ALL cogitations, and ALL stored LLM API keys. You will need to create a new soul, re-register with the server, and re-enter any cloud API keys.\n\nThis cannot be undone.', true)) return;
          const msg = document.getElementById('wipe-msg');
          msg.style.color = 'var(--text-muted)';
          msg.textContent = 'Wiping…';
          try {
            const r = await fetch('/db/soul', { method: 'DELETE' });
            if (!r.ok) { msg.textContent = 'Error: ' + await r.text(); return; }
            msg.style.color = 'var(--success)';
            msg.textContent = '✓ Soul wiped. Reload the page to create a new one.';
            await refreshData();
            setTimeout(() => location.reload(), 1500);
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        async function wipeMessages() {
          if (!await ariaConfirm('Wipe ALL messages from the local bridge store?\n\nCogitation records are kept.', true)) return;
          const msg = document.getElementById('wipe-msg');
          msg.textContent = 'Wiping…';
          try {
            const r = await fetch('/db/messages', { method: 'DELETE' });
            if (!r.ok) { msg.textContent = 'Error: ' + await r.text(); return; }
            msg.style.color = 'var(--success)';
            msg.textContent = '✓ Messages wiped.';
            await refreshData();
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

    """;
}
