using Aria.Agent;
using Aria.Harness.Formats;
using Aria.Web.Services.AgentServices;
using Aria.Web.Services.Chat;
using Microsoft.AspNetCore.Components;

namespace Aria.Web.Components.Pages;

/// <summary>
/// The "format not recognised" decision modal, extracted from Chat.razor. Session init awaits
/// <see cref="RequestConfirmationAsync"/> (through an @ref on the parent); the modal owns every
/// action's persistence — SAVE stores the detected formats, APPLY stores a human-picked override,
/// a conclusive RETRY stores the fresh probe — so the caller never re-persists.
/// </summary>
public partial class ChatFormatModal : ComponentBase
{
    [Inject] private AgentService     AgentService { get; set; } = null!;
    [Inject] private UserSessionState SessionState { get; set; } = null!;

    /// <summary>What the modal shows the user for one channel/model.</summary>
    public sealed record FormatDetectPrompt(
        string SourceName, string? ModelId, ThinkingFormat Thinking, ToolCallFormat ToolCall);

    // Session init is sequential, so a single pending slot + TCS is sufficient.
    private FormatDetectPrompt? _pending;
    private TaskCompletionSource<bool>? _tcs;

    private bool    _retrying;
    private string? _retryMsg;
    private bool    _unreachable;   // last retry failed because the server couldn't be reached

    // Manual override selections (value = enum name).
    private string? _overrideThinking;
    private string? _overrideTool;

    /// <summary>
    /// Parks session init on the modal until the user chooses. Returns true to keep the session going
    /// with a persisted decision (SAVE / APPLY / conclusive RETRY), false to SKIP (re-probe next
    /// session). Cancellation counts as skip.
    /// </summary>
    public async Task<bool> RequestConfirmationAsync(FormatDetectPrompt prompt, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await InvokeAsync(() =>
        {
            _pending = prompt;
            _tcs     = tcs;
            SeedOverride(prompt);
            StateHasChanged();
        });

        bool confirmed;
        try   { confirmed = await tcs.Task.WaitAsync(ct); }
        catch (OperationCanceledException) { confirmed = false; }

        await InvokeAsync(() =>
        {
            _pending = null;
            _tcs     = null;
            StateHasChanged();
        });

        return confirmed;
    }

    // "SAVE AS DETECTED" — lock in exactly what the probe returned.
    private async Task ConfirmAsync()
    {
        if (_pending is not { } fd) return;
        await AgentService.ConfirmFormatsAsync(fd.SourceName, fd.ModelId, fd.Thinking, fd.ToolCall,
            SessionState.CurrentUser?.Id);
        _tcs?.TrySetResult(true);
    }

    private void Refuse() => _tcs?.TrySetResult(false);

    // "RETRY NOW" — re-probe the model live so the user sees the fresh result. Drops any cached
    // (possibly negative) verdict first so this is a genuine hit, not a cached None returned instantly.
    private async Task RetryAsync()
    {
        if (_pending is not { } fd || _retrying) return;

        _retrying    = true;
        _unreachable = false;
        _retryMsg    = "Re-probing the model live…";
        StateHasChanged();

        var userId = SessionState.CurrentUser?.Id;
        try
        {
            await AgentService.ClearChannelFormatsAsync(fd.SourceName, userId: userId);
            var res = await AgentService.ResolveFormatsAsync(fd.SourceName, fd.ModelId, userId: userId);

            if (!res.NeedsConfirmation)
            {
                // Fresh probe is now conclusive — lock it in and dismiss the modal.
                await AgentService.ConfirmFormatsAsync(fd.SourceName, fd.ModelId, res.Thinking, res.ToolCall, userId);
                _tcs?.TrySetResult(true);
                return;
            }

            // Still ambiguous — but "ambiguous" and "server down" look identical from the probe (both
            // Unknown). Ask the node whether it could reach the endpoint and say which it is, so the
            // user isn't left thinking Aria failed to parse a model that never ran.
            _pending = fd with { Thinking = res.Thinking, ToolCall = res.ToolCall };
            SeedOverride(_pending);

            var (reachable, detail) = await AgentService.ProbeSourceReachabilityAsync(fd.SourceName, fd.ModelId, userId);
            _unreachable = !reachable;
            _retryMsg = reachable
                ? $"Re-probe complete — the model answered, but its thinking/tool format is still unrecognised (thinking: {res.Thinking}, tools: {res.ToolCall})."
                : $"Couldn't reach this model. {detail} — start the server (e.g. LM Studio / Ollama), load the model, then retry.";
        }
        catch (Exception ex)
        {
            _retryMsg = "Re-probe failed: " + ex.Message;
        }
        finally
        {
            _retrying = false;
            StateHasChanged();
        }
    }

