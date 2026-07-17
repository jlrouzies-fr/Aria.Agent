using System.Text;
using System.Text.Json;
using Aria.Harness.Governance;
using Aria.Web.Services.Chat;
using Aria.Web.Services.ModelBridge;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Pages;

public sealed record TerminalLine(
    bool IsCommand,
    string Text,
    string? Cwd = null,
    string? NodeId = null,
    string? NodeLabel = null,
    DateTime? Timestamp = null,
    bool TimedOut = false);

public enum TerminalMode { QuickExec, Pty }

public partial class Chat
{

    private readonly List<TerminalLine> _terminalLines = [];

    private bool   _terminalCollapsed = true;
    private bool   _terminalAgentSees = false;
    private string _terminalSessionId = Guid.NewGuid().ToString("N");
    private string  _terminalCwd      = "";
    private string  _terminalInput    = "";
    private bool    _terminalExecuting;
    private bool    _terminalStateRestored;

    private TerminalMode _terminalMode = TerminalMode.QuickExec;
    private bool         _ptyEnabledOnNode;
    private bool         _ptyRequesting;
    private string?      _ptyWarning;
    private int          _ptyCols = 80;
    private int          _ptyRows = 24;
    private bool         _ptyNeedsCreation;
    private bool         _ptyCreating;

    // Bridge-side Terminal Capability toggle. Null = not checked yet; false = disabled; true = enabled.
    private bool?        _terminalEnabledOnBridge;

    private DotNetObjectReference<Chat>? _terminalInputDotNetRef;
    private DotNetObjectReference<Chat>? _ptyDotNetRef;
    private TerminalCompletionState?     _terminalCompletion;
    private bool                         _terminalCompleting;

    private bool TerminalProjectSelected => SessionState.ActiveProject != null;

    private string TerminalCwdDisplay => GetTerminalCwd();

    private string GetTerminalCwd()
    {
        if (!string.IsNullOrWhiteSpace(_terminalCwd)) return _terminalCwd;
        return SessionState.ActiveProject?.Path ?? "";
    }

    private string GetTerminalBridgeBadge()
    {
        var userId = BridgeUserId();
        if (userId == null) return "no bridge";

        var project = SessionState.ActiveProject;
        if (!string.IsNullOrWhiteSpace(project?.NodeId))
        {
            var node = BridgeRegistry.GetNodes(userId).FirstOrDefault(n => n.NodeId == project.NodeId);
            if (node != null)
                return string.IsNullOrWhiteSpace(node.Label) ? node.NodeId : node.Label;
        }

        var nodeId = ResolveTerminalNodeId();
        if (nodeId == null) return "no bridge";
        var fallbackNode = BridgeRegistry.GetNodes(userId).FirstOrDefault(n => n.NodeId == nodeId);
        return string.IsNullOrWhiteSpace(fallbackNode?.Label) ? nodeId : fallbackNode.Label;
    }

    // Lightness floors are deliberately high: the terminal background is near-black, so text needs
    // a bright accent to read clearly. dim/faint are used for body output and hints — keep them
    // legible, not decorative.
    private string TerminalAccentStyle =>
        $"--terminal-bg: {MixWithBlack(TerminalAccentColor, 0.94)}; " +
        $"--terminal-bg-soft: {MixWithBlack(TerminalAccentColor, 0.90)}; " +
        $"--terminal-grid: {MixWithBlack(TerminalAccentColor, 0.82)}; " +
        $"--terminal-accent: {BrightenHex(TerminalAccentColor, 0.78)}; " +
        $"--terminal-accent-dim: {BrightenHex(TerminalAccentColor, 0.66)}; " +
        $"--terminal-accent-faint: {BrightenHex(TerminalAccentColor, 0.50)};";

    private sealed record TerminalCompletionState(
        string Line,
        int ReplaceStart,
        int ReplaceEnd,
        List<TerminalCompletionCandidate> Candidates,
        int SelectedIndex,
        bool Truncated);

    private const int MaxTerminalLines      = 2000;
    private const int AgentContextMaxLines  = 80;
    private const int AgentContextMaxChars  = 8000;

    private const string TerminalCollapsedKey   = "ariaTerminalCollapsed";
    private const string TerminalAgentSeesKey   = "ariaTerminalAgentSees";

