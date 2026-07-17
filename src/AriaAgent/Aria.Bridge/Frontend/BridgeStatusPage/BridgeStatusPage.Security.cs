namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
    internal const string PanelSecurity = """
        <!-- ── SECURITY TAB ─────────────────────────────────────────── -->
        <div id="panel-security" style="display:none">
          <div class="section-head"><div class="section-title">// Security</div></div>
          <div class="card">
            <div class="card-header">// Layer B — Context Grant Enforcement</div>
            <div class="card-body">
              <div class="section-lead">When enforcing, sensitive server-relayed operations (provider-key spend, shell, tool execution) run only after a human here approves this session — so a compromised hosted server can't drive them on its own. Enabled by default; turn it off to fall back to observe-only (logs, never blocks).</div>
              <div class="cap-row" style="margin-top:12px">
                <span id="enforce-light" class="cap-light off">◌ LOADING…</span>
                <button id="enforce-toggle" class="btn primary" onclick="toggleEnforcement()" disabled>—</button>
              </div>
            </div>
          </div>
          <div class="card">
            <div class="card-header">// Security Posture</div>
            <div class="card-body">
              <div class="section-lead">A live summary of what this node currently permits. Everything below is enforced locally — the hosted server can neither read nor widen it.</div>
              <div id="security-posture" class="metrics">Loading…</div>
            </div>
          </div>
          <div class="card">
            <div class="card-header" style="display:flex;justify-content:space-between;align-items:center">
              <span>// Audit Trail</span>
              <span id="audit-count" style="font-size:10px;color:var(--text-dead)">0 events</span>
            </div>
            <div class="card-body" style="padding:0">
              <div id="audit-panel" style="margin:0;padding:10px 14px;font-size:11px;line-height:1.6;max-height:480px;overflow-y:auto;background:var(--bg-base);border-radius:0 0 2px 2px;font-family:'Consolas','Courier New',monospace">
                <span style="color:var(--text-dead)">(waiting for events…)</span>
              </div>
            </div>
          </div>
        </div>

    """;

    internal const string ScriptSecurity = """
        // ── Security posture ──────────────────────────────────────────────────
        function postureTile(label, value, tone) {
          const color = tone === 'ok' ? 'var(--success)' : (tone === 'warn' ? 'var(--gold-bright)' : 'var(--text-muted)');
          return `<div class="metric"><div class="metric-label">${label}</div>` +
                 `<div class="metric-value" style="font-size:13px;color:${color}">${value}</div></div>`;
        }
        async function refreshSecurity() {
          const panel = document.getElementById('panel-security');
          if (!panel || panel.style.display === 'none') return;
          const el = document.getElementById('security-posture');
          if (!el) return;
          const soulOk = !!(soul && soul.hasKeypair);
          const linked = !!(soul && soul.serverSoulId);
          let projects = false, quick = false, pty = false, sudo = false;
          try { const r = await fetch('/terminal/capabilities'); if (r.ok) { const d = await r.json(); projects = !!d.projects; quick = !!d.quickExec; pty = !!d.pty; } } catch {}
          try { const r = await fetch('/metrics/sudo/status');   if (r.ok) sudo = !!(await r.json()).isRunning; } catch {}
          el.innerHTML = [
            postureTile('Soul Keypair',   soulOk ? 'PRESENT' : 'MISSING',   soulOk ? 'ok' : 'warn'),
            postureTile('Server Link',    linked ? 'LINKED'  : 'UNLINKED',  linked ? 'ok' : 'muted'),
            postureTile('Agent Projects', projects ? 'ENABLED' : 'DISABLED', projects ? 'warn' : 'muted'),
            postureTile('Quick Exec',      quick ? 'ENABLED'  : 'DISABLED',  quick ? 'warn' : 'muted'),
            postureTile('PTY Shell',       pty  ? 'GRANTED'  : 'OFF',       pty  ? 'warn' : 'muted'),
            postureTile('Sudo Telemetry',  sudo ? 'ACTIVE'   : 'INACTIVE',  sudo ? 'warn' : 'muted')
          ].join('');
        }

        // ── Layer B enforcement toggle ────────────────────────────────
        let _enforceState = null;
        function renderEnforce() {
          const light = document.getElementById('enforce-light');
          const btn   = document.getElementById('enforce-toggle');
          if (!light || !btn) return;
          if (_enforceState === null) { light.className = 'cap-light off'; light.textContent = '◌ LOADING…'; btn.disabled = true; btn.textContent = '—'; return; }
          btn.disabled = false;
          if (_enforceState) {
            light.className = 'cap-light on';  light.textContent = '⛨ ENFORCING';
            btn.className = 'btn ghost'; btn.textContent = 'DISABLE';
          } else {
            light.className = 'cap-light off'; light.textContent = '○ OBSERVING (logs only)';
            btn.className = 'btn primary'; btn.textContent = 'ENABLE';
          }
        }
        async function loadEnforcement() {
          renderEnforce();
          try { const r = await fetch('/context/enforcement'); if (r.ok) _enforceState = !!(await r.json()).enabled; }
          catch { _enforceState = null; }
          renderEnforce();
        }
        async function toggleEnforcement() {
          const next = !_enforceState;
          const btn = document.getElementById('enforce-toggle');
          if (btn) btn.disabled = true;
          try {
            const r = await fetch('/context/enforcement', {
              method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ enabled: next })
            });
            if (r.ok) _enforceState = !!(await r.json()).enabled;
          } catch {}
          renderEnforce();
        }

    """;

    internal const string ScriptAudit = """
        // ── Security audit trail ──────────────────────────────────────────────
        let cachedAudit = [];
        let _lastAuditSig = '';

        function renderAudit() {
          const panel = document.getElementById('audit-panel');
          const countEl = document.getElementById('audit-count');
          if (!panel) return;
          if (countEl) countEl.textContent = cachedAudit.length + ' events';
          const sig = cachedAudit.length + '|' + (cachedAudit[0]?.id ?? '');
          if (panel.dataset.r === '1' && (sig === _lastAuditSig || uiBusy(panel))) return;
          _lastAuditSig = sig;
          panel.dataset.r = '1';
          if (cachedAudit.length === 0) {
            panel.innerHTML = '<span style="color:var(--text-dead)">(no audit events yet)</span>';
            return;
          }
          const rows = cachedAudit.map(e => {
            const ts = new Date(e.timestamp).toLocaleString();
            const allowed = e.allowed
              ? '<span style="color:var(--success)">ALLOW</span>'
              : '<span style="color:var(--text-title)">DENY</span>';
            const cap = e.capability ? ` <span style="color:var(--text-muted)">[${esc(e.capability)}]</span>` : '';
            const detail = e.detail ? ` — ${esc(e.detail)}` : '';
            return `<div style="padding:3px 0;border-bottom:1px solid rgba(90,64,64,0.2)">${ts} ${allowed}${cap} <strong>${esc(e.category)}:${esc(e.action)}</strong>${detail}</div>`;
          }).join('');
          panel.innerHTML = rows;
        }

        async function refreshAudit() {
          try {
            const r = await fetch('/audit/log?limit=100');
            if (!r.ok) return;
            cachedAudit = await r.json();
            if (document.getElementById('panel-security').style.display !== 'none') renderAudit();
          } catch {}
        }

    """;
}
