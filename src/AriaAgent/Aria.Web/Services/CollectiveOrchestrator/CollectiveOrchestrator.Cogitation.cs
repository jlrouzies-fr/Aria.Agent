using System.Text;
using System.Text.Json;
using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Aria.Web.Services.CollectiveOrchestrator;

public partial class CollectiveOrchestrator
{
    public record HiveCogitationResult(int CogitationId, bool Success, string? Error);

    /// <summary>
    /// Creates an empty cogitation bound to this collective and returns its id, so the caller can
    /// navigate straight to the normal Chat UI (no separate "type your prompt" modal) — the user's
    /// first message there is what kicks off orchestration via <see cref="RunOnExistingCogitationAsync"/>.
    /// </summary>
    public async Task<int?> CreateHiveCogitationAsync(int collectiveId)
    {
        var collective = await LoadCollectiveAsync(collectiveId);
        if (collective == null) return null;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var cogSvc = scope.ServiceProvider.GetRequiredService<CogitationService>();
        var cog    = await cogSvc.CreateAsync(collective.UserId, collective.OvermindSubAgentId, collectiveId: collective.Id);
        return cog.Id;
    }

    /// <summary>
    /// Has the Overmind introduce itself in a freshly-opened, still-empty Hive cogitation — mirrors the
    /// "present yourself" greeting a normal new chat gets, so the channel doesn't sit silent until the
    /// soul sends the first message. Persists the reply (with thinking split out) and notifies
    /// <paramref name="onMessageAdded"/> exactly like a normal orchestration message.
    /// </summary>
    public async Task<HiveCogitationResult> SendOvermindGreetingAsync(
        int          collectiveId,
        int          cogitationId,
        Action<int>? onMessageAdded = null,
        CancellationToken ct = default)
    {
        try
        {
            var collective = await LoadCollectiveAsync(collectiveId);
            if (collective == null) return new(cogitationId, false, "Collective not found.");

            var members = await GetMembersAsync(collectiveId);
            var roster  = members.Count > 0
                ? string.Join(", ", members.Select(m => m.SubAgent.DisplayName))
                : "none recruited yet";

            var greetingPrompt =
                "Introduce yourself briefly, in character, as the Overmind of this Hive to the soul who just " +
                $"opened this channel. Mention the drones currently at your service: {roster}. Keep it to 2-4 " +
                "sentences and end by inviting them to give you an objective. Do not invent an objective yourself.";

            var (text, thinking) = await _executor.RunHeadlessWithThinkingAsync(
                userId: collective.UserId, subAgentId: collective.OvermindSubAgentId,
                prompt: greetingPrompt, sourceName: collective.OvermindSourceName,
                modelId: collective.OvermindModelId, instructionsPrefix: OvermindPrefix(collective), ct: ct);

            await SafeAppendCogMessageAsync(cogitationId, text, thinking);
            onMessageAdded?.Invoke(cogitationId);
            return new(cogitationId, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HIVE-COG] Overmind greeting failed for cogitation {CogId}", cogitationId);
            return new(cogitationId, false, ex.Message);
        }
    }