    private string TerminalAccentColor => EffectiveAgent?.AccentColor ?? "#8B0000";

    private string GetTerminalButtonTitle()
    {
        if (_agent == null || _cogitationOffline)
            return "Terminal unavailable";
        if (!SessionState.IsToolEnabled("terminal"))
            return "Terminal is disabled in tool settings";
        if (_terminalEnabledOnBridge == false)
            return "Terminal is not enabled on the bridge — open http://localhost:5741 and enable Terminal Capability";
        return _terminalCollapsed ? "Open terminal" : "Close terminal";
    }

    private async Task RefreshTerminalBridgeStatusAsync()
    {
        var userId = BridgeUserId();
        if (userId == null) return;
        try
        {
            _terminalEnabledOnBridge = await TerminalClient.IsTerminalEnabledAsync(userId, ResolveTerminalNodeId());
        }
        catch
        {
            _terminalEnabledOnBridge = false;
        }
        await InvokeAsync(StateHasChanged);
    }

    private static string MixWithBlack(string hex, double blackFactor)
    {
        var (r, g, b) = ParseHexRgb(hex);
        blackFactor = Math.Clamp(blackFactor, 0, 1);
        return $"#{(int)(r * (1 - blackFactor)):X2}{(int)(g * (1 - blackFactor)):X2}{(int)(b * (1 - blackFactor)):X2}";
    }

    private static string BrightenHex(string hex, double minLightness)
    {
        var (r, g, b) = ParseHexRgb(hex);
        var (h, s, l) = RgbToHsl(r, g, b);
        l = Math.Max(l, minLightness);
        var (nr, ng, nb) = HslToRgb(h, s, l);
        return $"#{nr:X2}{ng:X2}{nb:X2}";
    }

