using Aria.Agent;
using Aria.Harness.Context;
using Aria.Harness.Formats;
using Aria.Harness.Governance;
using Aria.Harness.Tools;
using Aria.Tools;

namespace Aria.Harness.Core;

/// <summary>
/// Configuration for creating one agent session.
/// Host-agnostic: no DB, no UI, no bridge specifics.
/// </summary>
public sealed class HarnessOptions
{
    public string? SelectedSourceName { get; set; }
    public string? SelectedModel { get; set; }
    public ThinkingFormat ThinkingFormat { get; set; } = ThinkingFormat.None;

    /// <summary>Resolved tool-call format for the session. Only <see cref="ToolCallFormat.Functionary"/>
    /// changes runtime parsing (delimiter-less, human-forced); all others are auto-detected from the
    /// stream and this is purely informational.</summary>
    public ToolCallFormat ToolCallFormat { get; set; } = ToolCallFormat.Unknown;
    public IReadOnlyList<ActiveToolConfig> EnabledTools { get; set; } = Array.Empty<ActiveToolConfig>();
    public IEnumerable<McpServerConfig>? UserMcpServers { get; set; }

    /// <summary>
    /// Bridge-authoritative Terminal projects (name, path, description, node id, platform).
    /// When supplied, the Harness uses these instead of parsing AllowedPaths from the terminal tool config.
    /// </summary>
    public IReadOnlyList<(string Name, string Path, string Description, string? NodeId, string? Platform)>? TerminalProjects { get; set; }

    public string? InstructionsOverride { get; set; }
    public string? AgentNameOverride { get; set; }
    public string? BridgeNodeId { get; set; }

    /// <summary>
    /// How the Noosphere memory tools (Probe/Contemplate) recall across a soul's connected nodes.
    /// <see cref="Core.RecallScope.ThisNode"/> reads only the LLM node's local vault;
    /// <see cref="Core.RecallScope.AllNodes"/> (default) fans the query out to every connected node and
    /// merges the results — memory is node-local, so on a multi-node soul the LLM node often isn't where
    /// memories live. Inscribe always writes to the single LLM node regardless.
    /// </summary>
    public RecallScope RecallScope { get; set; } = RecallScope.AllNodes;

    /// <summary>
    /// When set, the Terminal tool is scoped to just this one declared project path (the active
    /// project selected in the chat file explorer / via "/project"): its <c>AllowedPaths</c> — and so
    /// the bridge's hard path enforcement — cover only this project, not every declared one. When null
    /// or unmatched, all declared projects remain accessible (the prior behaviour).
    /// </summary>
    public string? ActiveProjectPath { get; set; }

    /// <summary>
    /// Callback that receives reasoning/thinking tokens as they are emitted.
    /// </summary>
    public Action<string>? OnThinkingToken { get; set; }

    /// <summary>
    /// Callback for progress messages during session creation.
    /// </summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>
    /// Called when a tool invocation starts.
    /// </summary>
    public Action<string, string>? OnToolStart { get; set; }

    /// <summary>
    /// Called when a tool invocation completes: (name, resultText, imageBase64, imageMediaType, metadataJson).
    /// imageBase64/imageMediaType are non-null only for a multimodal result (e.g. TakeScreenshot).
    /// metadataJson is an optional UI-only payload (e.g. diff cards) the model never sees.
    /// </summary>
    public Action<string, string, string?, string?, string?>? OnToolComplete { get; set; }

    /// <summary>
    /// Called when the agent posts or updates its task manifest (todo list).
    /// When set, the always-on <c>update_task_manifest</c> tool is registered.
    /// </summary>
    public Action<IReadOnlyList<TodoItem>>? OnTodoUpdate { get; set; }

    /// <summary>
    /// When set, the always-on <c>ask_user</c> tool is registered: the agent can pause mid-run and
    /// ask a structured question (with up to 4 option buttons). The callback surfaces the question
    /// in chat and returns the user's answer — chosen option label or typed text — or null on
    /// timeout/skip, which the tool turns into a "proceed with your best judgment" result.
    /// Web wires this for interactive sessions only; headless runs leave it null.
    /// </summary>
    public Func<string, string[]?, CancellationToken, Task<string?>>? OnAskUser { get; set; }

    /// <summary>
    /// When set, the always-on <c>context_status</c> tool is registered: the agent can check its own
    /// context pressure (last reported input tokens, transcript estimate, auto-compact headroom).
    /// The host supplies a cheap per-session snapshot provider.
    /// </summary>
    public Func<ContextStatusSnapshot>? ContextStatusProvider { get; set; }

    /// <summary>
    /// Plain-text index of the host UI's "/" commands and "#" references. When set, the
    /// always-on <c>list_chat_capabilities</c> tool is registered so the agent can answer
    /// "how do I do X" questions about the interface. Web-only — Console leaves this null.
    /// </summary>
    public string? ChatCapabilitiesText { get; set; }

    /// <summary>
    /// When set, the always-on <c>spawn_agent</c>/<c>agent_result</c> tools are registered so the
    /// agent can delegate self-contained subtasks to sub-agent personas running headlessly.
    /// Web wires this (session-bound) for interactive chat sessions only — headless runs leave it
    /// null, so a spawned child can never itself fan out (delegation depth is one level).
    /// </summary>
    public ISubAgentSpawner? SubAgentSpawner { get; set; }

    /// <summary>
    /// Governance policy applied to every tool call. Null or <see cref="GovernanceMode.Off"/>
    /// leaves tools ungoverned (legacy behaviour).
    /// </summary>
    public GovernancePolicy? Governance { get; set; }

    /// <summary>
    /// Invoked when a tool call needs human authorisation (in-chat approval, or a node-signed Seal
    /// for <see cref="ToolSeverity.NeedsSeal"/>). Returns true to allow the call, false to deny it.
    /// </summary>
    public Func<ActionDescriptor, CancellationToken, Task<bool>>? OnApprovalRequested { get; set; }
}
