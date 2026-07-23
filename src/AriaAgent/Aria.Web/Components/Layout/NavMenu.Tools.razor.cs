using System.Text.Json;
using Aria.Agent;
using Aria.Harness.Core;
using Aria.Harness.Governance;
using Aria.Tools;
using Aria.Web.Data;
using Aria.Web.Services;
using Aria.Web.Services.Memory;
using Aria.Web.Services.Tool;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Layout;

public partial class NavMenu
{
    internal List<BridgeMcpInfo> _bridgeMcpServers = [];

    // ── Agent governance ──────────────────────────────────────────────────────
    internal static readonly GovernanceMode[] GovernanceModes =
        [GovernanceMode.Off, GovernanceMode.Balanced, GovernanceMode.Coding, GovernanceMode.Plan,
         GovernanceMode.Strict, GovernanceMode.Paranoid];

    internal static string GovernanceBlurb(GovernanceMode m) => m switch
    {
        GovernanceMode.Off      => "No restraint — every tool call runs unchecked.",
        GovernanceMode.Balanced => "Budgets + loop guard; out-of-scope asks first.",
        GovernanceMode.Coding   => "Roomy budgets for real refactors; out-of-scope asks first.",
        GovernanceMode.Plan     => "Read-only exploration — mutations blocked; present the plan.",
        GovernanceMode.Strict   => "Tight budgets, scope lock, mutations need approval.",
        GovernanceMode.Paranoid => "Strict + high-stakes acts need a node Seal.",
        _                       => ""
    };

    internal async Task SetGovernanceModeAsync(GovernanceMode mode)
    {
        if (SessionState.CurrentUser == null) return;
        SessionState.Governance = mode;
        await ToolService.SaveGovernanceModeAsync(SessionState.CurrentUser.Id, mode);
        StateHasChanged();
    }

    internal bool _govHelpOpen;
    internal void OpenGovHelp()  { _govHelpOpen = true;  StateHasChanged(); }
    internal void CloseGovHelp() { _govHelpOpen = false; StateHasChanged(); }

    // ── Auto-memory ───────────────────────────────────────────────────────────
    internal static readonly AutoMemoryMode[] AutoMemoryModes =
        [AutoMemoryMode.Off, AutoMemoryMode.ModelAuto, AutoMemoryMode.Regular, AutoMemoryMode.Always];

    internal static string AutoMemoryBlurb(AutoMemoryMode m) => m switch
    {
        AutoMemoryMode.Off       => "Only when you explicitly ask — the model won't inscribe on its own.",
        AutoMemoryMode.ModelAuto => "The model decides what's worth remembering (default).",
        AutoMemoryMode.Regular   => "A batch of recent turns is auto-inscribed every N exchanges.",
        AutoMemoryMode.Always    => "Every single turn is auto-inscribed — heaviest extraction load.",
        _                        => ""
    };

    internal static string AutoMemoryDetail(AutoMemoryMode m) => m switch
    {
        AutoMemoryMode.Off       => "No harness-triggered inscribe. The Inscribe tool stays available for explicit requests like \"remember that…\".",
        AutoMemoryMode.ModelAuto => "No harness-triggered inscribe either — but the model is free (and encouraged by the tool description) to call Inscribe on its own judgment whenever it decides something is worth preserving.",
        AutoMemoryMode.Regular   => "The harness buffers each turn's content and, every N exchanges, sends the whole batch through the extraction pipeline in one Inscribe call — so nothing is skipped, it's just periodic.",
        AutoMemoryMode.Always    => "The harness sends every user message + assistant reply through the extraction pipeline immediately after each turn. Most thorough, but triggers an LLM extraction call (and an embedding call, if configured) on every single message — watch token/compute usage on remote or metered channels.",
        _                        => ""
    };

    internal async Task SetAutoMemoryModeAsync(AutoMemoryMode mode)
    {
        if (SessionState.CurrentUser == null) return;
        SessionState.AutoMemory = mode;
        await ToolService.SaveAutoMemorySettingsAsync(SessionState.CurrentUser.Id, mode, SessionState.AutoMemoryInterval);
        StateHasChanged();
    }

