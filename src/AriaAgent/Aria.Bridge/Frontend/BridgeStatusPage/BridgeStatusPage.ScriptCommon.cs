namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
    internal const string ScriptCommonNav = """
      <script>
        // ── Section navigation (left sidebar) ─────────────────────────────────
        const SECTIONS = ['overview','soul','channels','memory','mcp','oauth','terminal','telemetry','logs','security','data','endpoints'];
        function showSection(name, btn) {
          if (!SECTIONS.includes(name)) name = 'overview';
          SECTIONS.forEach(t => {
            const p = document.getElementById('panel-' + t);
            if (p) p.style.display = t === name ? '' : 'none';
          });
          document.querySelectorAll('.nav-item').forEach(b => b.classList.remove('active'));
          const active = btn || document.querySelector('.nav-item[data-section="' + name + '"]');
          if (active) active.classList.add('active');
          // Drive the URL hash (not localStorage) so a refresh is deterministic: no hash → Overview,
          // #section → that section. Avoids stale cross-version state landing you on the wrong tab.
          try { history.replaceState(null, '', name === 'overview' ? location.pathname : '#' + name); } catch {}
          if (name === 'overview')  refreshOverview();
          if (name === 'channels')  refreshChannels();
          if (name === 'memory')    refreshMemoryConfig();
          if (name === 'mcp')       { refreshMcpServers(); refreshWebSearchKey(); }
          if (name === 'oauth')     { refreshOAuth(); refreshOAuthConfig(); }
          if (name === 'terminal')  { refreshTerminalCaps(); refreshTerminalConfig(); refreshPty(); }
          if (name === 'telemetry') refreshSudo();
          if (name === 'security')  { refreshSecurity(); renderAudit(); loadEnforcement(); }
          if (name === 'data')      { refreshData(); refreshNoosphere(); }
          if (name === 'logs')      renderLogs();
        }
        // back-compat alias in case anything still calls showTab
        function showTab(name) { showSection(name); }

        function restoreTab() {
          // Section is driven purely by the URL hash: a bare load (no hash) always opens Overview,
          // and #section deep-links (or a refresh after navigating) restore that section.
          const hash = (location.hash || '').replace('#','').toLowerCase();
          showSection(SECTIONS.includes(hash) ? hash : 'overview');
        }
        window.addEventListener('hashchange', restoreTab);

    """;

    internal const string ScriptStatusAndTooltips = """
        // ── Status polling ────────────────────────────────────────────────────
        async function refresh() {
          try {
            const r = await fetch('/status');
            if (!r.ok) return;
            const d = await r.json();
            document.getElementById('val-version').textContent = d.version ?? '—';
            document.getElementById('val-uptime').textContent  = d.uptime  ?? '—';
            const sessions = d.sessions ?? [];
            const el = document.getElementById('val-sessions');
            if (sessions.length === 0) {
              el.textContent = '(none)';
            } else {
              el.innerHTML = sessions.map(s =>
                `<div style="padding:3px 0;border-bottom:1px solid var(--border-dim);display:flex;justify-content:space-between">` +
                `<span style="color:var(--text-normal)">${esc(s.label)}</span>` +
                `<span style="color:var(--text-dead);font-size:10px">idle ${fmtIdle(s.idleSecs)}</span>` +
                `</div>`
              ).join('');
            }
          } catch {}
          try {
            const lr = await fetch('/logs');
            if (!lr.ok) return;
            cachedLogs = await lr.json();
            // Only re-render if logs tab is visible
            if (document.getElementById('panel-logs').style.display !== 'none') renderLogs();
          } catch {}
          try {
            await refreshAudit();
          } catch {}
          try {
            await refreshMemoryHealth();
          } catch {}
          // keep the overview status tiles live while that panel is open
          if (document.getElementById('panel-overview').style.display !== 'none') refreshOverview();
        }

        refreshSoul();
        refresh();
        refreshSudo();
        refreshTerminalCaps();
        refreshTerminalConfig();
        refreshJoinCode();
        refreshSoulPin();
        refreshAudit();
        loadSessionCode();
        updateOnboardingBadges();
        restoreTab();
        setInterval(refresh, 5000);
        setInterval(updateOnboardingBadges, 20000);
        setInterval(refreshSudo, 5000);
        setInterval(refreshTerminalCaps, 5000);
        setInterval(refreshPty, 5000);
        setInterval(refreshSoul, 15000);
        setInterval(refreshJoinCode, 4000);
        setInterval(refreshSoulPin, 8000);
        setInterval(refreshAudit, 5000);

        // ── Stylized tooltips ────────────────────────────────────────────────
        // One shared #aria-tip element follows the cursor (clamped to the viewport). Native `title`
        // attributes anywhere on the page — including dynamically-rendered content — are lazily
        // promoted to data-tip on first hover so they render with this style, not the OS default.
        (function initTips() {
          const tip = document.getElementById('aria-tip');
          if (!tip) return;
          let cur = null;
          document.addEventListener('mouseover', e => {
            const el = e.target.closest('[data-tip],[title]');
            if (!el) return;
            if (!el.hasAttribute('data-tip')) {
              const t = el.getAttribute('title');
              if (!t) return;                       // empty title → nothing to show
              el.setAttribute('data-tip', t);
              el.removeAttribute('title');          // suppress the native tooltip
            }
            const text = el.getAttribute('data-tip');
            if (!text) return;
            cur = el;
            tip.textContent = text;
            tip.className = '';
            const variant = el.getAttribute('data-tip-variant');
            if (variant) tip.classList.add('aria-tip-' + variant);
            tip.style.opacity = '1';
          });
          document.addEventListener('mousemove', e => {
            if (!cur) return;
            const pad = 14, tw = tip.offsetWidth, th = tip.offsetHeight;
            let x = e.clientX + pad, y = e.clientY + pad;
            if (x + tw > window.innerWidth  - 8) x = e.clientX - tw - pad;
            if (y + th > window.innerHeight - 8) y = e.clientY - th - pad;
            tip.style.left = Math.max(8, x) + 'px';
            tip.style.top  = Math.max(8, y) + 'px';
          });
          document.addEventListener('mouseout', e => {
            if (cur && (!e.relatedTarget || !cur.contains(e.relatedTarget))) {
              const gone = e.target.closest('[data-tip]');
              if (gone === cur) {
                cur = null;
                tip.style.opacity = '0';
                tip.style.left = '-9999px';
                tip.className = '';
              }
            }
          });
        })();
      </script>
    """;
}
