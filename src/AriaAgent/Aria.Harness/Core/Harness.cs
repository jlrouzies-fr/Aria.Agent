using System.Runtime.CompilerServices;
using Aria.Agent;
using Aria.Harness.Context;
using Aria.Harness.Formats;
using Aria.Harness.Governance;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat; // OpenAIChatClientExtensions.AsAIAgent

namespace Aria.Harness.Core;

public sealed partial class Harness : IHarness
{
    private readonly ILogger<Harness> _logger;
    private readonly IHarnessRuntime _runtime;
    private readonly ConditionalWeakTable<AIAgent, UniversalReasoningHandler> _reasoningHandlers = new();
    private readonly ConditionalWeakTable<AIAgent, GovernanceContext> _governanceContexts = new();

    public Harness(ILogger<Harness> logger, IHarnessRuntime runtime)
    {
        _logger  = logger;
        _runtime = runtime;
    }

    public async Task<(AIAgent Agent, AgentSession Session)> CreateSessionAsync(
        HarnessOptions options,
        HarnessContext context,
        CancellationToken ct = default)
    {
        options.OnProgress?.Invoke("// LINK:    Locating channel...");

        var toolFmt = await DetectToolCallFormatAsync(
            options.SelectedSourceName, options.SelectedModel, context, ct);
        options.ToolCallFormat = toolFmt;   // carried into BuildChatClient so a forced Functionary parse activates
        options.OnProgress?.Invoke($"// TOOLS:   {(toolFmt == ToolCallFormat.None ? "NATIVE OPENAI" : toolFmt.ToString().ToUpper())}");

        // Resolve the bridge node for LLM calls: explicit override, then the selected source's binding.
        var sourceForLlm = _runtime.FindSource(options.SelectedSourceName, context);
        var llmNodeId = options.BridgeNodeId
                     ?? (sourceForLlm?.IsBridged == true ? sourceForLlm.BridgeNodeId : null)
                     ?? context.BridgeNodeId;
        context.BridgeNodeId = llmNodeId;

        context.ContextWindow = await ResolveContextWindowAsync(sourceForLlm, options.SelectedModel, context, ct);
        if (context.ContextWindow is { } known)
            options.OnProgress?.Invoke($"// CONTEXT: {known.Tokens:N0} tokens ({(known.Assumed ? "assumed" : "known")})");

        var chatClientPair = BuildChatClient(options, context, llmNodeId);
        if (chatClientPair == null)
            throw new InvalidOperationException("No channel selected. Select a channel in the sidebar.");

        var (chatClient, reasoningHandler) = chatClientPair.Value;

        var (tools, hasTerminalTools, hasMemoryTools, terminalProjects, terminalNodePlatforms) =
            await AssembleToolsAsync(options, context, llmNodeId, ct);

        var baseInstructions = options.InstructionsOverride ?? AgentDefaults.SystemMessage;
        if (hasTerminalTools)
            baseInstructions += BuildTerminalAddendum(terminalProjects, terminalNodePlatforms, options.ActiveProjectPath);
        // run_tests rides the same bridge manifest as the terminal tools but only exists on bridges
        // new enough to ship it — gate on actual registration so the prompt never names an absent tool.
        if (tools.Any(t => t.Name == "run_tests"))
            baseInstructions += RunTestsAddendum;
        // Gated on actual tool registration (not on the user's toggle): the memory tools also vanish
        // when the bridge is down, and the prompt must never reference absent tools.
        if (hasMemoryTools)
            baseInstructions += BuildMemoryAddendum();

        // Active-project AGENTS.md — Benign read_file, fail-soft. Reloaded on every session build
        // (including /project switches) so a cleared project drops the charter automatically.
        if (!string.IsNullOrWhiteSpace(options.ActiveProjectPath) &&
            await _runtime.IsBridgeAvailableAsync(context, ct))
        {
            var agentsText = await TryLoadAgentsMdAsync(
                options.ActiveProjectPath, terminalProjects, llmNodeId, context, ct);
            if (AgentsMdPrompt.BuildAddendum(agentsText) is { } agentsAddendum)
            {
                baseInstructions += agentsAddendum;
                options.OnProgress?.Invoke("// AGENTS:  Project charter loaded");
            }
        }

        // Always wrap every tool in a governance decorator (even when the initial mode is Off) so a
        // later mode change applies to this existing session — the per-turn policy is refreshed in
        // StreamAsync. The wrapper is invisible to the model (name/description/schema delegate to the
        // inner tool) and only intervenes to block, gate, or seal a call at invocation time.
        var govCtx = new GovernanceContext(options.Governance ?? GovernancePolicy.FromMode(GovernanceMode.Off));
        tools = tools
            .Select(t => t is AIFunction f
                ? (AITool)new GovernedTool(f, govCtx, options.OnApprovalRequested,
                      options.OnToolStart, options.OnToolComplete)
                : t)
            .ToList();

        // Deduplicate by function name (first wins). EnabledTools can carry the same tool twice
        // (e.g. globally enabled + sub-agent set), and duplicate function declarations are rejected
        // by OpenAI and confuse local servers' chat templates.
        var seenToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        tools = tools.Where(t => seenToolNames.Add(t.Name)).ToList();

        // EnableMessageInjection installs MessageInjectingChatClient so the UI can enqueue mid-turn
        // steers (tool-round boundary). Per-service-call persistence keeps those injects in session
        // history between function-loop iterations. Both flags are Experimental (MAAI001) in Agents.AI 1.6.2.
#pragma warning disable MAAI001
        var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions
            {
                Instructions = baseInstructions,
                Tools = tools,
            },
            EnableMessageInjection = true,
            RequirePerServiceCallChatHistoryPersistence = true,
        });
#pragma warning restore MAAI001

        _reasoningHandlers.GetValue(agent, _ => reasoningHandler);
        _governanceContexts.GetValue(agent, _ => govCtx);

        var session = await agent.CreateSessionAsync();
        options.OnProgress?.Invoke("Session active");
        return (agent, session);
    }
}
