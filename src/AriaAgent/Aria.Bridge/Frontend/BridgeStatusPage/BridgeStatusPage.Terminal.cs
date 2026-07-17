namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
    internal const string PanelTerminal = """
        <!-- ── TERMINAL TAB ─────────────────────────────────────────── -->
        <div id="panel-terminal" style="display:none">
          <div class="section-head"><div class="section-title">// Terminal / Projects</div></div>
          <div class="info-callout">
            Three independent capabilities. Each is off until a human at this node opts in — the web UI
            can never turn them on for you. All are confined to the Allowed Projects declared below.
          </div>
          <div class="card">
            <div class="card-header" style="display:flex;justify-content:space-between;align-items:center">
              <span>// Agent Projects</span>
              <span id="cap-projects-status" style="font-size:11px;color:var(--text-dead)">unknown</span>
            </div>
            <div class="card-body">
              <div class="section-lead">
                Lets the <strong>agent</strong> work inside your declared projects — read / write / search
                files, run git, and execute shell commands — all confined to the Allowed Projects below.
              </div>
              <div class="cap-row">
                <span id="cap-projects-light" class="cap-light off">● DISABLED</span>
                <button class="btn primary sm" id="cap-projects-btn" onclick="toggleCap('projects')">▶ ENABLE</button>
              </div>
            </div>
          </div>
          <div class="card">
            <div class="card-header" style="display:flex;justify-content:space-between;align-items:center">
              <span>// Terminal · Quick Exec</span>
              <span id="cap-quick-status" style="font-size:11px;color:var(--text-dead)">unknown</span>
            </div>
            <div class="card-body">
              <div class="section-lead">
                The <strong>user</strong> web Terminal's one-shot mode — you type a command, it runs once
                and returns its output. Scoped to the Allowed Projects below.
              </div>
              <div class="cap-row">
                <span id="cap-quick-light" class="cap-light off">● DISABLED</span>
                <button class="btn primary sm" id="cap-quick-btn" onclick="toggleCap('quick')">▶ ENABLE</button>
              </div>
            </div>
          </div>
          <div class="card">
            <div class="card-header" style="display:flex;justify-content:space-between;align-items:center">
              <span>// Allowed Projects &amp; Policy</span>
              <span id="terminal-config-status" style="font-size:11px;color:var(--text-dead)">unknown</span>
            </div>
            <div class="card-body">
              <div class="section-lead">
                The projects all three capabilities are confined to. Each has a name, a path, and an
                optional description; the path is the only directory reachable. The web displays these
                read-only — it can neither add nor widen them. No projects means all access is blocked.
              </div>
              <div style="margin-bottom:12px">
                <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:6px">
                  <div class="field-label" style="margin-bottom:0">Allowed Projects</div>
                  <button class="btn ghost sm" onclick="addProjectRow()">+ ADD PROJECT</button>
                </div>
                <div id="terminal-projects"></div>
              </div>
              <div style="margin-bottom:12px">
                <div style="font-size:10px;color:var(--dim);margin-bottom:4px;letter-spacing:1px;text-transform:uppercase">Blocked Patterns (one per line)</div>
                <textarea id="terminal-blocked-commands" rows="4"
                          style="width:100%;background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:8px;font-family:monospace;font-size:12px;resize:vertical"
                          placeholder="npm publish&#10;git push --force"></textarea>
              </div>
              <div style="display:flex;align-items:center;gap:10px">
                <button onclick="saveTerminalConfig()" style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">▶ SAVE POLICY</button>
                <span id="terminal-config-msg" style="font-size:11px;color:var(--text-muted)"></span>
              </div>
            </div>
          </div>
          <div class="card">
            <div class="card-header" style="display:flex;justify-content:space-between;align-items:center">
              <span>// Terminal · PTY</span>
              <span id="pty-status" style="font-size:10px;color:var(--text-dead)">unknown</span>
            </div>
            <div class="card-body">
              <div style="font-size:11px;color:var(--text-dead);margin-bottom:12px">
                The user web Terminal's interactive shell mode. Independent of the toggles above and
                gated solely by an Inquisitorial Seal: a granted seal is valid for a limited time; once
                it lapses, the next PTY switch requests a fresh seal. Revoking ends the grant immediately.
              </div>
              <div id="pty-active" style="display:none;align-items:center;gap:10px;flex-wrap:wrap">
                <span style="color:var(--success);font-size:12px">● ENABLED</span>
                <span id="pty-remaining" style="font-size:11px;color:var(--text-muted)"></span>
                <button onclick="revokePty()" class="btn-danger" style="margin-left:auto">▶ REVOKE</button>
              </div>
              <div id="pty-inactive" style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">
                <span style="color:var(--text-dead);font-size:12px">● DISABLED</span>
                <button class="btn primary sm" id="pty-enable-btn" style="margin-left:auto" onclick="enablePty()">▶ REQUEST SEAL &amp; ENABLE</button>
              </div>
              <div style="margin-top:12px;display:flex;align-items:center;gap:8px;flex-wrap:wrap">
                <span style="font-size:11px;color:var(--text-dead)">Seal valid for</span>
                <input id="pty-minutes" type="number" min="1" max="1440"
                       style="width:70px;background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:5px 8px;font-family:monospace;font-size:12px">
                <span style="font-size:11px;color:var(--text-dead)">minutes</span>
                <button onclick="savePtyDuration()" style="background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:5px 12px;cursor:pointer;font-family:'Consolas',monospace;font-size:11px;letter-spacing:.08em;border-radius:2px">SAVE</button>
              </div>
              <div id="pty-msg" style="margin-top:8px;font-size:11px;color:var(--text-muted);min-height:16px"></div>
            </div>
          </div>
        </div>

    """;

    internal const string ScriptTerminal = """
        async function toggleCap(which) {
          const on = which === 'projects' ? _caps.projects : _caps.quickExec;
          const base = which === 'projects' ? '/terminal/projects' : '/terminal/quick-exec';
          const url = base + (on ? '-disable' : '-enable');
          try {
            await fetch(url, { method: 'POST' });
            await refreshTerminalCaps();
          } catch {}
        }

        function paintCap(prefix, on) {
          const light = document.getElementById('cap-' + prefix + '-light');
          const status = document.getElementById('cap-' + prefix + '-status');
          const btn = document.getElementById('cap-' + prefix + '-btn');
          if (light) { light.textContent = on ? '● ENABLED' : '● DISABLED'; light.className = 'cap-light ' + (on ? 'on' : 'off'); }
          if (status) { status.textContent = on ? 'enabled' : 'disabled'; status.style.color = on ? 'var(--success)' : 'var(--text-dead)'; }
          if (btn) { btn.textContent = on ? '▶ DISABLE' : '▶ ENABLE'; btn.className = 'btn sm ' + (on ? 'ghost' : 'primary'); }
        }

        async function refreshTerminalCaps() {
          try {
            const r = await fetch('/terminal/capabilities');
            if (!r.ok) return;
            const d = await r.json();
            _caps = { projects: !!d.projects, quickExec: !!d.quickExec };
            paintCap('projects', _caps.projects);
            paintCap('quick', _caps.quickExec);
          } catch {}
        }

        // Build one project editor row. Values are pre-escaped for the value attribute.
        function projectRowHtml(p) {
          const n = esc(p.name || ''), pa = esc(p.path || ''), de = esc(p.description || '');
          return `<div class="proj-row">
            <input class="proj-name" placeholder="name" value="${n}">
            <input class="proj-path" placeholder="/home/user/project" value="${pa}">
            <input class="proj-desc" placeholder="description (optional)" value="${de}">
            <button class="proj-remove" title="Remove project" onclick="this.closest('.proj-row').remove()">✕</button>
          </div>`;
        }
        function renderProjectRows(projects) {
          const host = document.getElementById('terminal-projects');
          if (!host) return;
          // Don't clobber the rows while the user is editing one of them.
          if (host.contains(document.activeElement)) return;
          host.innerHTML = (projects && projects.length)
            ? projects.map(projectRowHtml).join('')
            : '<div class="proj-empty">No projects declared — all file-system access is blocked.</div>';
        }
        function addProjectRow() {
          const host = document.getElementById('terminal-projects');
          if (!host) return;
          const empty = host.querySelector('.proj-empty');
          if (empty) empty.remove();
          host.insertAdjacentHTML('beforeend', projectRowHtml({}));
          const rows = host.querySelectorAll('.proj-row');
          const last = rows[rows.length - 1];
          if (last) last.querySelector('.proj-name').focus();
        }
        function collectProjects() {
          return [...document.querySelectorAll('#terminal-projects .proj-row')].map(row => ({
            name: row.querySelector('.proj-name').value.trim(),
            path: row.querySelector('.proj-path').value.trim(),
            description: row.querySelector('.proj-desc').value.trim()
          })).filter(p => p.path);
        }

        async function refreshTerminalConfig() {
          try {
            const r = await fetch('/terminal/config');
            if (!r.ok) return;
            const d = await r.json();
            const blockedEl = document.getElementById('terminal-blocked-commands');
            const status = document.getElementById('terminal-config-status');
            renderProjectRows(d.projects || []);
            if (blockedEl && document.activeElement !== blockedEl)
              blockedEl.value = (d.blockedCommands || []).join('\n');
            if (status) {
              const hasPolicy = (d.projects?.length > 0) || (d.blockedCommands?.length > 0);
              status.textContent = hasPolicy ? 'configured' : 'default';
              status.style.color = hasPolicy ? 'var(--success)' : 'var(--text-dead)';
            }
          } catch {}
        }

        async function saveTerminalConfig() {
          const blockedEl = document.getElementById('terminal-blocked-commands');
          const msg = document.getElementById('terminal-config-msg');
          if (!blockedEl) return;

          const projects = collectProjects();
          const blocked = blockedEl.value.split('\n').map(s => s.trim()).filter(s => s);
          msg.textContent = 'Saving…';
          try {
            const r = await fetch('/terminal/config', {
              method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ projects, blockedCommands: blocked })
            });
            if (!r.ok) throw new Error('save failed');
            await refreshTerminalConfig();
            msg.textContent = 'Policy saved — refresh your Aria web page (or its Explorer panel) to see new projects.';
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        // Enable PTY = request an Inquisitorial Seal, have the human grant it in the popup, then
        // consume it to start the time-limited PTY grant. Mirrors the Soul Backup seal flow.
        let _ptySealId = null;
        async function enablePty() {
          const msg = document.getElementById('pty-msg');
          const btn = document.getElementById('pty-enable-btn');
          if (btn) btn.disabled = true;
          try {
            const nonce = Array.from(crypto.getRandomValues(new Uint8Array(32)))
              .map(b => b.toString(16).padStart(2, '0')).join('');
            const r = await fetch('/seal/request', {
              method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({
                toolName: 'terminal_pty',
                reason: 'Grant the web Terminal a full interactive PTY shell on this node',
                argsPreview: 'Approving grants an interactive shell for the seal duration set below. Revoke any time.',
                nonceBase64: btoa(nonce),
                openHere: true
              })
            });
            const d = await r.json();
            if (!r.ok) throw new Error(d.error || JSON.stringify(d));
            _ptySealId = d.id;
            window.open('http://localhost:5741/seal/' + d.id, '_blank');
            msg.textContent = 'Approve the Inquisitorial Seal that opened in your browser…';
            await pollPtySeal();
          } catch (e) {
            msg.textContent = 'Error: ' + e.message;
          } finally {
            if (btn) btn.disabled = false;
          }
        }

        async function pollPtySeal() {
          if (!_ptySealId) return;
          const msg = document.getElementById('pty-msg');
          for (let i = 0; i < 60; i++) {           // ~3 min at 3s
            await new Promise(r => setTimeout(r, 3000));
            try {
              const r = await fetch('/seal/poll', {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ id: _ptySealId })
              });
              const d = await r.json();
              if (d.status === 'approved') {
                const er = await fetch('/terminal/pty-enable', {
                  method: 'POST', headers: { 'Content-Type': 'application/json' },
                  body: JSON.stringify({ sealId: _ptySealId })
                });
                _ptySealId = null;
                if (!er.ok) { const ed = await er.json().catch(() => ({})); msg.textContent = 'Error: ' + (ed.error || 'pty-enable failed'); return; }
                msg.textContent = '';
                await refreshPty();
                return;
              }
              if (d.status === 'rejected') { msg.textContent = 'Seal refused — PTY not enabled.'; _ptySealId = null; return; }
              if (d.status === 'expired')  { msg.textContent = 'Seal expired — try again.';      _ptySealId = null; return; }
            } catch { /* keep polling */ }
          }
          msg.textContent = 'Seal approval timed out.';
          _ptySealId = null;
        }

        async function revokePty() {
          const msg = document.getElementById('pty-msg');
          msg.textContent = 'Revoking…';
          try {
            await fetch('/terminal/pty-revoke', { method: 'POST' });
            await refreshPty();
            msg.textContent = '';
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        async function savePtyDuration() {
          const input = document.getElementById('pty-minutes');
          const msg = document.getElementById('pty-msg');
          let mins = parseInt(input.value, 10);
          if (!Number.isFinite(mins)) { msg.textContent = 'Enter a number of minutes.'; return; }
          mins = Math.min(1440, Math.max(1, mins));
          msg.textContent = 'Saving…';
          try {
            const r = await fetch('/terminal/pty-duration', {
              method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ minutes: mins })
            });
            const d = await r.json();
            input.value = d.minutes;
            msg.textContent = 'Seal lifetime set to ' + d.minutes + ' min.';
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        async function refreshPty() {
          try {
            const r = await fetch('/terminal/pty-enabled');
            if (!r.ok) return;
            const d = await r.json();
            const active = document.getElementById('pty-active');
            const inactive = document.getElementById('pty-inactive');
            const status = document.getElementById('pty-status');
            const minutes = document.getElementById('pty-minutes');
            const remaining = document.getElementById('pty-remaining');
            // Only overwrite the minutes field when the user isn't editing it.
            if (minutes && document.activeElement !== minutes && typeof d.minutes === 'number')
              minutes.value = d.minutes;
            if (d.enabled) {
              active.style.display = 'flex';
              inactive.style.display = 'none';
              status.textContent = 'enabled';
              status.style.color = 'var(--success)';
              if (remaining) {
                const secs = d.remainingSeconds || 0;
                const m = Math.floor(secs / 60), s = secs % 60;
                remaining.textContent = 'expires in ' + (m > 0 ? m + 'm ' : '') + s + 's';
              }
            } else {
              active.style.display = 'none';
              inactive.style.display = 'flex';
              status.textContent = 'disabled';
              status.style.color = 'var(--text-dead)';
              if (remaining) remaining.textContent = '';
            }
          } catch {}
        }

        function fmtIdle(secs) {
          if (secs < 60)  return `${secs}s ago`;
          if (secs < 3600) return `${Math.floor(secs/60)}m ago`;
          return `${Math.floor(secs/3600)}h ago`;
        }
        function fmtBytes(b) {
          if (b < 1024)        return `${b} B`;
          if (b < 1048576)     return `${(b/1024).toFixed(1)} KB`;
          return `${(b/1048576).toFixed(2)} MB`;
        }

    """;
}