    // "APPLY SELECTED FORMAT" — persist the user's explicit picks and dismiss. This is the only path
    // that can select ToolCallFormat.Functionary (delimiter-less, never auto-detected).
    private async Task ApplyOverrideAsync()
    {
        if (_pending is not { } fd || _retrying) return;
        if (!Enum.TryParse<ThinkingFormat>(_overrideThinking, out var think)) think = ThinkingFormat.None;
        if (!Enum.TryParse<ToolCallFormat>(_overrideTool,     out var tool))  tool  = ToolCallFormat.None;

        _retrying = true;
        _retryMsg = $"Applying override — thinking: {think}, tools: {tool}…";
        StateHasChanged();
        try
        {
            await AgentService.ConfirmFormatsAsync(fd.SourceName, fd.ModelId, think, tool, SessionState.CurrentUser?.Id);
            _tcs?.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _retryMsg = "Override failed: " + ex.Message;
            _retrying = false;
            StateHasChanged();
        }
    }

    // Seed the dropdowns from the current (detected) verdict whenever the prompt is (re)set.
    private void SeedOverride(FormatDetectPrompt fd)
    {
        _overrideThinking = fd.Thinking == ThinkingFormat.Unknown ? "None" : fd.Thinking.ToString();
        _overrideTool     = fd.ToolCall == ToolCallFormat.Unknown ? null   : fd.ToolCall.ToString();
    }

    // Dropdown options (value = enum name, label = human hint). Unknown is not offerable.
    internal static readonly List<(string Value, string Label)> ThinkingFormatOptions = new()
    {
        ("None",              "None — no thinking tokens"),
        ("ReasoningContent",  "ReasoningContent — reasoning_content field"),
        ("ThinkTags",         "ThinkTags — <think>…</think>"),
        ("StartsInThinkMode", "StartsInThinkMode — </think> only"),
        ("ChannelThought",    "ChannelThought — <|channel>thought"),
        ("Harmony",           "Harmony — <|channel|>analysis/final"),
    };
    internal static readonly List<(string Value, string Label)> ToolCallFormatOptions = new()
    {
        ("None",              "None — native OpenAI tool_calls"),
        ("ToolCallTag",       "ToolCallTag — <tool_call>…</tool_call>"),
        ("StartFunctionCall", "StartFunctionCall — <start_function_call>"),
        ("MistralToolCalls",  "MistralToolCalls — [TOOL_CALLS]"),
        ("MinimaxToolCall",   "MinimaxToolCall — <minimax:tool_call>"),
        ("KimiK2",            "KimiK2 — <|tool_calls_section_begin|>"),
        ("Longcat",           "Longcat — <longcat_tool_call>"),
        ("GlmXml",            "GlmXml — <arg_key>/<arg_value>"),
        ("Gemma4ToolCall",    "Gemma4ToolCall — <|tool_call>…<tool_call|>"),
        ("Harmony",           "Harmony — <|channel|>commentary to=…"),
        ("Functionary",       "Functionary — bare name\\n{args} (v3.x)"),
    };
}