    private static (int r, int g, int b) ParseHexRgb(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        return (
            int.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
            int.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
            int.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber));
    }

    private static (double h, double s, double l) RgbToHsl(int r, int g, int b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double l = (max + min) / 2.0;
        double s, h;
        if (Math.Abs(max - min) < 0.0001) { s = 0; h = 0; }
        else
        {
            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (Math.Abs(max - rd) < 0.0001) h = (gd - bd) / d + (gd < bd ? 6 : 0);
            else if (Math.Abs(max - gd) < 0.0001) h = (bd - rd) / d + 2;
            else h = (rd - gd) / d + 4;
            h /= 6;
        }
        return (h, s, l);
    }

    private static (int r, int g, int b) HslToRgb(double h, double s, double l)
    {
        double r, g, b;
        if (s < 0.0001) r = g = b = l;
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3.0);
        }
        return ((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }

    private TerminalProject? ResolveTerminalProject() => SessionState.ActiveProject;

    private string? ResolveTerminalNodeId()
    {
        var userId = BridgeUserId();
        if (userId == null) return null;

        var project = ResolveTerminalProject();
        if (!string.IsNullOrWhiteSpace(project?.NodeId))
            return project.NodeId;

        return BridgeRegistry.GetNodes(userId).FirstOrDefault()?.NodeId;
    }

    private string ResolveTerminalContextLabel()
    {
        var project = ResolveTerminalProject();
        if (project != null) return project.Name;

        var userId = BridgeUserId();
        var nodeId = ResolveTerminalNodeId();
        if (userId == null || nodeId == null) return "node";
        var node = BridgeRegistry.GetNodes(userId).FirstOrDefault(n => n.NodeId == nodeId);
        return string.IsNullOrWhiteSpace(node?.Label) ? nodeId : node.Label;
    }

    private List<(string Value, string Label)> TerminalProjectOptions()
    {
        var opts = new List<(string Value, string Label)> { ("", "— select project —") };
        opts.AddRange(SessionState.Projects.Select(p => (p.Path, p.Name)));
        return opts;
    }

    private async Task ToggleTerminalAsync()
    {
        _terminalCollapsed = !_terminalCollapsed;
        await SaveTerminalStateAsync();
    }

    private async Task SetTerminalAgentSeesAsync(bool sees)
    {
        _terminalAgentSees = sees;
        await SaveTerminalStateAsync();
    }

    private async Task OnTerminalProjectSelectedAsync(string? path)
    {
        var project = string.IsNullOrWhiteSpace(path)
            ? null
            : SessionState.Projects.FirstOrDefault(p => p.Path == path);

        // A live PTY session is tied to a cwd/node; changing project scope tears it down.
        if (_terminalMode == TerminalMode.Pty)
            await DisposePtyAsync();

        await SetActiveProjectAsync(project);
        if (project == null) _terminalCwd = "";
        await SaveTerminalStateAsync();
    }

    private async Task SaveTerminalStateAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("ariaInterop.setLocalStorage", TerminalCollapsedKey, _terminalCollapsed ? "1" : "0");
            await JS.InvokeVoidAsync("ariaInterop.setLocalStorage", TerminalAgentSeesKey, _terminalAgentSees ? "1" : "0");
        }
        catch { }
    }

    private async Task RestoreTerminalStateAsync()
    {
        string? collapsed, agentSees;
        try
        {
            collapsed = await JS.InvokeAsync<string?>("ariaInterop.getLocalStorage", TerminalCollapsedKey);
            agentSees = await JS.InvokeAsync<string?>("ariaInterop.getLocalStorage", TerminalAgentSeesKey);
        }
        catch { return; }

        if (collapsed == "0") _terminalCollapsed = false;
        if (agentSees == "1") _terminalAgentSees = true;
        await InvokeAsync(StateHasChanged);
    }

    private async Task ExecuteTerminalCommandAsync()
    {
        var command = _terminalInput.Trim();
        if (string.IsNullOrEmpty(command) || _terminalExecuting || !TerminalProjectSelected) return;

        ClearTerminalCompletion();

        var userId = BridgeUserId();
        if (userId == null) return;

        var nodeId   = ResolveTerminalNodeId();
        var nodeLabel = ResolveTerminalContextLabel();
        var cwd      = GetTerminalCwd();

        _terminalExecuting = true;
        _terminalInput = "";
        AppendTerminalLine(new TerminalLine(true, command, cwd, nodeId, nodeLabel, DateTime.UtcNow));
        await InvokeAsync(StateHasChanged);
        await ScrollTerminalToBottomAsync();

        var allowedPaths = SessionState.AllowedProjectPaths;
        // Blocked-command patterns are authoritative on the bridge; the web no longer supplies extra ones.
        var blockedCommands = Array.Empty<string>();

        var result = await TerminalClient.ExecuteAsync(
            userId, command, cwd, _terminalSessionId, allowedPaths, blockedCommands, nodeId);

        if (result == null)
        {
            AppendTerminalLine(new TerminalLine(false, "// Terminal unavailable: bridge did not respond.", cwd, nodeId, nodeLabel, DateTime.UtcNow));
        }
        else
        {
            _terminalCwd = result.Cwd;

            if (!string.IsNullOrEmpty(result.Stdout))
            {
                foreach (var line in result.Stdout.ReplaceLineEndings("\n").Split('\n'))
                    AppendTerminalLine(new TerminalLine(false, line, result.Cwd, nodeId, nodeLabel, DateTime.UtcNow));
            }
            if (!string.IsNullOrEmpty(result.Stderr))
            {
                foreach (var line in result.Stderr.ReplaceLineEndings("\n").Split('\n'))
                    AppendTerminalLine(new TerminalLine(false, line, result.Cwd, nodeId, nodeLabel, DateTime.UtcNow, result.TimedOut));
            }
            if (string.IsNullOrEmpty(result.Stdout) && string.IsNullOrEmpty(result.Stderr) && result.TimedOut)
            {
                AppendTerminalLine(new TerminalLine(false, "⏱ TIMED OUT", result.Cwd, nodeId, nodeLabel, DateTime.UtcNow, true));
            }
            if (string.IsNullOrEmpty(result.Stdout) && string.IsNullOrEmpty(result.Stderr) && !result.TimedOut && result.ExitCode == 0)
            {
                // Empty success — show nothing, like a real shell.
            }
        }

        _terminalExecuting = false;
        await InvokeAsync(StateHasChanged);
        await ScrollTerminalToBottomAsync();
    }

    private async Task ScrollTerminalToBottomAsync()
    {
        try { await JS.InvokeVoidAsync("ariaInterop.scrollToBottom", "terminalBody"); }
        catch { }
    }

    private void AppendTerminalLine(TerminalLine line)
    {
        _terminalLines.Add(line);
        while (_terminalLines.Count > MaxTerminalLines)
            _terminalLines.RemoveAt(0);
    }

    private void OnTerminalKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Tab")
        {
            // Tab is handled by the JS interceptor so it can read the live DOM value/cursor.
            return;
        }

        if (_terminalCompletion != null && (e.Key == "Enter" || e.Key == "ArrowRight"))
        {
            AcceptTerminalCompletion();
            return;
        }

        if (e.Key == "Enter" && !e.ShiftKey)
        {
            ClearTerminalCompletion();
            _ = ExecuteTerminalCommandAsync();
            return;
        }

        if (e.Key == "Escape")
        {
            ClearTerminalCompletion();
            return;
        }

        // Any other keystroke while the completion strip is showing dismisses it
        // (the user is editing the token, not cycling).
        if (_terminalCompletion != null)
            ClearTerminalCompletion();
    }

    private void OnTerminalInputChanged(string value)
    {
        // If the incoming value matches what we just wrote programmatically (e.g. while cycling
        // completion candidates), don't treat it as a user edit and don't dismiss the strip.
        var isProgrammaticUpdate = _terminalInput == value;
        _terminalInput = value;
        if (_terminalCompletion != null && !isProgrammaticUpdate)
        {
            _terminalCompletion = null;
            _ = InvokeAsync(StateHasChanged);
        }
    }

    private async Task QuoteTerminalBlockToChatAsync(int commandIndex)
    {
        if (commandIndex < 0 || commandIndex >= _terminalLines.Count) return;
        var commandLine = _terminalLines[commandIndex];
        if (!commandLine.IsCommand) return;

        // Collect output lines until the next command.
        var output = new StringBuilder();
        for (var i = commandIndex + 1; i < _terminalLines.Count; i++)
        {
            if (_terminalLines[i].IsCommand) break;
            if (output.Length > 0) output.Append('\n');
            output.Append(_terminalLines[i].Text);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"```terminal ({commandLine.NodeLabel ?? commandLine.NodeId ?? "node"})");
        sb.AppendLine($"$ {commandLine.Text}");
        if (output.Length > 0)
            sb.AppendLine(output.ToString());
        sb.Append("```");

        var text = sb.ToString();
        var sep = _input.Length > 0 && !_input.EndsWith(' ') ? "\n\n" : "";
        _input = $"{_input}{sep}{text}\n";
        await FocusInputAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task<string?> BuildTerminalContextForAgentAsync()
    {
        if (!_terminalAgentSees) return null;

        var contextLabel = ResolveTerminalContextLabel();

        if (_terminalMode == TerminalMode.Pty)
        {
            var lines = await GetPtyBufferLinesAsync();
            if (lines.Count == 0) return null;

            var sb = new StringBuilder();
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var line = "  " + lines[i];
                if (sb.Length + line.Length + 1 > AgentContextMaxChars && sb.Length > 0)
                    break;
                sb.Insert(0, line + "\n");
            }
            if (sb.Length == 0) return null;
            return $"◈ TERMINAL (user's shell on {contextLabel}, PTY mode, most recent last):\n{sb.ToString().TrimEnd()}";
        }

        if (_terminalLines.Count == 0) return null;

        // Take the last N lines, then cap by characters from the tail backward.
        var recent = _terminalLines.TakeLast(AgentContextMaxLines).ToList();
        var sb2 = new StringBuilder();
        for (var i = recent.Count - 1; i >= 0; i--)
        {
            var prefix = recent[i].IsCommand ? "$ " : "  ";
            var line = prefix + recent[i].Text;
            if (sb2.Length + line.Length + 1 > AgentContextMaxChars && sb2.Length > 0)
                break;
            sb2.Insert(0, line + "\n");
        }

        if (sb2.Length == 0) return null;

        return $"◈ TERMINAL (user's shell on {contextLabel}, most recent last):\n{sb2.ToString().TrimEnd()}";
    }

    private async Task<List<string>> GetPtyBufferLinesAsync()
    {
        try
        {
            var result = await JS.InvokeAsync<List<string>?>("ariaInterop.terminalPty.getBufferLines", AgentContextMaxLines);
            return result ?? [];
        }
        catch { return []; }
    }

    // ── Tab completion ────────────────────────────────────────────────────────

    private void EnsureTerminalInputRef()
    {
        _terminalInputDotNetRef ??= DotNetObjectReference.Create(this);
    }

    [JSInvokable("OnTerminalTabAsync")]
    public async Task OnTerminalTabAsync(string text, int cursor, bool shiftKey)
    {
        if (!TerminalProjectSelected) return;
        var userId = BridgeUserId();
        if (userId == null) return;

        if (_terminalCompletion != null)
        {
            CycleTerminalCompletion(!shiftKey);
            return;
        }

        if (_terminalCompleting) return;
        _terminalCompleting = true;

        try
        {
            var nodeId = ResolveTerminalNodeId();
            var cwd = GetTerminalCwd();
            var allowedPaths = SessionState.AllowedProjectPaths;

            var result = await TerminalClient.CompleteAsync(
                userId, text, cursor, cwd, _terminalSessionId, allowedPaths, nodeId);
            if (result == null) return;

            if (result.Candidates is not { Count: > 0 })
            {
                // Bash shows nothing on no-match; a future enhancement could flash the prompt.
                return;
            }

            if (result.Candidates.Count == 1)
            {
                var c = result.Candidates[0];
                var replacement = c.Text + (c.IsDir ? "" : " ");
                await SetTerminalInputValueAsync(
                    ReplaceRange(text, result.ReplaceStart, result.ReplaceEnd, replacement),
                    result.ReplaceStart + replacement.Length);
                return;
            }

            if (!string.IsNullOrEmpty(result.CommonPrefix))
            {
                // First Tab extends to the longest common prefix (classic bash behavior).
                var extended = ReplaceRange(text, result.ReplaceEnd, result.ReplaceEnd, result.CommonPrefix);
                await SetTerminalInputValueAsync(extended, result.ReplaceEnd + result.CommonPrefix.Length);
            }
            else
            {
                // Show the transient candidates strip.
                _terminalCompletion = new TerminalCompletionState(
                    text, result.ReplaceStart, result.ReplaceEnd,
                    result.Candidates, 0, result.Truncated);
                await InvokeAsync(StateHasChanged);
            }
        }
        finally
        {
            _terminalCompleting = false;
        }
    }

    private void CycleTerminalCompletion(bool forward)
    {
        if (_terminalCompletion == null) return;
        var state = _terminalCompletion;
        var next = forward
            ? (state.SelectedIndex + 1) % state.Candidates.Count
            : (state.SelectedIndex - 1 + state.Candidates.Count) % state.Candidates.Count;
        _terminalCompletion = state with { SelectedIndex = next };
        _ = ApplyTerminalCompletionSelectionAsync();
    }

    private async Task ApplyTerminalCompletionSelectionAsync()
    {
        if (_terminalCompletion == null) return;
        var state = _terminalCompletion;
        var c = state.Candidates[state.SelectedIndex];
        var replacement = c.Text;
        var newText = ReplaceRange(state.Line, state.ReplaceStart, state.ReplaceEnd, replacement);
        await SetTerminalInputValueCoreAsync(newText, state.ReplaceStart + replacement.Length);
    }

    private void SelectTerminalCompletion(int index)
    {
        if (_terminalCompletion == null || index < 0 || index >= _terminalCompletion.Candidates.Count) return;
        _terminalCompletion = _terminalCompletion with { SelectedIndex = index };
        _ = ApplyTerminalCompletionSelectionAsync();
    }

    private void AcceptTerminalCompletion()
    {
        if (_terminalCompletion == null) return;
        var state = _terminalCompletion;
        var c = state.Candidates[state.SelectedIndex];
        var replacement = c.Text + (c.IsDir ? "" : " ");
        _ = SetTerminalInputValueAsync(
            ReplaceRange(state.Line, state.ReplaceStart, state.ReplaceEnd, replacement),
            state.ReplaceStart + replacement.Length);
    }

    private void ClearTerminalCompletion()
    {
        if (_terminalCompletion == null) return;
        _terminalCompletion = null;
        _ = InvokeAsync(StateHasChanged);
    }

    private async Task SetTerminalInputValueAsync(string value, int? cursor = null)
    {
        ClearTerminalCompletion();
        await SetTerminalInputValueCoreAsync(value, cursor);
    }

    private async Task SetTerminalInputValueCoreAsync(string value, int? cursor = null)
    {
        _terminalInput = value;
        await InvokeAsync(StateHasChanged);
        try
        {
            var pos = cursor ?? value.Length;
            await JS.InvokeVoidAsync("ariaInterop.debouncedInput.setValueAndCursor", "terminalInput", value, pos);
            await JS.InvokeVoidAsync("ariaInterop.terminalInput.updateCursor", "terminalInput");
        }
        catch { }
    }

    private static string ReplaceRange(string text, int start, int end, string replacement)
        => text[..start] + replacement + text[end..];

    private void ResetTerminalState()
    {
        _ = DisposePtyAsync();
        _terminalLines.Clear();
        _terminalCwd = "";
        _terminalInput = "";
        _terminalExecuting = false;
        _terminalAgentSees = false;
        _terminalCollapsed = true;
        _terminalMode = TerminalMode.QuickExec;
        _ptyWarning = null;
        _ptyEnabledOnNode = false;
        _terminalSessionId = Guid.NewGuid().ToString("N");
        ClearTerminalCompletion();
    }

    // ── PTY mode ─────────────────────────────────────────────────────────────

    private async Task SetTerminalModeAsync(TerminalMode mode)
    {
        if (_terminalMode == mode) return;

        // Guard against double-clicks or concurrent requests queued on the same circuit.
        if (mode == TerminalMode.Pty && _ptyRequesting) return;

        if (mode == TerminalMode.Pty)
        {
            _ptyWarning = null;
            _ptyRequesting = true;
            await InvokeAsync(StateHasChanged);

            var userId = BridgeUserId();
            var nodeId = ResolveTerminalNodeId();
            if (userId == null || nodeId == null)
            {
                _ptyRequesting = false;
                _ptyWarning = "No bridge connected for PTY mode.";
                await InvokeAsync(StateHasChanged);
                return;
            }

            try
            {
                _ptyEnabledOnNode = await TerminalClient.IsPtyEnabledAsync(userId, nodeId);
                if (!_ptyEnabledOnNode)
                {
                    var sealId = await SealService.RequestSealIdAsync(userId,
                        new ActionDescriptor(
                            "terminal_pty",
                            "Enable full interactive shell on this node",
                            "Full PTY shell over the tunnel. The node's blocklist and allowed-path policy will NOT apply.",
                            null,
                            Aria.Harness.Governance.ToolSeverity.NeedsSeal),
                        CancellationToken.None);
                    if (!string.IsNullOrEmpty(sealId))
                    {
                        _ptyEnabledOnNode = await TerminalClient.EnablePtyAsync(userId, sealId, nodeId);
                    }
                }

                if (!_ptyEnabledOnNode)
                {
                    _ptyWarning = "PTY mode requires approval on the cogitator node.";
                    await InvokeAsync(StateHasChanged);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                _ptyWarning = "PTY request was cancelled.";
                await InvokeAsync(StateHasChanged);
                return;
            }
            finally
            {
                _ptyRequesting = false;
            }
        }
        else
        {
            await DisposePtyAsync();
        }

        _terminalMode = mode;
        _ptyWarning = null;
        if (mode == TerminalMode.Pty) _ptyNeedsCreation = true;
        await InvokeAsync(StateHasChanged);
    }

    private async Task CreatePtyAsync()
    {
        if (_ptyCreating) return;
        _ptyCreating = true;

        try
        {
            var userId = BridgeUserId();
            var nodeId = ResolveTerminalNodeId();
            if (userId == null || nodeId == null || !TerminalProjectSelected)
            {
                _ptyWarning = "Cannot create PTY: no bridge or project selected.";
                await InvokeAsync(StateHasChanged);
                return;
            }

            _ptyDotNetRef?.Dispose();
            _ptyDotNetRef = DotNetObjectReference.Create(this);

            var created = await JS.InvokeAsync<System.Text.Json.JsonElement?>("ariaInterop.terminalPty.create",
                "terminalXterm", _ptyDotNetRef, _ptyCols, _ptyRows);
            if (!created.HasValue)
            {
                _ptyWarning = "Could not initialise the terminal panel in the browser.";
                await InvokeAsync(StateHasChanged);
                return;
            }

            if (created.Value.TryGetProperty("cols", out var c)) _ptyCols = c.GetInt32();
            if (created.Value.TryGetProperty("rows", out var r)) _ptyRows = r.GetInt32();

            TerminalPtyService.RegisterSession(_terminalSessionId, userId, nodeId,
                OnPtyChunkAsync,
                OnPtyClosed);

            // Ask the bridge to ensure the PTY session exists for this id.
            var cwd = GetTerminalCwd();
            var body = JsonSerializer.Serialize(new { sessionId = _terminalSessionId, cwd, cols = _ptyCols, rows = _ptyRows });
            var ptyResult = await BridgeRegistry.SendLocalRestAsync(userId, "POST", "/terminal/pty", body, nodeId: nodeId, timeoutSeconds: 15);

            if (ptyResult is not { StatusCode: 200 })
            {
                var errorMsg = "PTY session could not be started on the node.";
                try
                {
                    if (!string.IsNullOrEmpty(ptyResult?.Body))
                    {
                        using var doc = JsonDocument.Parse(ptyResult.Value.Body);
                        if (doc.RootElement.TryGetProperty("error", out var err))
                            errorMsg = $"PTY session could not be started: {err.GetString()}";
                    }
                }
                catch { }

                _ptyWarning = errorMsg;
                await DisposePtyAsync();
                _terminalMode = TerminalMode.QuickExec;
                await InvokeAsync(StateHasChanged);
                return;
            }

            // Fit to container after layout settles.
            await Task.Delay(50);
            await FitPtyAsync();
        }
        finally
        {
            _ptyCreating = false;
        }
    }

    private async Task DisposePtyAsync()
    {
        TerminalPtyService.UnregisterSession(_terminalSessionId);
        _ = TerminalPtyService.SendCloseAsync(BridgeUserId() ?? "", ResolveTerminalNodeId() ?? "", _terminalSessionId);
        try { await JS.InvokeVoidAsync("ariaInterop.terminalPty.dispose"); }
        catch { }
        _ptyDotNetRef?.Dispose();
        _ptyDotNetRef = null;
    }

    private async Task FitPtyAsync()
    {
        try
        {
            var result = await JS.InvokeAsync<System.Text.Json.JsonElement?>("ariaInterop.terminalPty.fit");
            if (result.HasValue)
            {
                if (result.Value.TryGetProperty("cols", out var c)) _ptyCols = c.GetInt32();
                if (result.Value.TryGetProperty("rows", out var r)) _ptyRows = r.GetInt32();
            }
        }
        catch { }
    }

    [JSInvokable("OnPtyData")]
    public async Task OnPtyDataAsync(string dataBase64)
    {
        var userId = BridgeUserId();
        var nodeId = ResolveTerminalNodeId();
        if (userId == null || nodeId == null) return;
        await TerminalPtyService.SendInputAsync(userId, nodeId, _terminalSessionId,
            Convert.FromBase64String(dataBase64));
    }

    [JSInvokable("OnPtyResize")]
    public async Task OnPtyResizeAsync(int cols, int rows)
    {
        _ptyCols = cols;
        _ptyRows = rows;
        var userId = BridgeUserId();
        var nodeId = ResolveTerminalNodeId();
        if (userId == null || nodeId == null) return;
        await TerminalPtyService.SendResizeAsync(userId, nodeId, _terminalSessionId, cols, rows);
    }

    private async Task OnPtyChunkAsync(byte[] data)
    {
        await InvokeAsync(async () =>
        {
            try { await JS.InvokeVoidAsync("ariaInterop.terminalPty.write", Convert.ToBase64String(data)); }
            catch { }
        });
    }

    private void OnPtyClosed(int? exitCode)
    {
        _ = InvokeAsync(async () =>
        {
            _ptyWarning = exitCode.HasValue ? $"PTY session exited (code {exitCode})." : "PTY session closed.";
            await DisposePtyAsync();
            _terminalMode = TerminalMode.QuickExec;
            StateHasChanged();
        });
    }
}
