namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
    internal const string PanelOverview = """
        <!-- ── OVERVIEW ─────────────────────────────────────────────── -->
        <div id="panel-overview">
          <div class="section-head">
            <div class="section-title">// Overview</div>
          </div>
          <div class="card">
            <div class="card-header">// Setup Your Node</div>
            <div class="card-body">
              <div class="section-lead">Two quick steps bind this cogitator node and give it a machine spirit to speak to. You can explore any section meanwhile.</div>
              <div id="onboarding-list">Loading…</div>
            </div>
          </div>
        </div>

    """;

    internal const string ScriptOverview = """
        // ── Overview + onboarding ─────────────────────────────────────────────
        async function refreshOverview() {
          const panel = document.getElementById('panel-overview');
          if (!panel || panel.style.display === 'none') { updateOnboardingBadges(); return; }
          const linked = !!(soul && soul.serverSoulId);

          let chReady = 0, mcpCount = 0, oauthCount = 0, projCount = 0;
          try {
            const d = await (await fetch('/channels')).json();
            chReady = (d.channels || []).filter(c => c.hasKey || !c.isPublic).length;
          } catch {}
          try {
            const d = await (await fetch('/mcps')).json();
            mcpCount = (d.servers || []).length;
          } catch {}
          try {
            const [m, g] = await Promise.all([fetch('/oauth/microsoft/status'), fetch('/oauth/google/status')]);
            if (m.ok && (await m.json()).connected) oauthCount++;
            if (g.ok && (await g.json()).connected) oauthCount++;
          } catch {}
          try {
            const d = await (await fetch('/terminal/projects')).json();
            projCount = (d.projects || []).length;
          } catch {}
          renderOnboarding({ soulDone: !!(soul && soul.name), linked, chReady, mcpCount, oauthCount, projCount });
        }

        function onboardStepHtml(s, opts) {
          const optional = !!opts.optional;
          const cls = (s.done ? 'done' : (opts.next ? 'next' : '')) + (optional ? ' optional' : '');
          const num = s.done ? '✓' : (optional ? '○' : opts.index);
          let cta = '';
          if (s.section) {
            const label = s.done ? 'Manage →' : (s.cta || 'Set up →');
            const btnCls = (!s.done && !optional) ? 'btn primary sm' : 'btn ghost sm';
            cta = `<div style="margin-top:8px"><button class="${btnCls}" onclick="showSection('${s.section}')">${label}</button></div>`;
          }
          return `<div class="onboard-step ${cls}">
            <div class="step-num">${num}</div>
            <div class="step-body">
              <div class="step-title">${s.title}</div>
              <div class="step-sub">${s.sub}</div>
              ${cta}
            </div>
          </div>`;
        }

        function renderOnboarding(s) {
          const el = document.getElementById('onboarding-list');
          if (!el) return;
          const channelDone = s.chReady > 0;
          const nextIdx = !s.soulDone ? 0 : (!channelDone ? 1 : -1);
          const required = [
            {
              done: s.soulDone,
              title: s.soulDone ? ('Soul bound — ' + esc(soul.name)) : 'Set up your Soul',
              sub: s.soulDone
                ? (s.linked ? 'Identity active and linked to a server.' : 'Identity active. Link a server from the Soul section when ready.')
                : 'Create a new cryptographic soul, or link this machine to an existing soul on another device.',
              section: 'soul', cta: 'Configure Soul →'
            },
            {
              done: channelDone,
              title: channelDone ? (s.chReady + ' channel(s) ready') : 'Configure your first Channel',
              sub: channelDone
                ? 'A machine spirit is reachable. Add more from the Channels section any time.'
                : 'Add a cloud provider key, or point at a local model (LM Studio, Ollama, llama.cpp).',
              section: 'channels', cta: 'Configure Channel →'
            }
          ];
          const optional = [
            {
              done: s.mcpCount > 0,
              title: s.mcpCount > 0 ? (s.mcpCount + ' MCP server(s) configured') : 'Connect MCP servers',
              sub: 'Give the agent external tools via the Model Context Protocol.',
              section: 'mcp'
            },
            {
              done: s.oauthCount > 0,
              title: s.oauthCount > 0 ? (s.oauthCount + ' account(s) connected') : 'Connect email & calendar',
              sub: 'Link Microsoft 365 or Google for Outlook / Gmail and calendar tools.',
              section: 'oauth'
            },
            {
              done: s.projCount > 0,
              title: s.projCount > 0 ? (s.projCount + ' Terminal project(s) allowed') : 'Declare Terminal projects',
              sub: 'Grant the Terminal tool read/write access to specific folders (Allowed Paths).',
              section: 'terminal'
            }
          ];
          const reqHtml = required.map((st, i) => onboardStepHtml(st, { index: i + 1, next: i === nextIdx })).join('');
          const optHtml = optional.map(st => onboardStepHtml(st, { optional: true })).join('');
          el.innerHTML = reqHtml + '<div class="onboard-optional-label">// Optional</div>' + optHtml;
          setNavBadge('soul', !s.soulDone);
          setNavBadge('channels', s.soulDone && !channelDone);
        }

        // Lightweight badge refresh used when the overview panel isn't the visible one.
        async function updateOnboardingBadges() {
          const soulDone = !!(soul && soul.name);
          let channelDone = false;
          if (soulDone) {
            try {
              const r = await fetch('/channels');
              const d = await r.json();
              channelDone = (d.channels || []).some(c => c.hasKey || !c.isPublic);
            } catch {}
          }
          setNavBadge('soul', !soulDone);
          setNavBadge('channels', soulDone && !channelDone);
          setNavBadge('memory', await memoryModelMissing());
        }

        // Explanatory tooltip per onboarding badge (rendered via the shared #aria-tip element).
        const NAV_BADGE_TIPS = {
          soul:     'No soul bound yet — forge a new identity or join an existing one to activate this node.',
          channels: 'No machine spirit reachable — add an LLM channel so the cogitator has a model to speak to.',
          memory:   'Noosphere has no embedding model set — configure one to enable semantic recall.'
        };

        function setNavBadge(section, show) {
          const item = document.querySelector('.nav-item[data-section="' + section + '"]');
          if (!item) return;
          let b = item.querySelector('.nav-badge');
          if (show && !b) {
            b = document.createElement('span');
            b.className = 'nav-badge';
            b.textContent = '!';
            b.setAttribute('data-tip', NAV_BADGE_TIPS[section] || 'This step still needs your attention.');
            b.setAttribute('aria-label', b.getAttribute('data-tip'));
            item.appendChild(b);
          } else if (!show && b) {
            b.remove();
          }
        }

    """;
}