    internal async Task SetAutoMemoryIntervalAsync(int interval)
    {
        if (SessionState.CurrentUser == null) return;
        interval = Math.Clamp(interval, 1, 50);
        SessionState.AutoMemoryInterval = interval;
        await ToolService.SaveAutoMemorySettingsAsync(SessionState.CurrentUser.Id, SessionState.AutoMemory, interval);
        StateHasChanged();
    }

    // ── Recall scope (single-node vs cross-node memory recall) ──────────────────
    internal static readonly RecallScope[] RecallScopes = [RecallScope.ThisNode, RecallScope.AllNodes];

    internal static string RecallScopeLabel(RecallScope s) => s switch
    {
        RecallScope.ThisNode => "THIS NODE",
        RecallScope.AllNodes => "ALL NODES",
        _                    => s.ToString().ToUpperInvariant()
    };

    internal static string RecallScopeBlurb(RecallScope s) => s switch
    {
        RecallScope.ThisNode => "Recall reads only the memory on the node running the model (default).",
        RecallScope.AllNodes => "Recall fans out to every connected node and merges what each remembers.",
        _                    => ""
    };

    /// <summary>True when the current soul has more than one node connected — the only situation in
    /// which recall scope actually changes behaviour.</summary>
    internal bool MultipleNodesConnected =>
        SessionState.CurrentUser != null
        && BridgeRegistry.GetNodes(SessionState.CurrentUser.Id.ToString()).Count > 1;

    internal async Task SetRecallScopeAsync(RecallScope scope)
    {
        if (SessionState.CurrentUser == null) return;
        SessionState.RecallScope = scope;
        await ToolService.SaveRecallScopeAsync(SessionState.CurrentUser.Id, scope);
        StateHasChanged();
    }

    internal bool _memGovHelpOpen;
    internal void OpenMemGovHelp()  { _memGovHelpOpen = true;  StateHasChanged(); }
    internal void CloseMemGovHelp() { _memGovHelpOpen = false; StateHasChanged(); }

    // Per-mode detail rows for the help modal — pulled from the real policy so the numbers can't drift.
    internal static (string Calls, string Reads, string Scope, string Mutations, string Seal) GovernanceDetail(GovernanceMode m)
    {
        if (m == GovernanceMode.Off)
            return ("unlimited", "unlimited", "no limit", "run freely", "never");

        var p = GovernancePolicy.FromMode(m);
        string scope = p.Scope switch
        {
            ScopeEnforcement.Block   => "blocked outside scope",
            ScopeEnforcement.Approve => "ask outside scope",
            _                        => "no limit"
        };
        return (
            $"{p.MaxToolCallsPerTurn} / turn",
            $"{p.MaxFileReadsPerTurn} / turn",
            scope,
            p.BlockMutations ? "blocked" : p.ApproveMutations ? "ask first" : "run freely",
            p.SealHighStakes ? "node Seal" : "—");
    }

    // Tool modal state
    internal ToolDefinition?              _modalTool;
    internal readonly Dictionary<string, string> _editBuffer = new();
    internal bool                         _modalSaved;
    internal List<(string Name, string Path, string Description, string? NodeId, string? Platform)> _pathListEntries = new();

    // OAuth connection status shown inside tool modals
    internal bool    _oauthConnected;
    internal string? _oauthEmail;

    // OAuth connection status per tool — drives sidebar indicators
    internal readonly Dictionary<string, bool> _oauthConnectedTools = new();

    // Bridge status
    internal bool _bridgeOnline   = false;
    internal bool _bridgeChecking = false;

    // Terminal capability status reported by the bridge (separate from the server-side tool toggle)
    internal bool _terminalEnabledOnBridge;
    internal bool _terminalStatusChecking;

