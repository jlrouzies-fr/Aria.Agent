namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
    internal const string PanelTelemetry = """
        <!-- ── TELEMETRY TAB ──────────────────────────────────────────── -->
        <div id="panel-telemetry" style="display:none">
          <div class="section-head"><div class="section-title">// Telemetry</div></div>
          <div class="card">
            <div class="card-header" style="display:flex;justify-content:space-between;align-items:center">
              <span>// Privileged Telemetry</span>
              <span id="sudo-status" style="font-size:10px;color:var(--text-dead)">inactive</span>
            </div>
            <div class="card-body">
              <div style="font-size:11px;color:var(--text-dead);margin-bottom:12px">
                GPU utilization and power require root on macOS. The password is sent once to
                authenticate <code>sudo powermetrics</code> and is not stored.
              </div>
              <div id="sudo-form" style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
                <input id="sudo-pw" type="password" placeholder="sudo password" autocomplete="off"
                       style="background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:6px 10px;font-family:monospace;font-size:12px;flex:1;min-width:160px">
                <button onclick="grantSudo()" style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">▶ GRANT</button>
              </div>
              <div id="sudo-active" style="display:none;align-items:center;gap:10px;flex-wrap:wrap">
                <span style="color:var(--success);font-size:12px">● ACTIVE</span>
                <span id="sudo-gpu" style="font-size:11px;color:var(--text-muted)"></span>
                <button onclick="revokeSudo()" class="btn-danger" style="margin-left:auto">▶ REVOKE</button>
              </div>
              <div id="sudo-msg" style="margin-top:8px;font-size:11px;color:var(--text-muted);min-height:16px"></div>
            </div>
          </div>
        </div>

    """;

    internal const string ScriptTelemetry = """
        async function grantSudo() {
          const pw = document.getElementById('sudo-pw').value;
          const msg = document.getElementById('sudo-msg');
          if (!pw) { msg.textContent = 'Enter your sudo password.'; return; }
          msg.textContent = 'Authenticating…';
          try {
            const r = await fetch('/metrics/sudo', {
              method: 'POST',
              headers: {'Content-Type': 'application/json'},
              body: JSON.stringify({ password: pw })
            });
            const d = await r.json();
            if (d.lastError) { msg.textContent = 'Error: ' + d.lastError; }
            else { msg.textContent = ''; }
            document.getElementById('sudo-pw').value = '';
            await refreshSudo();
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        async function revokeSudo() {
          const msg = document.getElementById('sudo-msg');
          msg.textContent = 'Revoking…';
          try {
            await fetch('/metrics/sudo', { method: 'DELETE' });
            await refreshSudo();
            msg.textContent = '';
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        async function refreshSudo() {
          try {
            const [statusR, metricsR] = await Promise.all([
              fetch('/metrics/sudo/status'),
              fetch('/metrics')
            ]);
            if (!statusR.ok) return;
            const d = await statusR.json();
            const metrics = metricsR.ok ? await metricsR.json() : {};
            const form = document.getElementById('sudo-form');
            const active = document.getElementById('sudo-active');
            const status = document.getElementById('sudo-status');
            const gpu = document.getElementById('sudo-gpu');
            const msg = document.getElementById('sudo-msg');
            if (d.isRunning) {
              form.style.display = 'none';
              active.style.display = 'flex';
              status.textContent = 'active';
              status.style.color = 'var(--success)';
              const util = metrics.gpuUtilizationPercent != null ? metrics.gpuUtilizationPercent : d.latestGpuUtilizationPercent;
              gpu.textContent = `GPU ${util != null ? util.toFixed(1) + '%' : '--'} · ${d.latestGpuPowerMw != null ? d.latestGpuPowerMw.toFixed(0) + ' mW' : '--'}`;
            } else {
              form.style.display = 'flex';
              active.style.display = 'none';
              status.textContent = 'inactive';
              status.style.color = 'var(--text-dead)';
              gpu.textContent = '';
              if (d.lastError && msg.textContent === '') msg.textContent = d.lastError;
            }
          } catch {}
        }

        // Independent capability toggles: Agent Projects + user Quick Exec. (PTY is seal-gated,
        // handled by the PTY card below.)
        let _caps = { projects: false, quickExec: false };
    """;
}
