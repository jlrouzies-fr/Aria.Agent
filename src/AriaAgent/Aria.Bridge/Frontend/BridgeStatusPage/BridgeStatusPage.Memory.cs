namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
// NOTE: this panel's JS (refreshMemoryConfig, buildNoosphereDropdown, saveNoosphereConfig,
// and the generic custom-select helpers it shares with the MCP transport picker) lives in
// BridgeStatusPage.Data.cs's ScriptDataAndMemory — it's interleaved there with Data-panel wipe
// actions in the original source, and splitting it further risked breaking JS declaration order
// (see ScriptSoul/ScriptChannels notes in BridgeStatusPage.cs about duplicate function names).
    internal const string PanelMemory = """
        <!-- ── MEMORY TAB ─────────────────────────────────────────────── -->
        <div id="panel-memory" style="display:none">
          <div class="section-head"><div class="section-title">// Memory</div></div>
          <div class="card">
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