    // Bridge-authoritative Terminal config displayed read-only in the tool modal.
    internal List<TerminalProject> _terminalProjects = [];
    internal bool _terminalProjectsRefreshing;
    internal string[] _terminalAllowedPaths = [];
    internal string[] _terminalBlockedCommands = [];
    internal bool _terminalConfigRefreshing;

    // Tab state for the Terminal tool modal: "projects" | "terminal"
    internal string _terminalModalTab = "projects";

    internal static readonly string[] OAuthToolIds = ["graph_email", "graph_calendar", "google_email", "google_calendar"];

    [JSInvokable]
    public async Task OnOAuthConnected(string toolId)
    {
        await RefreshOAuthStatusAsync();
        _activePanel = "tools";
        var def = ToolRegistry.Get(toolId);
        if (def is not null)
            await OpenModalAsync(def);
        await InvokeAsync(StateHasChanged);
    }

    internal async Task<(bool Connected, string? Email)> GetBridgeOAuthStatusAsync(string provider)
    {
        var userId = SessionState.CurrentUser?.Id.ToString();
        if (string.IsNullOrEmpty(userId)) return (false, null);

        try
        {
            var result = await BridgeRegistry.SendLocalRestAsync(userId, "GET", $"/oauth/{provider}/status");
            if (result is null || result.Value.StatusCode != 200 || string.IsNullOrEmpty(result.Value.Body))
                return (false, null);

            using var doc = JsonDocument.Parse(result.Value.Body);
            var root = doc.RootElement;
            var connected = root.TryGetProperty("connected", out var c) && c.GetBoolean();
            var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
            return (connected, email);
        }
        catch
        {
            return (false, null);
        }
    }

    internal async Task RefreshOAuthStatusAsync()
    {
        _oauthConnectedTools.Clear();
        if (SessionState.CurrentUser == null) return;

        foreach (var toolId in OAuthToolIds)
        {
            if (!SessionState.IsToolEnabled(toolId)) continue;

            var provider = OAuthProvider(toolId)!;
            var (connected, _) = await GetBridgeOAuthStatusAsync(provider);
            _oauthConnectedTools[toolId] = connected;
        }
    }

    // ── Tool toggles ──────────────────────────────────────────────────────

    internal async Task ToggleTool(ToolDefinition def)
    {
        if (SessionState.CurrentUser == null) return;
        if (def.Id == "mcp" && !AgentService.HasMcpEnabled && _bridgeMcpServers.Count == 0) return;

        var newEnabled = !SessionState.IsToolEnabled(def.Id);

        if (newEnabled && !await IsToolReadyAsync(def))
        {
            await OpenModalAsync(def);
            return;
        }

        var cfg = SessionState.GetToolConfig(def.Id);
        SessionState.SetToolState(def.Id, newEnabled, cfg);
        await ToolService.SaveToolStateAsync(SessionState.CurrentUser.Id, def.Id, newEnabled, cfg);
        SessionState.NotifyToolSettingsChanged();
    }

    internal async Task<bool> IsToolReadyAsync(ToolDefinition def)
    {
        var cfg = SessionState.GetToolConfig(def.Id);
        if (def.HasConfig && def.ConfigFields.Any(f => f.Required && string.IsNullOrEmpty(cfg.GetValueOrDefault(f.Key, ""))))
            return false;

        var provider = OAuthProvider(def.Id);
        if (provider is not null)
        {
            if (SessionState.CurrentUser is null) return false;
            var (connected, _) = await GetBridgeOAuthStatusAsync(provider);
            return connected;
        }

        return true;
    }

    // ── Tool modal ────────────────────────────────────────────────────────

    internal void OpenModal(ToolDefinition def) => _ = OpenModalAsync(def);

    internal void OpenBridgeOAuthPage(string provider)
        => _ = JS.InvokeVoidAsync("open", $"http://localhost:5741/oauth/{provider}/connect", "aria_oauth");

