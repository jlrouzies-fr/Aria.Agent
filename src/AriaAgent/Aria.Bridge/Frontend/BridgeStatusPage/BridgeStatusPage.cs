namespace Aria.Bridge;

// The bridge status page (served at http://127.0.0.1:5741/) used to be one 3500-line file — a
// single raw-string literal holding the whole HTML document. Split per nav menu for readability;
// each partial below owns one tab's markup (+ its JS, where the two aren't awkward to separate).
//
// Two constraints drove how the split was cut:
//  1. Panel markup order here (Overview, Soul, Channels, Mcp, Logs, Security, Data, Memory,
//     Endpoints, Telemetry, Terminal, Oauth) matches the original <div id="panel-XXX"> order in
//     the page body — cosmetic, but keeps a diff against the old file easy to eyeball.
//  2. Script order below does NOT match panel order — it matches the ORIGINAL <script> block's
//     order, which is load-bearing: the page defines `function esc(s)` twice (a simple version in
//     the Channels script, a DOM-escaping version in the Soul script) and JS function declarations
//     silently overwrite one another in declaration order — whichever is emitted LAST wins for the
//     rest of the script's lifetime. The original source has Channels before Soul, so Soul's esc()
//     is the one actually in effect; reordering these consts would flip that silently. Don't reorder
//     the Script* concatenation below without checking for cross-chunk name collisions first.
public static partial class BridgeStatusPage
{
    // Every piece below is its own raw string literal, and a raw string literal never includes the
    // line break right before its closing `"""` — that break was purely a delimiter, not content.
    // When this was one literal, that only cost the file's final trailing newline; split into N
    // literals, EVERY join point loses one. String.Join("\n", ...) puts each one back so the
    // concatenated result matches the original line-for-line.
    public static string Build() => string.Join("\n",
        Head, ShellOpen,
        PanelOverview, PanelSoul, PanelChannels, PanelMcp, PanelLogs, PanelSecurity,
        PanelData, PanelMemory, PanelEndpoints, PanelTelemetry, PanelTerminal, PanelOauth,
        ShellClose,
        ScriptCommonNav, ScriptOverview, ScriptSecurity, ScriptChannels, ScriptMcp,
        ScriptTelemetry, ScriptTerminal, ScriptSoul, ScriptDataAndMemory, ScriptLogs,
        ScriptAudit, ScriptOauth, ScriptStatusAndTooltips,
        ShellFinalClose);
}
