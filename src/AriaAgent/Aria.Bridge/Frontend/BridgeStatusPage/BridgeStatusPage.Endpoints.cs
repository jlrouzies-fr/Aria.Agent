namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
// NOTE: static reference content only — no dedicated JS for this panel.
    internal const string PanelEndpoints = """
        <!-- ── ENDPOINTS TAB ──────────────────────────────────────────── -->
        <div id="panel-endpoints" style="display:none">
          <div class="section-head"><div class="section-title">// Endpoints</div></div>
          <div class="card">
            <div class="card-header">// Endpoints</div>
            <div class="card-body" style="padding:0">
              <details class="endpoint-group">
                <summary class="endpoint-group-summary">// Status &amp; Monitoring</summary>
                <table>
                  <tr><td><span class="method get">GET</span> /</td><td>/</td><td>This status page</td></tr>
                  <tr><td><span class="method get">GET</span> /status</td><td>/status</td><td>Machine-readable status</td></tr>
                  <tr><td><span class="method get">GET</span> /health</td><td>/health</td><td>Health check</td></tr>
                  <tr><td><span class="method get">GET</span> /logs</td><td>/logs</td><td>Recent log entries</td></tr>
                  <tr><td><span class="method get">GET</span> /metrics</td><td>/metrics</td><td>Live bridge performance metrics</td></tr>
                  <tr><td><span class="method post">POST</span> /metrics/sudo</td><td>/metrics/sudo</td><td>Grant sudo for privileged GPU telemetry</td></tr>
                  <tr><td><span class="method get">GET</span> /metrics/sudo/status</td><td>/metrics/sudo/status</td><td>Privileged telemetry status</td></tr>
                  <tr><td><span class="method get">GET</span> /db-info</td><td>/db-info</td><td>SQLite path, size, and record counts</td></tr>
                </table>
              </details>
              <details class="endpoint-group">
                <summary class="endpoint-group-summary">// Soul &amp; Identity</summary>
                <table>
                  <tr><td><span class="method get">GET</span> /soul</td><td>/soul</td><td>Local soul identity + saved server links</td></tr>
                  <tr><td><span class="method post">POST</span> /soul/link-server</td><td>/soul/link-server</td><td>Register a new Aria.Web server link and activate it</td></tr>
                  <tr><td><span class="method post">POST</span> /soul/switch-server</td><td>/soul/switch-server</td><td>Activate a previously saved server link</td></tr>
                  <tr><td><span class="method post">POST</span> /soul/unlink</td><td>/soul/unlink</td><td>Clear the active server link and disconnect</td></tr>
                  <tr><td><span class="method delete">DELETE</span> /soul/server-link</td><td>/soul/server-link?id=</td><td>Remove a saved server link</td></tr>
                </table>
              </details>
              <details class="endpoint-group">
                <summary class="endpoint-group-summary">// Cogitations</summary>
                <table>
                  <tr><td><span class="method post">POST</span> /cogitations/init</td><td>/cogitations/init</td><td>Ensure a cogitation exists locally</td></tr>
                  <tr><td><span class="method get">GET</span> /cogitations</td><td>/cogitations?soulId=</td><td>List cogitations for a soul</td></tr>
                  <tr><td><span class="method post">POST</span> /cogitations/{id}/messages</td><td>/cogitations/{id}/messages</td><td>Append a message</td></tr>
                </table>
              </details>
              <details class="endpoint-group">
                <summary class="endpoint-group-summary">// Tools</summary>
                <table>
                  <tr><td><span class="method post">POST</span> /tools/list</td><td>/tools/list</td><td>Discover tools from a stdio MCP server</td></tr>
                  <tr><td><span class="method post">POST</span> /tools/call</td><td>/tools/call</td><td>Call a named tool</td></tr>
                </table>
              </details>
              <details class="endpoint-group">
                <summary class="endpoint-group-summary">// OAuth</summary>
                <table>
                  <tr><td><span class="method get">GET</span> /oauth/microsoft/status</td><td>/oauth/microsoft/status</td><td>Microsoft connection status</td></tr>
                  <tr><td><span class="method get">GET</span> /oauth/microsoft/token</td><td>/oauth/microsoft/token</td><td>Get Microsoft access token</td></tr>
                  <tr><td><span class="method get">GET</span> /oauth/google/status</td><td>/oauth/google/status</td><td>Google connection status</td></tr>
                  <tr><td><span class="method get">GET</span> /oauth/google/token</td><td>/oauth/google/token</td><td>Get Google access token</td></tr>
                </table>
              </details>
              <details class="endpoint-group">
                <summary class="endpoint-group-summary">// Database Admin</summary>
                <table>
                  <tr><td><span class="method delete">DELETE</span> /db/cogitations</td><td>/db/cogitations</td><td>Wipe all local cogitations + messages</td></tr>
                  <tr><td><span class="method delete">DELETE</span> /db/messages</td><td>/db/messages</td><td>Wipe messages only</td></tr>
                </table>
              </details>
            </div>
          </div>
        </div>

    """;
}