    internal async Task CheckOAuthConnectionAsync()
    {
        if (_modalTool is null) return;
        var provider = OAuthProvider(_modalTool.Id);
        if (provider is null) return;
        var (connected, email) = await GetBridgeOAuthStatusAsync(provider);
        _oauthConnected = connected;
        _oauthEmail     = email;
    }

    internal async Task OpenModalAsync(ToolDefinition def)
    {
        _modalTool      = def;
        _modalSaved     = false;
        _oauthConnected = false;
        _oauthEmail     = null;
        _terminalEnabledOnBridge = false;
        _terminalStatusChecking  = false;
        _terminalModalTab        = "projects";
        _editBuffer.Clear();

        var cfg = SessionState.GetToolConfig(def.Id);
        foreach (var kv in cfg) _editBuffer[kv.Key] = kv.Value;

        _pathListEntries.Clear();

        if (SessionState.CurrentUser != null)
        {
            var provider = OAuthProvider(def.Id);
            if (provider is not null)
            {
                var (connected, email) = await GetBridgeOAuthStatusAsync(provider);
                _oauthConnected = connected;
                _oauthEmail     = email;
            }
        }

        StateHasChanged();

        if (def.Id == "terminal" && SessionState.CurrentUser != null)
        {
            _ = CheckTerminalStatusAsync();
            _ = RefreshTerminalProjectsAsync();
            _ = RefreshTerminalConfigAsync();
        }

        if (def.Id == "mcp" && SessionState.CurrentUser != null)
            _ = CheckBridgeStatusAsync();
    }

    internal async Task CheckTerminalStatusAsync()
    {
        if (SessionState.CurrentUser == null) return;
        _terminalStatusChecking = true;
        StateHasChanged();
        try
        {
            _terminalEnabledOnBridge = await TerminalClient.IsTerminalEnabledAsync(SessionState.CurrentUser.Id.ToString());
        }
        catch
        {
            _terminalEnabledOnBridge = false;
        }
        _terminalStatusChecking = false;
        StateHasChanged();
    }

    internal async Task RefreshTerminalProjectsAsync()
    {
        if (SessionState.CurrentUser == null) return;
        _terminalProjectsRefreshing = true;
        StateHasChanged();
        try
        {
            _terminalProjects = await TerminalClient.GetAllProjectsAsync(SessionState.CurrentUser.Id.ToString());
            SessionState.SetProjects(_terminalProjects);
        }
        catch
        {
            _terminalProjects = [];
        }
        _terminalProjectsRefreshing = false;
        StateHasChanged();
    }

    internal async Task RefreshTerminalConfigAsync()
    {
        if (SessionState.CurrentUser == null) return;
        _terminalConfigRefreshing = true;
        StateHasChanged();
        try
        {
            var (allowed, blocked) = await TerminalClient.GetConfigAsync(SessionState.CurrentUser.Id.ToString());
            _terminalAllowedPaths = allowed;
            _terminalBlockedCommands = blocked;
        }
        catch
        {
            _terminalAllowedPaths = [];
            _terminalBlockedCommands = [];
        }
        _terminalConfigRefreshing = false;
        StateHasChanged();
    }

    internal static string? OAuthProvider(string toolId) => toolId switch
    {
        "graph_email" or "graph_calendar"   => "microsoft",
        "google_email" or "google_calendar" => "google",
        _ => null
    };

    internal void CloseModal() => _ = CloseModalAsync();

    internal async Task CloseModalAsync()
    {
        _modalTool  = null;
        _modalSaved = false;
        _editBuffer.Clear();
        _pathListEntries.Clear();
        await RefreshOAuthStatusAsync();
        StateHasChanged();
    }

    internal void OnFieldInput(string key, string value) => _editBuffer[key] = value;

    internal void AddPathEntry()
    {
        _pathListEntries.Add(("", "", "", null, null));
        SyncPathList();
    }

    internal void RemovePathEntry(int idx)
    {
        _pathListEntries.RemoveAt(idx);
        SyncPathList();
    }

