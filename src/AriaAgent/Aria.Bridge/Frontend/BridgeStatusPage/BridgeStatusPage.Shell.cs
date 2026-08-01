namespace Aria.Bridge;

public static partial class BridgeStatusPage
{
    internal const string Head = """
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>ARIA // BRIDGE</title>
      <link rel="icon" type="image/png" href="data:image/png;base64,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">
      <style>
        :root {
          --bg-base:       #252525;
          --bg-panel:      #2d2d2d;
          --bg-surface:    #353535;
          --border-dim:    #5a4040;
          --border-normal: #8a5252;
          --border-glow:   #e07070;
          --text-dead:     #9a8282;
          --text-muted:    #cc9898;
          --text-normal:   #e08888;
          --text-bright:   #f5a0a0;
          --text-title:    #f06060;
          --text-body:     #eed0d0;
          --glow-sm:       0 0 6px rgba(180,80,80,0.4);
          --glow-md:       0 0 12px rgba(180,80,80,0.45);
          --glow-lg:       0 0 28px rgba(180,80,80,0.4);
          --gold-bright:   #d4a020;
          --gold-soft:     #c0ae82;
          --success:       #55aa55;
        }
        *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
        html, body {
          height: 100%;
          background: var(--bg-base);
          color: var(--text-body);
          font-family: 'Consolas', 'Courier New', monospace;
          font-size: 13.5px;
          line-height: 1.5;
          overflow: hidden;
        }
        body {
          display: flex;
          flex-direction: column;
        }
        /* scanline overlay */
        body::after {
          content: '';
          position: fixed;
          inset: 0;
          background: repeating-linear-gradient(
            0deg, transparent, transparent 1px,
            rgba(0,0,0,0.06) 1px, rgba(0,0,0,0.06) 2px
          );
          pointer-events: none;
          z-index: 9999;
        }
        /* header */
        .header {
          flex-shrink: 0;
          z-index: 100;
          background: var(--bg-panel);
          border-bottom: 1px solid var(--border-glow);
          height: 36px;              /* kept in lockstep with the Aria.Web status bar */
          padding: 0 16px;
          box-sizing: border-box;
          display: flex;
          align-items: center;
          justify-content: space-between;
          gap: 12px;
          box-shadow: var(--glow-sm);
        }
        .header-left {
          display: flex;
          align-items: center;
          gap: 10px;
        }
        .header-status {
          display: flex;
          flex-direction: row;
          align-items: center;
          gap: 14px;
        }
        .header-status .status-text {
          font-size: 11px;
          letter-spacing: 0.1em;
        }
        .header-status-detail {
          font-size: 10px;
          color: var(--text-dead);
          letter-spacing: 0.06em;
        }
        .header-sigil {
          font-size: 22px;
          padding-right: 5px;
          color: var(--text-title);
          text-shadow: var(--glow-md);
        }
        .header-titles {
          display: flex;
          align-items: baseline;
          gap: 8px;
        }
        .header-title {
          font-size: 13px;
          font-weight: bold;
          color: var(--text-title);
          letter-spacing: 0.12em;
          text-shadow: var(--glow-sm);
        }
        .header-sub {
          font-size: 10px;
          color: var(--text-dead);
          letter-spacing: 0.06em;
        }
        .header-sub::before {
          content: "|";
          margin-right: 8px;
          color: var(--text-dead);
          opacity: 0.6;
        }
        /* connected-server pill, mirroring the server UI's .header-bridge badge */
        .server-badge {
          display: inline-flex;
          align-items: center;
          gap: 5px;
          padding: 3px 9px;
          font-size: 9px;
          letter-spacing: 0.12em;
          text-transform: uppercase;
          border: 1px solid var(--border-dim);
          border-radius: 2px;
          font-family: 'Consolas', 'Courier New', monospace;
          color: var(--text-dead);
          max-width: 260px;
          overflow: hidden;
          text-overflow: ellipsis;
          white-space: nowrap;
        }
        .server-badge.linked { color: var(--success); border-color: var(--success); }

        /* Stylized tooltip — a single shared #aria-tip element positioned by JS (cursor-following,
           viewport-clamped), mirroring the Aria.Web [data-tip] system. */
        [data-tip] { cursor: help; }
        #aria-tip {
          position: fixed;
          left: -9999px;
          top: -9999px;
          z-index: 9998;
          pointer-events: none;
          max-width: 280px;
          padding: 6px 10px;
          font-family: 'Consolas', 'Courier New', monospace;
          font-size: 10.5px;
          line-height: 1.5;
          letter-spacing: 0.03em;
          background: var(--bg-panel);
          border: 1px solid var(--gold-soft);
          border-radius: 2px;
          color: var(--text-normal);
          box-shadow: var(--glow-sm), 0 2px 8px rgba(0,0,0,0.6);
          white-space: normal;
          word-break: break-word;
          opacity: 0;
          transition: opacity 0.12s;
        }
        /* main content — full-width scroll container so the scrollbar sits at the viewport's
           right edge; the content itself is centered via the constrained children below. */
        .main {
          flex: 1;
          overflow-y: auto;
          width: 100%;
          padding: 0 20px 20px;
          display: flex;
          flex-direction: column;
          align-items: center;
          gap: 20px;
          scrollbar-width: thin;
          scrollbar-color: var(--border-normal) var(--bg-base);
        }
        /* Constrain + centre each section; the scroll container stays full-width. */
        .main > * {
          width: 100%;
          max-width: 680px;
        }
        .main::-webkit-scrollbar { width: 10px; }
        .main::-webkit-scrollbar-track { background: var(--bg-base); }
        .main::-webkit-scrollbar-thumb {
          background: var(--border-normal);
          border-radius: 5px;
          border: 2px solid var(--bg-base);
        }
        .main::-webkit-scrollbar-thumb:hover { background: var(--border-glow); }
        /* card */
        .card {
          background: var(--bg-panel);
          border: 1px solid var(--border-normal);
          border-radius: 3px;
          overflow: hidden;
          box-shadow: 0 1px 0 rgba(0,0,0,0.25);
          margin-bottom: 16px;
        }
        .card:last-child { margin-bottom: 0; }
        /* Gold-accented header bar so each block reads as a clearly-titled unit. */
        .card-header {
          background: var(--bg-surface);
          border-bottom: 1px solid var(--border-dim);
          border-left: 2px solid var(--gold-soft);
          padding: 9px 14px;
          font-size: 11.5px;
          color: var(--gold-soft);
          letter-spacing: 0.16em;
          text-transform: uppercase;
          font-weight: normal;
          text-shadow: 0 0 6px rgba(192, 174, 130, 0.25);
        }
        .card-body {
          padding: 14px 16px;
        }
        /* status indicator */
        .status-line {
          display: flex;
          align-items: center;
          gap: 10px;
          margin-bottom: 18px;
        }
        .dot {
          width: 10px;
          height: 10px;
          border-radius: 50%;
          background: var(--success);
          box-shadow: 0 0 8px var(--success);
          animation: pulse 2.4s ease-in-out infinite;
          flex-shrink: 0;
        }
        @keyframes pulse {
          0%, 100% { opacity: 1; box-shadow: 0 0 8px var(--success); }
          50%       { opacity: 0.65; box-shadow: 0 0 16px var(--success); }
        }
        .status-text {
          font-size: 13px;
          font-weight: bold;
          color: var(--success);
          letter-spacing: 0.1em;
          text-shadow: 0 0 8px rgba(85,170,85,0.5);
        }
        /* metrics grid — compact: more columns, tighter cells, smaller values */
        .metrics {
          display: grid;
          grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
          gap: 8px;
        }
        .metric {
          background: var(--bg-surface);
          border: 1px solid var(--border-dim);
          border-radius: 2px;
          padding: 7px 10px;
        }
        .metric-label {
          font-size: 10px;
          color: var(--text-dead);
          letter-spacing: 0.1em;
          text-transform: uppercase;
          margin-bottom: 2px;
        }
        .metric-value {
          font-size: 14px;
          color: var(--text-title);
          text-shadow: var(--glow-sm);
          letter-spacing: 0.03em;
        }
        /* endpoint table */
        table {
          width: 100%;
          border-collapse: collapse;
        }
        tr { border-bottom: 1px solid var(--border-dim); }
        tr:last-child { border-bottom: none; }
        td { padding: 7px 10px; vertical-align: top; }
        td:first-child {
          color: var(--text-muted);
          font-size: 11px;
          white-space: nowrap;
          width: 130px;
        }
        td:nth-child(2) {
          color: var(--text-normal);
          font-family: 'Consolas', monospace;
          font-size: 11px;
        }
        td:last-child {
          color: var(--text-dead);
          font-size: 11px;
        }
        .method {
          display: inline-block;
          padding: 1px 5px;
          border-radius: 2px;
          font-size: 10px;
          font-weight: bold;
          letter-spacing: 0.06em;
          margin-right: 4px;
          background: var(--bg-surface);
          border: 1px solid var(--border-dim);
          color: var(--text-muted);
        }
        .method.post   { border-color: var(--border-normal); color: var(--text-normal); }
        .method.get    { border-color: #406030; color: #80aa60; }
        .method.delete { border-color: #703030; color: #c05050; }
        /* endpoint groups */
        .endpoint-group {
          border-bottom: 1px solid var(--border-dim);
        }
        .endpoint-group:last-child {
          border-bottom: none;
        }
        .endpoint-group > summary {
          list-style: none;
          cursor: pointer;
          padding: 8px 14px;
          font-size: 11px;
          color: var(--text-muted);
          letter-spacing: 0.1em;
          text-transform: uppercase;
          background: var(--bg-surface);
          display: flex;
          align-items: center;
          gap: 8px;
        }
        .endpoint-group > summary::-webkit-details-marker { display: none; }
        .endpoint-group > summary::before {
          content: '+';
          display: inline-block;
          width: 12px;
          color: var(--text-dead);
          font-weight: bold;
        }
        .endpoint-group[open] > summary::before {
          content: '−';
        }
        .endpoint-group > summary:hover {
          color: var(--text-normal);
        }
        .endpoint-group table {
          width: 100%;
          border-collapse: collapse;
        }
        /* tab bar */
        /* Frozen at the top of the scroll region. Owns the top padding so it stays flush to the
           header when stuck; opaque background so scrolling content passes cleanly behind it. */
        .tab-bar {
          position: sticky;
          top: 0;
          z-index: 50;
          display: flex;
          justify-content: space-evenly;
          border-bottom: 1px solid var(--border-dim);
          background: var(--bg-base);
          padding-top: 30px;
          margin-bottom: 20px;
        }
        .tab-btn {
          background: none;
          border: none;
          border-bottom: 2px solid transparent;
          color: var(--text-dead);
          font-family: 'Consolas', 'Courier New', monospace;
          font-size: 10px;
          letter-spacing: 0.1em;
          text-transform: uppercase;
          padding: 8px 8px;
          cursor: pointer;
          margin-bottom: -1px;
          transition: color 0.15s, border-color 0.15s;
          white-space: nowrap;
        }
        .tab-btn:hover { color: var(--text-normal); }
        .tab-btn.active {
          color: var(--text-title);
          border-bottom-color: var(--border-glow);
        }
        /* wipe button */
        .btn-danger {
          background: var(--bg-surface);
          border: 1px solid #703030;
          color: #c05050;
          padding: 5px 12px;
          cursor: pointer;
          font-family: 'Consolas', monospace;
          font-size: 11px;
          letter-spacing: .08em;
          border-radius: 2px;
        }
        .btn-danger:hover { border-color: #c05050; color: #e07070; }
        /* log lines */
        .log-warn  { color: var(--text-title); }
        .log-error { color: #c05050; }
        .log-ok    { color: #80aa60; }
        /* custom confirm modal */
        #aria-confirm-overlay {
          display: none; position: fixed; inset: 0;
          background: rgba(0,0,0,.72); z-index: 9999;
          align-items: center; justify-content: center;
        }
        #aria-confirm-overlay.active { display: flex; }
        #aria-confirm-box {
          background: var(--bg-panel);
          border: 1px solid var(--border-glow);
          padding: 24px 28px 20px;
          max-width: 420px; width: 92%;
          box-shadow: 0 0 32px rgba(0,0,0,.6);
        }
        #aria-confirm-title {
          font-size: 11px; letter-spacing: 3px; text-transform: uppercase;
          color: var(--text-muted); margin-bottom: 12px;
        }
        #aria-confirm-msg {
          font-size: 12px; color: var(--text-normal); line-height: 1.65;
          margin-bottom: 20px; white-space: pre-wrap;
        }
        .aria-confirm-btns { display: flex; gap: 10px; justify-content: flex-end; }
        .aria-confirm-cancel {
          background: none; border: 1px solid var(--border-dim); color: var(--text-dead);
          font-family: 'Consolas', monospace; font-size: 10px; letter-spacing: 2px;
          text-transform: uppercase; padding: 5px 14px; cursor: pointer;
        }
        .aria-confirm-cancel:hover { border-color: var(--border-normal); color: var(--text-muted); }
        .aria-confirm-ok {
          background: none; border: 1px solid #703030; color: #c05050;
          font-family: 'Consolas', monospace; font-size: 10px; letter-spacing: 2px;
          text-transform: uppercase; padding: 5px 14px; cursor: pointer;
        }
        .aria-confirm-ok:hover { border-color: #c05050; color: #e07070; }
        .aria-confirm-ok.danger { border-color: #8b0000; color: #c02020; }
        .aria-confirm-ok.danger:hover { border-color: #c05050; color: #e04040; }
        .ch-status:empty { display: none; }

        /* ══════════════════════════════════════════════════════════════════
           v2 layout — left sidebar + content pane (mirrors the Aria.Web server)
           ══════════════════════════════════════════════════════════════════ */
        .layout {
          flex: 1;
          display: flex;
          min-height: 0;
          overflow: hidden;
        }
        .sidebar {
          flex: 0 0 210px;
          background: var(--bg-panel);
          border-right: 1px solid var(--border-dim);
          overflow-y: auto;
          padding: 16px 10px 24px;
          display: flex;
          flex-direction: column;
          gap: 4px;
          scrollbar-width: thin;
          scrollbar-color: var(--border-normal) var(--bg-panel);
        }
        .sidebar::-webkit-scrollbar { width: 8px; }
        .sidebar::-webkit-scrollbar-thumb { background: var(--border-dim); border-radius: 4px; }
        .nav-group { margin-top: 14px; }
        .nav-group-label {
          font-size: 12px;
          letter-spacing: 0.12em;
          text-transform: uppercase;
          color: var(--text-muted);
          padding: 2px 10px 8px;
        }
        .nav-item {
          display: block;
          width: 100%;
          text-align: left;
          background: none;
          border: none;
          border-left: 2px solid transparent;
          color: var(--text-dead);
          font-family: 'Consolas', 'Courier New', monospace;
          font-size: 12px;
          letter-spacing: 0.06em;
          padding: 7px 10px;
          cursor: pointer;
          transition: color 0.12s, background 0.12s, border-color 0.12s;
          border-radius: 2px;
        }
        .nav-item:hover { color: var(--text-normal); background: var(--bg-surface); }
        .nav-item.active {
          color: var(--text-title);
          background: var(--bg-surface);
          border-left-color: var(--border-glow);
          text-shadow: var(--glow-sm);
        }
        .nav-home {
          font-size: 13px;
          color: var(--text-muted);
          margin-bottom: 2px;
        }
        /* a small badge on nav items with outstanding onboarding steps —
           a softly-glowing amber disc that gently pulses to draw the eye */
        .nav-item .nav-badge {
          float: right;
          margin-top: 1px;
          width: 15px;
          height: 15px;
          display: inline-flex;
          align-items: center;
          justify-content: center;
          font-size: 10px;
          font-weight: 700;
          line-height: 1;
          color: var(--gold-bright);
          background: rgba(212, 160, 32, 0.12);
          border: 1px solid rgba(212, 160, 32, 0.55);
          border-radius: 50%;
          box-shadow: 0 0 5px rgba(212, 160, 32, 0.30);
          animation: navBadgePulse 2.6s ease-in-out infinite;
        }
        /* Runtime Noosphere extract/embed failure — red pulse, distinct from amber onboarding. */
        .nav-item .nav-badge.nav-badge-warn {
          color: #f07070;
          background: rgba(208, 64, 64, 0.16);
          border-color: rgba(208, 64, 64, 0.7);
          box-shadow: 0 0 6px rgba(208, 64, 64, 0.4);
          animation: navBadgeWarnPulse 1.15s ease-in-out infinite;
        }
        @keyframes navBadgePulse {
          0%, 100% { box-shadow: 0 0 4px rgba(212, 160, 32, 0.22); opacity: 0.9; }
          50%      { box-shadow: 0 0 9px rgba(212, 160, 32, 0.55); opacity: 1; }
        }
        @keyframes navBadgeWarnPulse {
          0%, 100% { box-shadow: 0 0 3px rgba(208, 64, 64, 0.25); opacity: 0.75; }
          50%      { box-shadow: 0 0 11px rgba(208, 64, 64, 0.75); opacity: 1; }
        }
        @media (prefers-reduced-motion: reduce) {
          .nav-item .nav-badge { animation: none; }
        }
        /* content pane reuses .main (below) but drops the old centering padding-top */
        .layout .main { padding-top: 18px; }
        .layout .main > * { max-width: 720px; }

        /* section heading shown at the top of each panel */
        .section-head {
          margin-bottom: 14px;
          padding-bottom: 8px;
          border-bottom: 1px solid var(--border-dim);
        }
        .section-title {
          font-size: 15px;
          color: var(--text-title);
          letter-spacing: 0.14em;
          text-shadow: var(--glow-sm);
          text-transform: uppercase;
          font-weight: normal;
        }
        .section-lead {
          font-size: 12px;
          color: var(--text-dead);
          line-height: 1.6;
          margin-bottom: 8px;
        }

        /* ── onboarding checklist ─────────────────────────────────────────── */
        .onboard-step {
          display: flex;
          align-items: flex-start;
          gap: 12px;
          padding: 12px 0;
          border-bottom: 1px solid var(--border-dim);
        }
        .onboard-step:last-child { border-bottom: none; }
        .step-num {
          flex: 0 0 26px;
          height: 26px;
          border-radius: 50%;
          border: 1px solid var(--border-normal);
          display: flex;
          align-items: center;
          justify-content: center;
          font-size: 12px;
          color: var(--text-muted);
        }
        .onboard-step.done .step-num {
          border-color: var(--success);
          color: var(--success);
          box-shadow: 0 0 8px rgba(85,170,85,0.4);
        }
        .onboard-step.next .step-num {
          border-color: var(--border-glow);
          color: var(--text-title);
          box-shadow: var(--glow-sm);
        }
        .step-body { flex: 1; min-width: 0; }
        .step-title { font-size: 13px; color: var(--text-bright); }
        .onboard-step.done .step-title { color: var(--text-muted); }
        .step-sub { font-size: 11px; color: var(--text-dead); margin-top: 2px; }
        .onboard-optional-label {
          font-size: 10px;
          letter-spacing: 0.14em;
          text-transform: uppercase;
          color: var(--text-dead);
          margin: 16px 0 2px;
        }
        .onboard-step.optional .step-num {
          border-style: dashed;
          color: var(--text-dead);
          box-shadow: none;
        }
        .onboard-step.optional.done .step-num {
          border-style: solid;
          border-color: var(--success);
          color: var(--success);
        }
        .onboard-step.optional .step-title { color: var(--text-muted); }

        /* Terminal project editor rows */
        .proj-row {
          display: grid;
          grid-template-columns: 150px 1fr 1fr auto;
          gap: 8px;
          align-items: center;
          margin-bottom: 8px;
        }
        .proj-row .proj-remove {
          background: transparent;
          border: 1px solid var(--border-dim);
          color: var(--text-dead);
          cursor: pointer;
          border-radius: 2px;
          padding: 7px 10px;
          font-family: 'Consolas', 'Courier New', monospace;
          font-size: 12px;
          line-height: 1;
        }
        .proj-row .proj-remove:hover { border-color: #8b0000; color: #c05050; }
        .proj-empty { font-size: 11px; color: var(--text-dead); padding: 2px 0 8px; }
        @media (max-width: 640px) { .proj-row { grid-template-columns: 1fr; } }

        /* capability toggle row */
        .cap-row { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
        .cap-light { font-size: 12.5px; letter-spacing: 0.06em; }
        .cap-light.on  { color: var(--success); text-shadow: 0 0 8px rgba(85,170,85,0.4); }
        .cap-light.off { color: var(--text-dead); }
        .cap-row .btn { margin-left: auto; }

        /* ── shared control system (fixes the ad-hoc inline styling) ──────── */
        .btn {
          display: inline-flex;
          align-items: center;
          gap: 6px;
          background: var(--bg-surface);
          border: 1px solid var(--border-normal);
          color: var(--text-bright);
          font-family: 'Consolas', 'Courier New', monospace;
          font-size: 11px;
          letter-spacing: 0.08em;
          padding: 6px 14px;
          cursor: pointer;
          border-radius: 2px;
          transition: border-color 0.12s, color 0.12s, background 0.12s;
        }
        .btn:hover { border-color: var(--border-glow); color: var(--text-title); }
        .btn.primary { border-color: var(--border-glow); color: var(--text-title); }
        .btn.primary:hover { background: rgba(224,112,112,0.08); }
        .btn.ghost { background: transparent; color: var(--text-muted); }
        .btn.ghost:hover { border-color: var(--border-normal); color: var(--text-bright); }
        .btn.sm { padding: 4px 10px; font-size: 10px; }
        /* .btn-danger already defined above; align its metrics with .btn */
        .btn-danger { border-radius: 2px; letter-spacing: 0.08em; }

        .field-label {
          font-size: 10px;
          color: var(--text-dead);
          letter-spacing: 0.1em;
          text-transform: uppercase;
          margin-bottom: 5px;
        }
        /* Dashed, tinted callout for a standalone lead-in note ahead of a group of .card
           blocks — deliberately lighter than a .card so it doesn't read as another block. */
        .info-callout {
          border: 1px dashed var(--border-dim);
          background: rgba(192, 174, 130, 0.05);
          padding: 10px 14px;
          margin-bottom: 16px;
          font-size: 11.5px;
          color: var(--text-dead);
          line-height: 1.6;
        }
        /* Sub-header used to separate a list of existing items from an add/edit form
           within the same card, so the two zones read as distinct sections. */
        .subsection-title {
          font-size: 11px;
          color: var(--gold-soft);
          letter-spacing: 0.12em;
          text-transform: uppercase;
          border-left: 2px solid var(--gold-soft);
          padding-left: 8px;
          margin-bottom: 10px;
        }
        /* normalize every text control so the raw ones stop looking unstyled */
        input[type="text"], input[type="password"], input[type="number"], input:not([type]),
        textarea, select {
          background: var(--bg-surface);
          border: 1px solid var(--border-normal);
          color: var(--text-bright);
          font-family: 'Consolas', 'Courier New', monospace;
          font-size: 12.5px;
          padding: 7px 9px;
          border-radius: 2px;
          outline: none;
          transition: border-color 0.12s;
        }
        input::placeholder, textarea::placeholder { color: var(--text-dead); }
        input:focus, textarea:focus, select:focus { border-color: var(--border-glow); }
        textarea { resize: vertical; line-height: 1.5; }
        select {
          appearance: none;
          -webkit-appearance: none;
          padding-right: 30px;
          /* two lines forming a clear ▼ chevron, in gold so the control reads as styled */
          background-image:
            linear-gradient(45deg,  transparent 50%, var(--gold-soft) 50%),
            linear-gradient(135deg, var(--gold-soft) 50%, transparent 50%);
          background-position: calc(100% - 16px) 52%, calc(100% - 11px) 52%;
          background-size: 6px 6px, 6px 6px;
          background-repeat: no-repeat;
          cursor: pointer;
          letter-spacing: 0.04em;
        }
        select:hover { border-color: var(--border-glow); }
        option,
        select option {
          background-color: var(--bg-panel) !important;
          color: var(--text-bright) !important;
          font-family: 'Consolas', 'Courier New', monospace !important;
          font-size: 12.5px !important;
        }
        option:hover,
        select option:hover,
        option:checked,
        select option:checked {
          background-color: var(--bg-surface) !important;
          color: var(--text-bright) !important;
        }

        /* macOS-native <select> dropdowns ignore CSS on <option>, so bridge-themed dropdowns are
           rendered with divs instead. The hidden input carries the real value. */
        .custom-select { position: relative; }
        .custom-select-trigger {
          background: var(--bg-surface);
          border: 1px solid var(--border-normal);
          color: var(--text-bright);
          font-family: 'Consolas', 'Courier New', monospace;
          font-size: 12.5px;
          padding: 7px 30px 7px 9px;
          border-radius: 2px;
          cursor: pointer;
          letter-spacing: 0.04em;
          white-space: nowrap;
          overflow: hidden;
          text-overflow: ellipsis;
          /* same ▼ chevron used by native selects */
          background-image:
            linear-gradient(45deg,  transparent 50%, var(--gold-soft) 50%),
            linear-gradient(135deg, var(--gold-soft) 50%, transparent 50%);
          background-position: calc(100% - 16px) 52%, calc(100% - 11px) 52%;
          background-size: 6px 6px, 6px 6px;
          background-repeat: no-repeat;
        }
        .custom-select-trigger:hover { border-color: var(--border-glow); }
        .custom-select-options {
          position: fixed;
          z-index: 1000;
          background: var(--bg-panel);
          border: 1px solid var(--border-normal);
          border-radius: 2px;
          overflow-y: auto;
          box-shadow: 0 4px 12px rgba(0,0,0,0.35);
        }
        .custom-select-option {
          padding: 7px 9px;
          font-family: 'Consolas', 'Courier New', monospace;
          font-size: 12.5px;
          color: var(--text-bright);
          cursor: pointer;
          white-space: nowrap;
          overflow: hidden;
          text-overflow: ellipsis;
        }
        .custom-select-option:hover { background: var(--bg-surface); color: var(--text-bright); }
        .custom-select-option.selected { background: var(--border-normal); color: var(--text-bright); }
        input[type="checkbox"] {
          appearance: none;
          -webkit-appearance: none;
          width: 15px;
          height: 15px;
          border: 1px solid var(--border-normal);
          border-radius: 2px;
          background: var(--bg-surface);
          cursor: pointer;
          position: relative;
          flex: 0 0 auto;
          padding: 0;
        }
        input[type="checkbox"]:checked {
          border-color: var(--border-glow);
          background: rgba(224,112,112,0.15);
        }
        input[type="checkbox"]:checked::after {
          content: '✓';
          position: absolute;
          inset: 0;
          display: flex;
          align-items: center;
          justify-content: center;
          font-size: 11px;
          color: var(--text-title);
        }

        /* responsive: collapse the sidebar to a horizontal strip on narrow screens */
        @media (max-width: 720px) {
          .layout { flex-direction: column; overflow: visible; }
          .sidebar {
            flex: 0 0 auto;
            flex-direction: row;
            flex-wrap: wrap;
            border-right: none;
            border-bottom: 1px solid var(--border-dim);
            padding: 8px;
          }
          .nav-group { margin-top: 0; display: flex; flex-wrap: wrap; align-items: center; }
          .nav-group-label { display: none; }
          .nav-item { width: auto; border-left: none; border-bottom: 2px solid transparent; }
          .nav-item.active { border-left: none; border-bottom-color: var(--border-glow); }
        }
      </style>
    </head>
    """;

