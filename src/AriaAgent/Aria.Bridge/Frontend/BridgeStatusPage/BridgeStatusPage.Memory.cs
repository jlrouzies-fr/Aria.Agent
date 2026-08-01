namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
// NOTE: this panel's JS (refreshMemoryConfig, refreshNoosphereBuiltin, buildNoosphereDropdown,
// saveNoosphereConfig, and the generic custom-select helpers it shares with the MCP transport picker)
// lives in BridgeStatusPage.Data.cs's ScriptDataAndMemory — it's interleaved there with Data-panel
// wipe actions in the original source, and splitting it further risked breaking JS declaration order
// (see ScriptSoul/ScriptChannels notes in BridgeStatusPage.cs about duplicate function names).
    internal const string PanelMemory = """
        <!-- ── MEMORY TAB ─────────────────────────────────────────────── -->
        <div id="panel-memory" style="display:none">
          <div class="section-head"><div class="section-title">// Memory</div></div>
          <div id="noosphere-runtime-error" style="display:none;font-size:11px;color:#e07070;border:1px solid rgba(208,64,64,0.55);background:rgba(208,64,64,0.10);padding:10px 12px;margin-bottom:14px;line-height:1.5"></div>
          <div class="card">
            <div class="card-header">// Built-in models</div>
            <div class="card-body" style="display:flex;flex-direction:column;gap:12px">
              <div style="font-size:11px;color:var(--text-dead)">
                Optional on-node models so Noosphere works without a third-party inference engine
                (LM Studio, Ollama, …). Pick an LFM extract GGUF (1.2B or 2.6B, Q4/Q5/Q6) plus MiniLM
                embeddings (~23&nbsp;MB). Each extract quant downloads separately; only the selected
                one loads into RAM on first Inscribe/Probe. Unload frees RAM (files stay on disk).
                When built-in is on, the channel pickers are hidden — turn it off to configure an
                external inference channel again.
              </div>
              <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">
                <input type="checkbox" id="noosphere-builtin-enabled" style="accent-color:var(--accent)">
                <label for="noosphere-builtin-enabled" style="font-size:12px;color:var(--text-normal);cursor:pointer">Use built-in Noosphere models</label>
                <button onclick="saveNoosphereBuiltinConfig()" style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);padding:4px 12px;cursor:pointer;font-family:monospace;font-size:10px;letter-spacing:.08em">▶ APPLY</button>
              </div>
              <label style="display:flex;align-items:flex-start;gap:8px;font-size:11px;color:var(--text-muted);cursor:pointer">
                <input type="checkbox" id="noosphere-builtin-license" style="accent-color:var(--accent);margin-top:2px">
                <span>I accept the LFM Open License for LiquidAI extract models (LFM2.5-1.2B-Instruct / LFM2-2.6B). Embeddings use Apache-2.0 MiniLM.</span>
              </label>
              <div id="noosphere-builtin-roles" style="display:flex;flex-direction:column;gap:10px"></div>
              <div id="noosphere-builtin-status" style="font-size:11px;color:var(--text-muted);min-height:16px"></div>
            </div>
          </div>
          <div class="card" id="noosphere-channels-card">
            <div class="card-header">// Noosphere Channels</div>
            <div class="card-body" style="display:flex;flex-direction:column;gap:14px">
              <div style="font-size:11px;color:var(--text-dead)">
                Noosphere uses your existing bridged channels for extraction (turning raw text into engrams)
                and optionally for embeddings (vector search). If you leave a field as <em>Auto</em>, it borrows
                the first available local channel. Disable embeddings to use keyword/graph probe only.
              </div>
              <div style="font-size:11px;color:#c09050;border:1px solid #5a4020;background:rgba(180,120,40,0.08);padding:8px 10px">
                ⚠ Prefer a plain instruct model for extraction — a "thinking"/reasoning model (e.g. a
                DeepSeek-R1 or Qwen3-thinking distill) will emit a long internal reasoning block before
                every answer, turning a trivial "extract facts from this note" task into a multi-minute
                generation and backing up the whole ingest queue behind it. Aria asks compatible servers
                to skip that reasoning step, but not every server honors the request — a non-thinking
                model sidesteps the problem entirely and extracts just as well for this task.
              </div>
              <div class="subsection-title" style="margin-bottom:0">Extraction</div>
              <div>
                <div class="metric-label" style="margin-bottom:6px">Extraction channel</div>
                <div class="custom-select" id="noosphere-extraction-wrapper" style="width:100%;max-width:360px">
                  <div class="custom-select-trigger" onclick="toggleCustomDropdown(this)">Auto — first local channel</div>
                  <div class="custom-select-options" style="display:none"></div>
                  <input type="hidden" id="noosphere-extraction" value="">
                </div>
              </div>
              <div>
                <div class="metric-label" style="margin-bottom:6px">Extraction model</div>
                <div style="font-size:11px;color:var(--text-dead);margin-bottom:6px">
                  The channel above is just a URL + key — it may serve several models. Pick the specific
                  instruct model to use for extraction; embedding-only models are hidden from this list.
                  Leave on Auto to fall back to the channel's first listed model.
                </div>
                <div class="custom-select" id="noosphere-extraction-model-wrapper" style="width:100%;max-width:360px">
                  <div class="custom-select-trigger" onclick="toggleCustomDropdown(this)">Auto — first listed model</div>
                  <div class="custom-select-options" style="display:none"></div>
                  <input type="hidden" id="noosphere-extraction-model" value="">
                </div>
                <div id="noosphere-extraction-model-warn" style="display:none;font-size:11px;color:#c09050;margin-top:6px"></div>
              </div>
              <div class="subsection-title" style="margin-bottom:0;margin-top:4px">Embeddings</div>
              <div>
                <div class="metric-label" style="margin-bottom:6px">Embeddings channel</div>
                <div class="custom-select" id="noosphere-embeddings-wrapper" style="width:100%;max-width:360px">
                  <div class="custom-select-trigger" onclick="toggleCustomDropdown(this)">Auto — first local channel</div>
                  <div class="custom-select-options" style="display:none"></div>
                  <input type="hidden" id="noosphere-embeddings" value="">
                </div>
              </div>
              <div>
                <div class="metric-label" style="margin-bottom:6px">Embeddings model</div>
                <div style="font-size:11px;color:var(--text-dead);margin-bottom:6px">
                  The channel above is just a URL + key — it may serve several models. This list is
                  filtered to the ones that look like embedding models (by name — e.g. anything with
                  "embed" in it) so a chat model can't be picked here by mistake. If none are detected,
                  every model on the channel is shown instead. Leave on Auto to fall back to the
                  channel's first listed model.
                </div>
                <div class="custom-select" id="noosphere-embeddings-model-wrapper" style="width:100%;max-width:360px">
                  <div class="custom-select-trigger" onclick="toggleCustomDropdown(this)">Auto — first listed model</div>
                  <div class="custom-select-options" style="display:none"></div>
                  <input type="hidden" id="noosphere-embeddings-model" value="">
                </div>
                <div id="noosphere-embeddings-model-warn" style="display:none;font-size:11px;color:#c09050;margin-top:6px"></div>
              </div>
              <div style="display:flex;align-items:center;gap:10px">
                <input type="checkbox" id="noosphere-embeddings-enabled" checked style="accent-color:var(--accent)">
                <label for="noosphere-embeddings-enabled" style="font-size:12px;color:var(--text-normal);cursor:pointer">Enable embeddings (vector probe)</label>
              </div>
              <div style="display:flex;gap:8px;flex-wrap:wrap">
                <button onclick="saveNoosphereConfig()" style="background:var(--bg-surface);border:1px solid var(--border-glow);color:var(--text-title);padding:6px 14px;cursor:pointer;font-family:monospace;font-size:11px;letter-spacing:.08em">▶ SAVE</button>
              </div>
              <div id="noosphere-config-msg" style="font-size:11px;color:var(--text-muted);min-height:16px"></div>
            </div>
          </div>
        </div>

    """;
}