    internal void UpdatePathEntryName(int idx, string value)
    {
        var (_, p, d, n, pl) = _pathListEntries[idx];
        _pathListEntries[idx] = (value, p, d, n, pl);
        SyncPathList();
    }

    internal void UpdatePathEntryPath(int idx, string value)
    {
        var (n, _, d, nid, pl) = _pathListEntries[idx];
        _pathListEntries[idx] = (n, value, d, nid, pl);
        SyncPathList();
    }

    internal void UpdatePathEntryDesc(int idx, string value)
    {
        var (n, p, _, nid, pl) = _pathListEntries[idx];
        _pathListEntries[idx] = (n, p, value, nid, pl);
        SyncPathList();
    }

    internal void UpdatePathEntryNodeId(int idx, string? value)
    {
        var platform = string.IsNullOrEmpty(value) || SessionState.CurrentUser == null
            ? null
            : BridgeRegistry.GetNodes(SessionState.CurrentUser.Id.ToString())
                .FirstOrDefault(n => n.NodeId == value)?.Platform;
        var (n, p, d, _, _) = _pathListEntries[idx];
        _pathListEntries[idx] = (n, p, d, value, platform);
        SyncPathList();
    }

    internal void UpdatePathEntryPlatform(int idx, string? value)
    {
        var (n, p, d, nid, _) = _pathListEntries[idx];
        _pathListEntries[idx] = (n, p, d, nid, value);
        SyncPathList();
    }

    internal void SyncPathList()
    {
        _editBuffer["AllowedPaths"] = JsonSerializer.Serialize(
            _pathListEntries.Select(e => new
            {
                name = e.Name,
                path = e.Path,
                description = e.Description,
                nodeId = e.NodeId,
                platform = e.Platform
            }));
    }

    internal async Task ApplyModal()
    {
        if (_modalTool == null || SessionState.CurrentUser == null) return;

        var cfg     = new Dictionary<string, string>(_editBuffer);
        var enabled = SessionState.IsToolEnabled(_modalTool.Id);
        SessionState.SetToolState(_modalTool.Id, enabled, cfg);
        await ToolService.SaveToolStateAsync(SessionState.CurrentUser.Id, _modalTool.Id, enabled, cfg);
        SessionState.NotifyToolSettingsChanged();

        _modalSaved = true;
        StateHasChanged();
        await Task.Delay(2000);
        _modalSaved = false;
        StateHasChanged();
    }

    internal async Task DisconnectOAuthAsync()
    {
        if (_modalTool is null || SessionState.CurrentUser is null) return;
        var provider = OAuthProvider(_modalTool.Id);
        if (provider is null) return;

        var userId = SessionState.CurrentUser.Id.ToString();
        _ = await BridgeRegistry.SendLocalRestAsync(userId, "DELETE", $"/oauth/{provider}");

        _oauthConnected = false;
        _oauthEmail     = null;
    }

    // ── MCP servers ───────────────────────────────────────────────────────

    internal async Task CheckBridgeStatusAsync()
    {
        if (SessionState.CurrentUser == null) return;
        _bridgeChecking = true;
        StateHasChanged();
        _bridgeOnline   = await AgentService.CheckMcpBridgeAsync(SessionState.CurrentUser.Id.ToString());
        _bridgeChecking = false;
        StateHasChanged();
    }

    /// <summary>Options for a ThemedSelect that picks a bridge node. Includes an empty "default" option
    /// plus currently-online nodes for the current soul.</summary>
    internal List<(string Value, string Label)> BridgeNodeSelectOptions()
    {
        var list = new List<(string, string)> { ("", "— use LLM node —") };
        if (SessionState.CurrentUser == null) return list;
        foreach (var nd in BridgeRegistry.GetNodes(SessionState.CurrentUser.Id.ToString()))
        {
            var lbl = (string.IsNullOrEmpty(nd.Label) ? nd.NodeId : nd.Label)
                + (string.IsNullOrEmpty(nd.Platform) ? "" : " · " + nd.Platform);
            list.Add((nd.NodeId, lbl));
        }
        return list;
    }
}