    internal const string ShellOpen = """
    <body>
      <!-- custom styled confirmation modal -->
      <div id="aria-confirm-overlay">
        <div id="aria-confirm-box">
          <div id="aria-confirm-title">// CONFIRM ACTION</div>
          <div id="aria-confirm-msg"></div>
          <div class="aria-confirm-btns">
            <button id="aria-confirm-cancel" class="aria-confirm-cancel">CANCEL</button>
            <button id="aria-confirm-ok" class="aria-confirm-ok">CONFIRM</button>
          </div>
        </div>
      </div>
      <div id="aria-tip"></div>
      <div class="header">
        <div class="header-left">
          <div class="header-sigil">⚙</div>
          <div class="header-titles">
            <div class="header-title">ARIA // BRIDGE</div>
            <div class="header-sub">MECHANICUS LOCAL PROCESS RELAY — localhost:5741</div>
          </div>
        </div>
        <div class="header-status">
          <span class="header-status-detail"><span id="val-version">—</span> · <span id="val-uptime">—</span></span>
          <span class="server-badge" id="header-server-url">Not linked</span>
          <div style="display:flex;align-items:center;gap:6px">
            <div class="dot"></div>
            <span class="status-text">OPERATIONAL</span>
          </div>
        </div>
      </div>
      <div class="layout">
        <nav class="sidebar">
          <button class="nav-item nav-home active" data-section="overview" onclick="showSection('overview',this)">◈ Overview</button>
          <div class="nav-group">
            <div class="nav-group-label">// Identity</div>
            <button class="nav-item" data-section="soul" onclick="showSection('soul',this)">Soul</button>
          </div>
          <div class="nav-group">
            <div class="nav-group-label">// Connect</div>
            <button class="nav-item" data-section="channels" onclick="showSection('channels',this)">Channels</button>
            <button class="nav-item" data-section="memory" onclick="showSection('memory',this)">Memory</button>
            <button class="nav-item" data-section="mcp" onclick="showSection('mcp',this)">Tools / MCP</button>
            <button class="nav-item" data-section="oauth" onclick="showSection('oauth',this)">OAuth</button>
          </div>
          <div class="nav-group">
            <div class="nav-group-label">// System</div>
            <button class="nav-item" data-section="terminal" onclick="showSection('terminal',this)">Terminal / Projects</button>
            <button class="nav-item" data-section="telemetry" onclick="showSection('telemetry',this)">Telemetry</button>
            <button class="nav-item" data-section="logs" onclick="showSection('logs',this)">Logs</button>
            <button class="nav-item" data-section="security" onclick="showSection('security',this)">Security</button>
            <button class="nav-item" data-section="data" onclick="showSection('data',this)">Data</button>
            <button class="nav-item" data-section="endpoints" onclick="showSection('endpoints',this)">Endpoints</button>
          </div>
        </nav>
        <div class="main">

    """;

    internal const string ShellClose = """

        </div>
      </div>
    """;

    internal const string ShellFinalClose = """
    </body>
    </html>
    """;
}
