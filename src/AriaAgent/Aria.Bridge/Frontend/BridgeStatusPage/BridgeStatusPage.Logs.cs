namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
    internal const string PanelLogs = """
        <!-- ── LOGS TAB ─────────────────────────────────────────────── -->
        <div id="panel-logs" style="display:none">
          <div class="section-head"><div class="section-title">// Event Log</div></div>
          <div class="card">
            <div class="card-header" style="display:flex;justify-content:space-between;align-items:center">
              <span>// Event Log</span>
              <span id="log-count" style="font-size:10px;color:var(--text-dead)">0 entries</span>
            </div>
            <div class="card-body" style="padding:0">
              <div id="log-panel" style="margin:0;padding:10px 14px;font-size:11px;line-height:1.7;max-height:480px;overflow-y:auto;background:var(--bg-base);border-radius:0 0 2px 2px;font-family:'Consolas','Courier New',monospace">
                <span style="color:var(--text-dead)">(waiting for events…)</span>
              </div>
            </div>
          </div>
        </div>

    """;

    internal const string ScriptLogs = """
        // ── Logs ──────────────────────────────────────────────────────────────
        let cachedLogs = [];

        function logClass(line) {
          if (/error|fail|exception/i.test(line)) return 'log-error';
          if (/warn|⚠/i.test(line)) return 'log-warn';
          if (/ok|success|✓|started|operational/i.test(line)) return 'log-ok';
          return '';
        }

        function renderLogs() {
          const panel = document.getElementById('log-panel');
          const countEl = document.getElementById('log-count');
          if (!panel) return;
          if (countEl) countEl.textContent = cachedLogs.length + ' entries';
          // Don't rebuild (and clear any selection) unless the log set actually changed and the user
          // isn't currently selecting text in the panel.
          const sig = cachedLogs.length + '|' + (cachedLogs[cachedLogs.length - 1] || '');
          if (panel.dataset.r === '1' && (sig === _lastLogSig || uiBusy(panel))) return;
          _lastLogSig = sig;
          panel.dataset.r = '1';
          if (cachedLogs.length === 0) {
            panel.innerHTML = '<span style="color:var(--text-dead)">(no events yet)</span>';
            return;
          }
          panel.innerHTML = cachedLogs.map(line => {
            const cls = logClass(line);
            return `<div class="${cls}" style="padding:1px 0;border-bottom:1px solid rgba(90,64,64,0.2)">${esc(line)}</div>`;
          }).join('');
          panel.scrollTop = panel.scrollHeight;
        }

    """;
}