    /// <summary>
    /// Fires background orchestration against an already-open cogitation whose user message has
    /// already been persisted by the caller (the Chat composer). Returns immediately; background work
    /// calls <paramref name="onMessageAdded"/> each time a new message is persisted.
    /// </summary>
    public async Task<HiveCogitationResult> RunOnExistingCogitationAsync(
        int          collectiveId,
        int          cogitationId,
        string       userPrompt,
        Action<int>? onMessageAdded = null,
        CancellationToken ct = default)
    {
        try
        {
            var collective = await LoadCollectiveAsync(collectiveId);
            if (collective == null) return new(cogitationId, false, "Collective not found.");

            var members = await GetMembersAsync(collectiveId);
            if (members.Count == 0) return new(cogitationId, false, "No drones in this collective.");

            await AppendEventAsync(collectiveId, CollectiveEventType.Info,
                $"Cogitation #{cogitationId} started — objective: {userPrompt[..Math.Min(80, userPrompt.Length)]}", null, null);
            FireChanged(collectiveId);

            FireBackgroundOrchestration(collective, members, cogitationId, userPrompt, onMessageAdded, ct);

            return new(cogitationId, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HIVE-COG] Collective {Id} cogitation (existing #{CogId}) setup crashed", collectiveId, cogitationId);
            return new(cogitationId, false, ex.Message);
        }
    }

    /// <summary>
    /// Creates the cogitation, writes the user message, fires background orchestration, and returns
    /// immediately. The caller receives the cogitationId so it can open the chat channel at once.
    /// Background work calls <paramref name="onMessageAdded"/> each time a new message is persisted.
    /// </summary>
    public async Task<HiveCogitationResult> RunCogitationAsync(
        int          collectiveId,
        string       userPrompt,
        Action<int>? onMessageAdded = null,
        CancellationToken ct = default)
    {
        try
        {
            var collective = await LoadCollectiveAsync(collectiveId);
            if (collective == null) return new(0, false, "Collective not found.");

            var members = await GetMembersAsync(collectiveId);
            if (members.Count == 0) return new(0, false, "No drones in this collective.");

            // Create cogitation & persist user message synchronously so the chat opens with content
            await using var scope0 = _scopeFactory.CreateAsyncScope();
            var cogSvc = scope0.ServiceProvider.GetRequiredService<CogitationService>();
            var cog    = await cogSvc.CreateAsync(collective.UserId, collective.OvermindSubAgentId, collectiveId: collective.Id);
            int cogId  = cog.Id;
            await cogSvc.SetTitleAsync(cogId, $"⬡ {collective.Name} — {userPrompt[..Math.Min(40, userPrompt.Length)]}…");
            await cogSvc.AddMessageAsync(cogId, "user", userPrompt);

            await AppendEventAsync(collectiveId, CollectiveEventType.Info,
                $"Cogitation #{cogId} started — objective: {userPrompt[..Math.Min(80, userPrompt.Length)]}", null, null);
            FireChanged(collectiveId);

            // Notify once so Chat opens and shows the user message before orchestration begins
            onMessageAdded?.Invoke(cogId);

            FireBackgroundOrchestration(collective, members, cogId, userPrompt, onMessageAdded, ct);

            return new(cogId, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HIVE-COG] Collective {Id} cogitation setup crashed", collectiveId);
            return new(0, false, ex.Message);
        }
    }

    // Fires the plan/dispatch/synthesise pipeline in the background — does NOT block the caller.
    private void FireBackgroundOrchestration(
        AgentCollective        collective,
        List<CollectiveMember> members,
        int                    cogId,
        string                 userPrompt,
        Action<int>?           onMessageAdded,
        CancellationToken      ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RunCogitationBackgroundAsync(collective, members, cogId, userPrompt, onMessageAdded, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HIVE-COG] Background cogitation {CogId} unhandled crash", cogId);
                await SafeAppendCogMessageAsync(cogId, $"⬡ **OVERMIND FAULT**: {ex.Message}");
                onMessageAdded?.Invoke(cogId);
            }
        });
    }

    private async Task RunCogitationBackgroundAsync(
        AgentCollective        collective,
        List<CollectiveMember> members,
        int                    cogId,
        string                 userPrompt,
        Action<int>?           onMessageAdded,
        CancellationToken      ct)
    {
        int collectiveId = collective.Id;

        var overmindPrefix = OvermindPrefix(collective);

        async Task WriteMsg(string content, string? thinking = null)
        {
            await SafeAppendCogMessageAsync(cogId, content, thinking);
            onMessageAdded?.Invoke(cogId);
        }

        try
        {
            // ── PLAN ─────────────────────────────────────────────────────────
            var memberIdList = string.Join(", ", members.Select(m => m.Id));
            var exampleId    = members[0].Id;
            var rosterLines  = members.Select(BuildRosterLine).ToList();

            var planSchema =
                "{\n  \"directives\": [\n    {\n" +
                $"      \"title\": \"short task title\",\n      \"assignedMemberId\": {exampleId},\n" +
                "      \"instruction\": \"concrete task\",\n      \"dependsOn\": []\n    }\n  ]\n}";

            var synapseBlock = !string.IsNullOrWhiteSpace(collective.SynapseMemory)
                ? $"\n\nSYNAPSE MEMORY (prior collective knowledge — use it to inform the plan):\n{collective.SynapseMemory}\n"
                : "";

            var planSystemPrompt =
                "You are the OVERMIND of an agent collective. Decompose the OBJECTIVE into directives, " +
                "one per drone. Each directive must be concrete and independently executable.\n" +
                "Assign each directive to the drone whose role, persona, tools and skills best fit it.\n\n" +
                $"DRONES (use exact memberId integers: {memberIdList}):\n" +
                string.Join("\n", rosterLines) +
                synapseBlock + "\n\n" +
                $"CRITICAL: assignedMemberId must be one of: {memberIdList}\n\n" +
                "Respond ONLY with valid JSON (no prose, no code blocks):\n" + planSchema;

            SetPhase(collectiveId, "Planning");
            await WriteMsg($"⬡ **OVERMIND** is planning the operation…\n\n*Drones online: {string.Join(", ", members.Select(m => m.SubAgent.DisplayName))}*");

            _logger.LogInformation("[HIVE-COG] {Id}: Calling Overmind for plan", collectiveId);
            var planHistory = new List<ChatMessage> { new(ChatRole.System, planSystemPrompt) };
            var planText    = await _executor.RunHeadlessAsync(
                userId: collective.UserId, subAgentId: collective.OvermindSubAgentId,
                prompt: $"OBJECTIVE: {userPrompt}", sourceName: collective.OvermindSourceName,
                modelId: collective.OvermindModelId, seedHistory: planHistory,
                instructionsPrefix: overmindPrefix, ct: ct);

            _logger.LogInformation("[HIVE-COG] {Id}: Raw plan: {T}", collectiveId,
                planText.Length > 500 ? planText[..500] : planText);

            var plan = TryParsePlan(planText);

            // One repair attempt if parse failed
            if (plan == null || plan.Directives.Length == 0)
            {
                _logger.LogWarning("[HIVE-COG] {Id}: Plan parse failed — attempting repair", collectiveId);
                var repairHistory = new List<ChatMessage>
                {
                    new(ChatRole.User,      $"OBJECTIVE: {userPrompt}"),
                    new(ChatRole.Assistant, planText),
                    new(ChatRole.User,
                        $"Your reply could not be parsed. Return ONLY a JSON object with a 'directives' array. " +
                        $"Each directive must have assignedMemberId set to one of: {memberIdList}. No prose, no markdown."),
                };
                var repairText = await _executor.RunHeadlessAsync(
                    userId: collective.UserId, subAgentId: collective.OvermindSubAgentId,
                    prompt: $"Return ONLY valid JSON with assignedMemberId from: {memberIdList}",
                    sourceName: collective.OvermindSourceName, modelId: collective.OvermindModelId,
                    seedHistory: repairHistory, instructionsPrefix: overmindPrefix, ct: ct);
                plan = TryParsePlan(repairText);
            }

            if (plan == null || plan.Directives.Length == 0)
            {
                await WriteMsg("⬡ **OVERMIND**: Failed to produce a valid plan. Cogitation aborted.");
                await AppendEventAsync(collectiveId, CollectiveEventType.Failed, "Plan phase failed.", null, null);
                FireChanged(collectiveId);
                return;
            }

            var memberIds     = new HashSet<int>(members.Select(m => m.Id));
            var memberByAgent = members.ToDictionary(m => m.SubAgentId, m => m.Id);
            var memberList    = members.Select(m => m.Id).ToArray();
            int rrIdx         = 0;

            var directives = plan.Directives
                .Select(d =>
                {
                    if (memberIds.Contains(d.AssignedMemberId)) return d;
                    if (memberByAgent.TryGetValue(d.AssignedMemberId, out var rid)) return d with { AssignedMemberId = rid };
                    return d;
                })
                .Select(d => memberIds.Contains(d.AssignedMemberId)
                    ? d : d with { AssignedMemberId = memberList[rrIdx++ % memberList.Length] })
                .Take(MaxTasksPerRound)
                .ToList();

            // ── DISPATCH with reliability check + retry ───────────────────────
            const int MaxRetries     = 2;
            const int MinReliability = 5;

            var droneMetrics = new Dictionary<int, (string Name, int Tokens, int Reliability, int Retries, string Result)>();

            SetPhase(collectiveId, "Dispatching");

            await Task.WhenAll(directives.Select(async directive =>
            {
                var member = members.FirstOrDefault(m => m.Id == directive.AssignedMemberId);
                if (member == null) return;

                SetDrone(collectiveId, member.Id, DroneRunState.Running);

                // Condition nodes: skip this drone unless its condition(s) pass.
                if (member.EdgeNodes.Any(n => n.NodeType == EdgeNodeType.Condition))
                {
                    var condInstr = directive.Instruction ?? directive.Title ?? userPrompt;
                    if (!await EvaluateConditionsAsync(collective, member, condInstr, ct))
                    {
                        await WriteMsg($"⬡ **{member.SubAgent.DisplayName.ToUpperInvariant()}** — skipped (condition not met).");
                        SetDrone(collectiveId, member.Id, DroneRunState.Skipped);
                        await AppendEventAsync(collectiveId, CollectiveEventType.Info,
                            $"{member.SubAgent.DisplayName} skipped — edge condition not met.", member.Id, null);
                        FireChanged(collectiveId);
                        return;
                    }
                }

                // Pre-dispatch gate: approve before the drone even receives the instruction
                if (member.RequiresHumanApproval && !member.GateAfterResponse)
                {
                    var preInstruction = member.EdgeNodes.Count > 0
                        ? CollectiveService.ApplyTransforms(member.EdgeNodes,
                            directive.Instruction ?? directive.Title ?? userPrompt)
                        : directive.Instruction ?? directive.Title ?? userPrompt;
                    await WriteMsg(
                        $"⬡ **GATE** — Awaiting approval before dispatching " +
                        $"**{member.SubAgent.DisplayName.ToUpperInvariant()}**\n\n" +
                        $"*Task: {directive.Title ?? "untitled"}*");
                    var gateTcs2 = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _memberGates[(cogId, member.Id)] = gateTcs2;
                    SetDrone(collectiveId, member.Id, DroneRunState.AwaitingGate);
                    OnHiveMemberGatePending?.Invoke(cogId, member.Id, member.SubAgent.DisplayName, preInstruction);
                    using var gateTimeout2 = new CancellationTokenSource(TimeSpan.FromHours(2));
                    using var linked2      = CancellationTokenSource.CreateLinkedTokenSource(gateTimeout2.Token, ct);
                    try   { await gateTcs2.Task.WaitAsync(linked2.Token); }
                    catch (OperationCanceledException) { /* timed out or cancelled — proceed anyway */ }
                    _memberGates.TryRemove((cogId, member.Id), out _);
                    SetDrone(collectiveId, member.Id, DroneRunState.Running);
                    OnHiveMemberGateResolved?.Invoke(cogId, member.Id);
                }

                string  result          = "";
                string? resultThinking  = null;
                int     tokens          = 0;
                int     retries         = 0;
                int     reliability     = 7;

                for (int attempt = 0; attempt <= MaxRetries; attempt++)
                {
                    ct.ThrowIfCancellationRequested();

                    var rawInstruction = attempt == 0
                        ? directive.Instruction ?? directive.Title ?? userPrompt
                        : $"Your previous answer was rated {reliability}/10 and needs improvement.\n" +
                          $"Feedback: {result[..Math.Min(200, result.Length)]}\n\n" +
                          $"Retry the task: {directive.Instruction ?? directive.Title}";

                    var instruction = member.EdgeNodes.Count > 0
                        ? CollectiveService.ApplyTransforms(member.EdgeNodes, rawInstruction)
                        : rawInstruction;

                    _logger.LogInformation("[HIVE-COG] Drone {Name} attempt {A} instruction: {I}",
                        member.SubAgent.DisplayName, attempt + 1,
                        instruction.Length > 120 ? instruction[..120] : instruction);
                    await AppendEventAsync(collectiveId, CollectiveEventType.Dispatched,
                        $"{member.SubAgent.DisplayName}: attempt {attempt + 1} — [{directive.Title ?? "task"}]",
                        member.Id, null);
                    FireChanged(collectiveId);

                    List<ChatMessage>? droneContext = null;
                    if (!string.IsNullOrWhiteSpace(collective.SynapseMemory))
                        droneContext = [new(ChatRole.System,
                            $"SYNAPSE LINK — OVERMIND BRIEFING:\n{collective.SynapseMemory}")];

                    var (text, est, thinking) = await _executor.RunHeadlessWithMetricsAsync(
                        userId:     collective.UserId,
                        subAgentId: member.SubAgentId,
                        prompt:     instruction,
                        sourceName: member.SubAgent.ModelSourceName,
                        modelId:    member.SubAgent.ModelId,
                        seedHistory: droneContext,
                        ct: ct);

                    result         = text;
                    resultThinking = thinking;
                    tokens        += est;

                    if (attempt < MaxRetries)
                    {
                        var ratingPrompt =
                            $"TASK: {directive.Instruction ?? directive.Title}\n\n" +
                            $"DRONE OUTPUT:\n{result[..Math.Min(600, result.Length)]}\n\n" +
                            $"OBJECTIVE: {userPrompt}\n\n" +
                            "Rate the drone's output from 1 (useless) to 10 (perfect). " +
                            "Respond ONLY as JSON: { \"score\": <1-10>, \"feedback\": \"...\" }";

                        var ratingHistory = new List<ChatMessage>
                        {
                            new(ChatRole.System,
                                "You are the OVERMIND quality controller. Rate the drone work objectively. No prose — JSON only.")
                        };

                        var ratingText = await _executor.RunHeadlessAsync(
                            userId: collective.UserId, subAgentId: collective.OvermindSubAgentId,
                            prompt: ratingPrompt, sourceName: collective.OvermindSourceName,
                            modelId: collective.OvermindModelId, seedHistory: ratingHistory,
                            instructionsPrefix: overmindPrefix, ct: ct);

                        var ratingJson = ExtractFirstJsonObject(ratingText);
                        if (ratingJson != null)
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(ratingJson);
                                if (doc.RootElement.TryGetProperty("score", out var scoreProp))
                                    reliability = scoreProp.GetInt32();
                            }
                            catch { }
                        }

                        if (reliability >= MinReliability) break;
                        retries++;
                    }
                    else break;
                }

                // Post-response gate: pause before the reply enters synthesis so the user can review it
                if (member.RequiresHumanApproval && member.GateAfterResponse)
                {
                    await WriteMsg(
                        $"⬡ **GATE** — **{member.SubAgent.DisplayName.ToUpperInvariant()}** replied. " +
                        $"Approve to include in synthesis.");
                    var gateTcs3 = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _memberGates[(cogId, member.Id)] = gateTcs3;
                    SetDrone(collectiveId, member.Id, DroneRunState.AwaitingGate);
                    OnHiveMemberGatePending?.Invoke(cogId, member.Id, member.SubAgent.DisplayName, result);
                    using var gateTimeout3 = new CancellationTokenSource(TimeSpan.FromHours(2));
                    using var linked3      = CancellationTokenSource.CreateLinkedTokenSource(gateTimeout3.Token, ct);
                    try   { await gateTcs3.Task.WaitAsync(linked3.Token); }
                    catch (OperationCanceledException) { /* timed out or cancelled — proceed anyway */ }
                    _memberGates.TryRemove((cogId, member.Id), out _);
                    OnHiveMemberGateResolved?.Invoke(cogId, member.Id);
                }

                lock (droneMetrics)
                    droneMetrics[member.Id] = (member.SubAgent.DisplayName, tokens, reliability, retries, result);

                await AppendEventAsync(collectiveId, CollectiveEventType.DroneResult,
                    $"{member.SubAgent.DisplayName} — reliability {reliability}/10, ~{tokens} tokens, {retries} retries",
                    member.Id, null);
                FireChanged(collectiveId);

                await WriteMsg(
                    $"**⬡ DRONE: {member.SubAgent.DisplayName.ToUpperInvariant()}** " +
                    $"*(reliability {reliability}/10 · ~{tokens} tokens · {retries} retries)*\n\n{result}",
                    resultThinking);

                SetDrone(collectiveId, member.Id, DroneRunState.Done);
            }));

            // ── HUMAN-IN-THE-LOOP GATE ───────────────────────────────────────
            string? soulDirective = null;
            if (collective.RequiresHumanApproval)
            {
                var dronePreview = string.Join("\n\n", droneMetrics.Values.Select(m =>
                    $"**{m.Name}** (reliability {m.Reliability}/10):\n{(m.Result.Length > 400 ? m.Result[..400] + "…" : m.Result)}"));

                await WriteMsg(
                    "⬡ **GATE — AWAITING SOUL DIRECTIVE**\n\n" +
                    "Drone phase complete. Review their outputs and approve synthesis, or provide a redirect directive.\n\n" +
                    dronePreview);

                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _humanGates[cogId] = tcs;
                OnHiveGatePending?.Invoke(cogId);

                using var gateTimeout = new CancellationTokenSource(TimeSpan.FromHours(2));
                using var linked      = CancellationTokenSource.CreateLinkedTokenSource(ct, gateTimeout.Token);
                try
                {
                    soulDirective = await tcs.Task.WaitAsync(linked.Token);
                    if (!string.IsNullOrWhiteSpace(soulDirective))
                        await WriteMsg($"⬡ **SOUL DIRECTIVE**: {soulDirective}");
                }
                catch (OperationCanceledException)
                {
                    _humanGates.TryRemove(cogId, out _);
                    OnHiveGateResolved?.Invoke(cogId);
                    await WriteMsg("⬡ **GATE TIMED OUT** — proceeding with synthesis.");
                }
            }

            // ── SYNTHESIS ─────────────────────────────────────────────────────
            SetPhase(collectiveId, "Synthesising");
            var droneResultsBlock = string.Join("\n\n", droneMetrics.Values.Select(m =>
                $"### [{m.Name}] (reliability {m.Reliability}/10)\n{m.Result[..Math.Min(1000, m.Result.Length)]}"));

            var synthSystemPrompt =
                "You are the OVERMIND. Synthesize the drone outputs into a single coherent, well-structured answer. " +
                "Do not mention the drones or the internal process — just produce the final answer.";
            var synthHistory = new List<ChatMessage> { new(ChatRole.System, synthSystemPrompt) };
            var soulBlock   = !string.IsNullOrWhiteSpace(soulDirective)
                ? $"\nSOUL DIRECTIVE (incorporate this into your synthesis): {soulDirective}\n"
                : "";
            var synthPrompt  =
                $"ORIGINAL QUESTION: {userPrompt}\n\n" +
                $"DRONE OUTPUTS:\n{droneResultsBlock}\n{soulBlock}\n" +
                "Produce the final synthesised answer:";

            _logger.LogInformation("[HIVE-COG] {Id}: Calling Overmind for synthesis", collectiveId);
            var (synthesis, synthThinking) = await _executor.RunHeadlessWithThinkingAsync(
                userId: collective.UserId, subAgentId: collective.OvermindSubAgentId,
                prompt: synthPrompt, sourceName: collective.OvermindSourceName,
                modelId: collective.OvermindModelId, seedHistory: synthHistory,
                instructionsPrefix: overmindPrefix, ct: ct);

            // Final answer with metrics table
            var sb = new StringBuilder();
            sb.AppendLine("⬡ **OVERMIND CONCLUSION**");
            sb.AppendLine();
            sb.AppendLine(synthesis.TrimEnd());
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("| Drone | Est. Tokens | Reliability | Retries |");
            sb.AppendLine("|-------|-------------|-------------|---------|");
            foreach (var m in droneMetrics.Values)
                sb.AppendLine($"| {m.Name} | ~{m.Tokens} | {m.Reliability}/10 | {m.Retries} |");

            await WriteMsg(sb.ToString(), synthThinking);

            // Persist synthesis as ResultSummary on the collective for the canvas drawer
            var summaryText = synthesis.Length > 2000 ? synthesis[..2000] : synthesis;
            if (!string.IsNullOrEmpty(collective.OriginNodeId))
            {
                await _bridgeHive.EnsureCollectiveAsync(collective.UserId, collectiveId, collective.OriginNodeId);
                await _bridgeHive.UpdateCollectiveContentAsync(
                    collective.UserId, collectiveId, collective.OriginNodeId, resultSummary: summaryText);
            }
            else
            {
                await using var summaryScope = _scopeFactory.CreateAsyncScope();
                var summaryDbf = summaryScope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                await using var summaryDb = await summaryDbf.CreateDbContextAsync();
                await summaryDb.AgentCollectives
                    .Where(c => c.Id == collectiveId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResultSummary, summaryText));
            }

            // ── Update Synapse Memory ─────────────────────────────────────────
            // Ask the Overmind to distill key facts from this cogitation into the shared memory.
            // This runs fire-and-forget so it doesn't delay the conclusion message.
            _ = Task.Run(async () =>
            {
                try
                {
                    var existing = string.IsNullOrWhiteSpace(collective.SynapseMemory)
                        ? ""
                        : $"EXISTING SYNAPSE MEMORY:\n{collective.SynapseMemory}\n\n";

                    var synapseSystemMsg = new List<ChatMessage>
                    {
                        new(ChatRole.System,
                            "You are the OVERMIND maintaining a collective memory. Extract only durable, reusable facts " +
                            "from this cogitation that would benefit future operations. Be concise — bullet points only, " +
                            "max 10 bullets, no fluff. Do NOT repeat the original question or the full answer.")
                    };
                    var synapsePrompt =
                        $"{existing}NEW COGITATION:\nQ: {userPrompt}\n\n" +
                        $"SYNTHESIS:\n{(synthesis.Length > 800 ? synthesis[..800] : synthesis)}\n\n" +
                        "Extract key learnings to add to Synapse Memory (bullet points):";

                    var updated = await _executor.RunHeadlessAsync(
                        userId:     collective.UserId,
                        subAgentId: collective.OvermindSubAgentId,
                        prompt:     synapsePrompt,
                        sourceName: collective.OvermindSourceName,
                        modelId:    collective.OvermindModelId,
                        seedHistory: synapseSystemMsg,
                        instructionsPrefix: overmindPrefix,
                        ct: CancellationToken.None);

                    if (!string.IsNullOrWhiteSpace(updated))
                    {
                        var newMemory = string.IsNullOrWhiteSpace(collective.SynapseMemory)
                            ? updated.Trim()
                            : collective.SynapseMemory.TrimEnd() + "\n\n" + updated.Trim();

                        // Cap at ~3000 chars to avoid unbounded growth
                        if (newMemory.Length > 3000) newMemory = newMemory[^3000..];

                        await using var synapseScope = _scopeFactory.CreateAsyncScope();
                        var synapseSvc = synapseScope.ServiceProvider.GetRequiredService<CollectiveService>();
                        await synapseSvc.SaveSynapseMemoryAsync(collectiveId, newMemory);

                        _logger.LogInformation("[HIVE-COG] {Id}: Synapse Memory updated ({Len} chars)", collectiveId, newMemory.Length);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[HIVE-COG] {Id}: Synapse Memory update failed (non-fatal)", collectiveId);
                }
            });

            // Update cogitation title (drop the ellipsis)
            await using var titleScope = _scopeFactory.CreateAsyncScope();
            var titleCogSvc = titleScope.ServiceProvider.GetRequiredService<CogitationService>();
            await titleCogSvc.SetTitleAsync(cogId, $"⬡ {collective.Name} — {userPrompt[..Math.Min(40, userPrompt.Length)]}");

            await AppendEventAsync(collectiveId, CollectiveEventType.Completed,
                $"Cogitation #{cogId} complete.", null, null);
            FireChanged(collectiveId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[HIVE-COG] Cogitation {CogId} cancelled", cogId);
            await SafeAppendCogMessageAsync(cogId, "⬡ **OVERMIND**: Cogitation cancelled.");
            onMessageAdded?.Invoke(cogId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HIVE-COG] Collective {Id} cogitation background crashed", collectiveId);
            await SafeAppendCogMessageAsync(cogId, $"⬡ **OVERMIND FAULT**: {ex.Message}");
            onMessageAdded?.Invoke(cogId);
            await AppendEventAsync(collectiveId, CollectiveEventType.Failed, $"Cogitation crashed: {ex.Message}", null, null);
            FireChanged(collectiveId);
        }
        finally
        {
            // Clear the live canvas state once the run ends (success, cancel, or crash).
            ClearRunState(collectiveId);
        }
    }

    private async Task SafeAppendCogMessageAsync(int cogId, string content, string? thinking = null)
    {
        try
        {
            await using var s  = _scopeFactory.CreateAsyncScope();
            var cs = s.ServiceProvider.GetRequiredService<CogitationService>();
            await cs.AddMessageAsync(cogId, "assistant", content, thinking);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HIVE-COG] Failed to append message to cogitation {CogId}", cogId);
        }
    }
}
