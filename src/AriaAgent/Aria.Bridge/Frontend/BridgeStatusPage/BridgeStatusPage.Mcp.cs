namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
    internal const string PanelMcp = """
        <!-- ── MCP TAB ──────────────────────────────────────────────── -->
        <div id="panel-mcp" style="display:none">
          <div class="section-head"><div class="section-title">// Tools / MCP</div></div>
          <div class="card">
            <div class="card-header" style="display:flex;justify-content:space-between;align-items:center">
              <span>// Web Search (Ollama)</span>
              <span id="websearch-status" style="font-size:11px;color:var(--text-dead);border-left:none">unknown</span>
            </div>
            <div class="card-body">
              <div class="section-lead">
                Uses Ollama's <code>/api/web_search</code> endpoint. The call is made by this node, so the
                key stays on your machine and the server never sees it. Get a key from
                <a href="https://ollama.com" target="_blank">ollama.com</a> → account → API Keys.
              </div>
              <div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin-top:10px">
                <input id="websearch-key" type="password" placeholder="paste API key" autocomplete="off"
                       style="flex:1;min-width:200px;background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:6px 8px;font-family:monospace;font-size:12px">
                <button class="btn primary" onclick="saveWebSearchKey()">▶ SAVE</button>
                <button class="btn-danger" id="websearch-remove-btn" onclick="removeWebSearchKey()" style="display:none;padding:5px 12px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.06em">REMOVE</button>
              </div>
              <div id="websearch-msg" style="font-size:11.5px;color:var(--text-muted);margin-top:6px"></div>
            </div>
          </div>
          <div class="card">
            <div class="card-header" style="display:flex;justify-content:space-between;align-items:center">
              <span>// MCP Servers</span>
              <span id="mcp-status" style="font-size:10px;color:var(--text-dead)">unknown</span>
            </div>
            <div class="card-body">
              <div style="font-size:11px;color:var(--text-dead);margin-bottom:12px">
                MCP servers are authored here on the bridge. The web UI only sees names/transports;
                commands, arguments, and env secrets never leave this node.
              </div>
              <div class="subsection-title">Configured Servers</div>
              <div id="mcp-list" style="font-size:12px;color:var(--text-muted);margin-bottom:22px"></div>
              <div>
                <div id="mcp-form-label" class="subsection-title">Add New Server</div>
                <div style="display:flex;gap:8px;margin-bottom:8px">
                  <div class="custom-select" id="mcp-transport-wrapper" style="flex:0 0 auto">
                    <div class="custom-select-trigger" onclick="toggleCustomDropdown(this)">LOCAL BRIDGE</div>
                    <div class="custom-select-options" style="display:none"></div>
                    <input type="hidden" id="mcp-transport" value="2">
                  </div>
                  <input id="mcp-name" placeholder="name (e.g. filesystem)"
                         style="flex:1;background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:7px;font-family:monospace;font-size:12px">
                </div>
                <div id="mcp-local-fields">
                  <input id="mcp-command" placeholder="command (e.g. npx)"
                         style="width:100%;background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:7px;font-family:monospace;font-size:12px;margin-bottom:8px">
                  <textarea id="mcp-args" rows="3" placeholder="arguments, one per line&#10;-y&#10;@modelcontextprotocol/server-filesystem"
                            style="width:100%;background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:7px;font-family:monospace;font-size:12px;resize:vertical;margin-bottom:8px"></textarea>
                  <textarea id="mcp-env" rows="2" placeholder="env vars KEY=VALUE, one per line&#10;MY_TOKEN=abc123"
                            style="width:100%;background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:7px;font-family:monospace;font-size:12px;resize:vertical;margin-bottom:8px"></textarea>
                </div>
                <input id="mcp-url" placeholder="https://my-mcp-server.example.com/mcp"
                       style="width:100%;background:var(--bg-surface);border:1px solid var(--border-normal);color:var(--text-bright);padding:7px;font-family:monospace;font-size:12px;margin-bottom:8px;display:none">
                <label style="display:flex;align-items:center;gap:6px;font-size:11px;color:var(--text-muted);margin-bottom:8px;cursor:pointer">
                  <input id="mcp-enabled" type="checkbox" checked> Enabled
                </label>
                <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">
                  <button onclick="saveMcpServer()" id="mcp-save-btn" style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">▶ SAVE SERVER</button>
                  <button onclick="testMcpFromForm()" style="background:transparent;border:1px solid var(--border-normal);color:var(--text-bright);padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">TEST CONNECTIVITY</button>
                  <button onclick="cancelMcpEdit()" id="mcp-cancel-btn" style="display:none;background:transparent;border:1px solid var(--border-normal);color:var(--text-muted);padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">CANCEL</button>
                  <span id="mcp-form-msg" style="font-size:11px;color:var(--text-muted)"></span>
                </div>
              </div>
            </div>
          </div>
          <div class="card">
            <div class="card-header">// MCP Server Processes</div>
            <div class="card-body">
              <div id="val-sessions" style="font-size:12px;color:var(--text-muted)">(none)</div>
            </div>
          </div>
        </div>

    """;

    internal const string ScriptMcp = """
        // ── MCP servers ───────────────────────────────────────────────────────

        function onMcpTransportChange() {
          const isSse = document.getElementById('mcp-transport').value === '1';
          document.getElementById('mcp-local-fields').style.display = isSse ? 'none' : '';
          document.getElementById('mcp-url').style.display = isSse ? '' : 'none';
        }

        async function refreshMcpServers() {
          populateCustomSelect('mcp-transport-wrapper',
            [{ value: '2', label: 'LOCAL BRIDGE' }, { value: '1', label: 'SSE' }],
            document.getElementById('mcp-transport').value || '2',
            (val) => onMcpTransportChange());

          const st = document.getElementById('mcp-status');
          try {
            const r = await fetch('/mcps');
            const d = await r.json();
            const list = d.servers || [];
            st.textContent = list.length + ' server(s)';
            st.style.color = 'var(--text-muted)';

            const btn = 'padding:5px 12px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.06em';
            document.getElementById('mcp-list').innerHTML = list.map(s => {
              const n = esc(s.name), nj = JSON.stringify(s.name);
              const isSse = s.transport === 1;
              const badge = isSse ? 'SSE' : 'LB';
              const detail = isSse ? esc(s.url || '') : esc(s.command || '');
              const enabled = s.enabled ? '<span style="color:var(--success);font-size:10px">enabled</span>' : '<span style="color:var(--text-dead);font-size:10px">disabled</span>';
              const sid = 'mcpstatus-' + s.name.replace(/[^a-zA-Z0-9_-]/g, '_');
              return `<div style="padding:8px 0;border-bottom:1px solid var(--border-dim);display:flex;flex-wrap:wrap;align-items:center;gap:8px">
                <span style="min-width:70px"><span style="border:1px solid var(--border-normal);padding:2px 6px;font-size:9px;color:var(--text-muted)">${badge}</span></span>
                <span style="min-width:120px;color:var(--text-bright);font-size:12px">${n}</span>
                <span style="flex:1;min-width:160px;color:var(--text-muted);font-size:11px">${detail}</span>
                <div style="display:flex;align-items:center;gap:8px;margin-left:auto;flex-wrap:wrap">${enabled}
                  <button onclick='editMcpServer(${nj})' style="background:transparent;border:1px solid var(--border-normal);color:var(--text-bright);${btn}">EDIT</button>
                  <button onclick='testMcpServer(${nj})' style="background:transparent;border:1px solid var(--border-normal);color:var(--text-bright);${btn}">TEST</button>
                  <button onclick='deleteMcpServer(${nj})' class="btn-danger" style="${btn}">DELETE</button>
                </div>
                <span id="${sid}" class="ch-status" style="font-size:11px;color:var(--text-muted);flex-basis:100%"></span>
              </div>`;
            }).join('') || '<span style="color:var(--text-dead)">No MCP servers configured yet.</span>';
          } catch(e) {
            st.textContent = 'error'; st.style.color = 'var(--danger,#c05050)';
            document.getElementById('mcp-list').textContent = 'Failed to load: ' + e.message;
          }
        }

        let editingMcpName = null;

        function setMcpEditMode(name) {
          const nameInput = document.getElementById('mcp-name');
          const cancelBtn = document.getElementById('mcp-cancel-btn');
          const label = document.getElementById('mcp-form-label');
          if (name) {
            editingMcpName = name;
            nameInput.disabled = true;
            nameInput.style.opacity = '0.6';
            cancelBtn.style.display = '';
            label.textContent = 'Editing Server: ' + name;
          } else {
            editingMcpName = null;
            nameInput.disabled = false;
            nameInput.style.opacity = '';
            cancelBtn.style.display = 'none';
            label.textContent = 'Add New Server';
          }
        }

        async function editMcpServer(name) {
          const msg = document.getElementById('mcp-form-msg');
          msg.textContent = 'Loading…';
          try {
            const r = await fetch('/mcps/' + encodeURIComponent(name));
            if (!r.ok) { msg.textContent = 'Error: ' + (await r.text()); return; }
            const s = await r.json();
            setCustomSelectValue('mcp-transport-wrapper', s.transport);
            onMcpTransportChange();
            document.getElementById('mcp-name').value = s.name || '';
            document.getElementById('mcp-command').value = s.command || '';
            document.getElementById('mcp-args').value = (s.args || []).join('\n');
            document.getElementById('mcp-env').value = Object.entries(s.env || {}).map(([k, v]) => k + '=' + v).join('\n');
            document.getElementById('mcp-url').value = s.url || '';
            document.getElementById('mcp-enabled').checked = s.enabled;
            setMcpEditMode(s.name);
            msg.textContent = '';
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        function cancelMcpEdit() {
          setCustomSelectValue('mcp-transport-wrapper', '2');
          onMcpTransportChange();
          document.getElementById('mcp-name').value = '';
          document.getElementById('mcp-command').value = '';
          document.getElementById('mcp-args').value = '';
          document.getElementById('mcp-env').value = '';
          document.getElementById('mcp-url').value = '';
          document.getElementById('mcp-enabled').checked = true;
          document.getElementById('mcp-form-msg').textContent = '';
          setMcpEditMode(null);
        }

        function readMcpForm() {
          const transport = parseInt(document.getElementById('mcp-transport').value, 10);
          const name = document.getElementById('mcp-name').value.trim();
          const command = document.getElementById('mcp-command').value.trim();
          const args = document.getElementById('mcp-args').value.split('\n').map(x => x.trim()).filter(x => x);
          const env = {};
          document.getElementById('mcp-env').value.split('\n').forEach(line => {
            const eq = line.indexOf('=');
            if (eq > 0) env[line.slice(0, eq).trim()] = line.slice(eq + 1).trim();
          });
          const url = document.getElementById('mcp-url').value.trim();
          const enabled = document.getElementById('mcp-enabled').checked;
          return { transport, name, command, args, env, url, enabled };
        }

        async function saveMcpServer() {
          const msg = document.getElementById('mcp-form-msg');
          const { transport, name, command, args, env, url, enabled } = readMcpForm();
          const targetName = editingMcpName || name;
          if (!targetName) { msg.textContent = 'Name is required.'; return; }
          if (transport === 1 && !url) { msg.textContent = 'SSE server requires a URL.'; return; }
          if (transport !== 1 && !command) { msg.textContent = 'Command is required.'; return; }

          msg.textContent = 'Saving…';
          try {
            const r = await fetch('/mcps/' + encodeURIComponent(targetName), {
              method: 'PUT', headers: {'Content-Type':'application/json'},
              body: JSON.stringify({ transport, command, args, env: Object.keys(env).length ? env : null, url, enabled })
            });
            if (!r.ok) { msg.textContent = 'Error: ' + (await r.text()); return; }
            cancelMcpEdit();
            msg.textContent = '';
            await refreshMcpServers();
          } catch(e) { msg.textContent = 'Error: ' + e.message; }
        }

        async function deleteMcpServer(name) {
          if (!confirm('Delete MCP server "' + name + '"?')) return;
          await fetch('/mcps/' + encodeURIComponent(name), { method: 'DELETE' });
          await refreshMcpServers();
        }

        async function testMcpServer(name) {
          const sid = 'mcpstatus-' + name.replace(/[^a-zA-Z0-9_-]/g, '_');
          const el = document.getElementById(sid) || document.getElementById('mcp-status');
          el.style.color = 'var(--text-muted)';
          el.textContent = 'Probing…';
          try {
            const r = await fetch('/mcps/' + encodeURIComponent(name) + '/probe', { method: 'POST' });
            const d = await r.json();
            if (d.ok) {
              el.style.color = 'var(--success)';
              el.textContent = '✓ Connected in ' + (d.latencyMs ?? '?') + 'ms — ' + (d.toolCount ?? 0) + ' tool(s)';
            } else {
              el.style.color = 'var(--danger,#c05050)';
              el.textContent = '✕ ' + (d.error || 'probe failed');
            }
          } catch(e) {
            el.style.color = 'var(--danger,#c05050)';
            el.textContent = '✕ ' + e.message;
          }
        }

        async function testMcpFromForm() {
          const msg = document.getElementById('mcp-form-msg');
          const { transport, name, command, args, env, url, enabled } = readMcpForm();
          if (!name) { msg.textContent = 'Name is required.'; return; }
          if (transport === 1 && !url) { msg.textContent = 'SSE server requires a URL.'; return; }
          if (transport !== 1 && !command) { msg.textContent = 'Command is required.'; return; }

          msg.textContent = 'Probing…';
          try {
            const r = await fetch('/mcps/probe', {
              method: 'POST', headers: {'Content-Type':'application/json'},
              body: JSON.stringify({ transport, command, args, env: Object.keys(env).length ? env : null, url, enabled })
            });
            const d = await r.json();
            if (d.ok) {
              msg.style.color = 'var(--success)';
              msg.textContent = '✓ Connected in ' + (d.latencyMs ?? '?') + 'ms — ' + (d.toolCount ?? 0) + ' tool(s)';
            } else {
              msg.style.color = 'var(--danger,#c05050)';
              msg.textContent = '✕ ' + (d.error || 'probe failed');
            }
          } catch(e) {
            msg.style.color = 'var(--danger,#c05050)';
            msg.textContent = '✕ ' + e.message;
          }
        }

        // Probe a channel's endpoint from THIS node (POST /llm/probe). The URL is reached from the
        // bridge machine — the same path chat traffic takes — so misconfiguration is caught here.
        // apiKey is the unsaved key from the form; when testing before saving, keyRef won't resolve
        // yet, so we pass the typed key explicitly.
        async function probeUrl(url, keyRef, apiKey, msgEl) {
          if (!url) { msgEl.textContent = 'Enter a URL first.'; return; }
          msgEl.style.color = 'var(--text-muted)';
          msgEl.textContent = 'Probing ' + url + ' …';
          try {
            const r = await fetch('/llm/probe', {
              method: 'POST', headers: {'Content-Type':'application/json'},
              body: JSON.stringify({ url: url.replace(/\/$/, ''), keyRef: keyRef || null, apiKey: apiKey || null })
            });
            const d = await r.json();
            if (d.ok) {
              msgEl.style.color = 'var(--success)';
              msgEl.textContent = '✓ Connected in ' + (d.latencyMs ?? '?') + 'ms';
            } else {
              msgEl.style.color = 'var(--danger,#c05050)';
              msgEl.textContent = '✕ ' + (d.error || 'probe failed');
            }
          } catch(e) {
            msgEl.style.color = 'var(--danger,#c05050)';
            msgEl.textContent = '✕ ' + e.message;
          }
        }

        function testChannelUrl() {
          const name = document.getElementById('ch-name').value.trim();
          const key = document.getElementById('ch-key').value.trim();
          probeUrl(document.getElementById('ch-url').value.trim(), name, key, document.getElementById('channels-msg'));
        }

        function testChannel(name, url) {
          const sid = 'chstatus-' + name.replace(/[^a-zA-Z0-9_-]/g, '_');
          const el = document.getElementById(sid) || document.getElementById('channels-status');
          probeUrl(url, name, null, el);
        }

    """;
}
