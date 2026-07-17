using System.Text.Json;
using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Aria.Web.Services.CollectiveOrchestrator;

public partial class CollectiveOrchestrator
{
    // ── HIVE MIND plan ──────────────────────────────────────────────────
    // Every drone tackles the SAME directive (the objective); the REVIEW phase then judges all
    // outputs and picks/merges the best. No decomposition, no per-drone assignment.

    private async Task<bool> RunHiveMindPlanAsync(
        AgentCollective collective, int round, string? priorFeedback, CancellationToken ct)
    {
        try
        {
            var members = await GetMembersAsync(collective.Id);
            if (members.Count == 0)
            {
                _logger.LogWarning("[HIVE] {Id}: No members for Hive Mind round", collective.Id);
                return false;
            }

            // Shared instruction = objective + prior-round context + last review feedback.
            var completedTasks = await GetCompletedTasksAsync(collective.Id, round - 1);
            var completedDigest = completedTasks.Count > 0
                ? "\n\nBEST ANSWER SO FAR (prior rounds):\n" +
                  string.Join("\n", completedTasks.Select(t =>
                      $"- {(t.Result?.Length > 200 ? t.Result[..200] + "…" : t.Result ?? "no result")}"))
                : "";
            var feedbackBlock = !string.IsNullOrWhiteSpace(priorFeedback)
                ? $"\n\nOVERMIND FEEDBACK FROM LAST REVIEW:\n{priorFeedback}"
                : "";
            var instruction = $"{collective.Objective}{completedDigest}{feedbackBlock}";

            await AppendEventAsync(collective.Id, CollectiveEventType.Info,
                $"Round {round} — HIVE MIND: all {members.Count} drones tackle the objective.", null, null);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbf.CreateDbContextAsync();

            var tasks = new List<CollectiveTask>();
            foreach (var m in members)
            {
                var task = new CollectiveTask
                {
                    CollectiveId     = collective.Id,
                    AssignedMemberId = m.Id,
                    Round            = round,
                    Title            = "Tackle objective",
                    Instruction      = instruction,
                    Status           = CollectiveTaskStatus.Pending,
                };
                db.CollectiveTasks.Add(task);
                tasks.Add(task);
            }
            await db.SaveChangesAsync();

            if (!string.IsNullOrEmpty(collective.OriginNodeId))
            {
                foreach (var t in tasks)
                {
                    await _bridgeHive.UpsertTaskContentAsync(
                        collective.UserId, collective.Id, t.Id, collective.OriginNodeId,
                        t.Title, t.Instruction, null, null);
                }
            }

            foreach (var t in tasks)
                await AppendEventAsync(collective.Id, CollectiveEventType.Planned,
                    $"Hive Mind: dispatched objective to member #{t.AssignedMemberId}", t.AssignedMemberId, t.Id);

            _logger.LogInformation("[HIVE] {Id} round {R}: HIVE MIND plan — {N} drones", collective.Id, round, tasks.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HIVE] {Id} round {R}: Hive Mind plan error", collective.Id, round);
            await AppendEventAsync(collective.Id, CollectiveEventType.Failed, $"Hive Mind plan error: {ex.Message}", null, null);
            return false;
        }
    }

    // ── PLAN phase ────────────────────────────────────────────────────────

    private async Task<bool> RunPlanPhaseAsync(
        AgentCollective collective, int round, string? priorFeedback, CancellationToken ct)
    {
        try
        {
            var members = await GetMembersAsync(collective.Id);
            if (members.Count == 0)
            {
                _logger.LogWarning("[HIVE] {Id}: No members to plan for", collective.Id);
                return false;
            }

            var rosterLines = members.Select(BuildRosterLine).ToList();

            var memberIdList = string.Join(", ", members.Select(m => m.Id));
            var exampleMemberId = members[0].Id;

            var planSchema =
                "{\n  \"directives\": [\n    {\n" +
                $"      \"title\": \"short task title\",\n" +
                $"      \"assignedMemberId\": {exampleMemberId},\n" +
                "      \"instruction\": \"concrete self-contained task description\",\n" +
                "      \"dependsOn\": []\n" +
                "    }\n  ]\n}";

            var systemPrompt =
                "You are the OVERMIND, orchestrator of an agent collective. You do NOT perform work yourself.\n" +
                "You decompose the OBJECTIVE into small, independently-executable DIRECTIVES and assign each to the\n" +
                "DRONE whose role, persona, tools and skills best fit it. Keep directives concrete and self-contained.\n\n" +
                $"DRONE ROSTER (use the exact integer memberId values: {memberIdList}):\n" +
                string.Join("\n", rosterLines) + "\n\n" +
                "CRITICAL: \"assignedMemberId\" must be one of these exact integers: " + memberIdList + "\n\n" +
                "Respond with ONLY a valid JSON object — no prose, no markdown, no code blocks — matching:\n" + planSchema;

            // Build completed-task digest for rounds > 1
            var completedTasks = await GetCompletedTasksAsync(collective.Id, round - 1);
            var completedDigest = completedTasks.Count > 0
                ? "\n\nCOMPLETED WORK (prior rounds):\n" +
                  string.Join("\n", completedTasks.Select(t =>
                      $"- [{t.Title}] → {(t.Result?.Length > 200 ? t.Result[..200] + "…" : t.Result ?? "no result")}"))
                : "";

            var feedbackBlock = !string.IsNullOrWhiteSpace(priorFeedback)
                ? $"\n\nOVERMIND FEEDBACK FROM LAST REVIEW:\n{priorFeedback}"
                : "";

            var userMessage = $"OBJECTIVE: {collective.Objective}{completedDigest}{feedbackBlock}";

            _logger.LogInformation("[HIVE] {Id} round {R}: PLAN phase — calling Overmind", collective.Id, round);
            await AppendEventAsync(collective.Id, CollectiveEventType.Info,
                $"Round {round} — PLAN phase: Overmind decomposing objective.", null, null);

            var planHistory = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
            };

            string planText = await _executor.RunHeadlessAsync(
                userId:      collective.UserId,
                subAgentId:  collective.OvermindSubAgentId,
                prompt:      userMessage,
                sourceName:  collective.OvermindSourceName,
                modelId:     collective.OvermindModelId,
                seedHistory: planHistory,
                instructionsPrefix: OvermindPrefix(collective),
                ct:          ct);

            _logger.LogInformation("[HIVE] {Id} round {R}: Raw plan text (first 500): {Text}",
                collective.Id, round, planText.Length > 500 ? planText[..500] : planText);

            // Parse JSON defensively
            var parsed = TryParsePlan(planText);
            if (parsed == null)
            {
                // One repair attempt
                _logger.LogWarning("[HIVE] {Id} round {R}: Plan parse failed, attempting repair", collective.Id, round);
                var repairPrompt = $"Your previous reply could not be parsed. Return ONLY a JSON object with a 'directives' array. " +
                    $"Each directive must have assignedMemberId set to one of these integers: {memberIdList}. No prose, no markdown.";
                var repairHistory = new List<ChatMessage>
                {
                    new(ChatRole.User,      userMessage),
                    new(ChatRole.Assistant, planText),
                    new(ChatRole.User,      repairPrompt),
                };
                string repairText = await _executor.RunHeadlessAsync(
                    userId:     collective.UserId,
                    subAgentId: collective.OvermindSubAgentId,
                    prompt:     repairPrompt,
                    sourceName: collective.OvermindSourceName,
                    modelId:    collective.OvermindModelId,
                    seedHistory: repairHistory,
                    instructionsPrefix: OvermindPrefix(collective),
                    ct:         ct);
                parsed = TryParsePlan(repairText);
            }

            if (parsed == null || parsed.Directives.Length == 0)
            {
                _logger.LogError("[HIVE] {Id} round {R}: Could not parse Overmind plan", collective.Id, round);
                await AppendEventAsync(collective.Id, CollectiveEventType.Failed,
                    "PLAN failed: could not parse Overmind JSON response.", null, null);
                return false;
            }

            var memberIds = new HashSet<int>(members.Select(m => m.Id));

            // Remap directives whose assignedMemberId is a sub-agent ID instead of a member ID
            var memberByAgentId = members.ToDictionary(m => m.SubAgentId, m => m.Id);
            var remappedDirectives = parsed.Directives.Select(d =>
            {
                if (memberIds.Contains(d.AssignedMemberId)) return d;
                // If the LLM used the sub-agent ID instead of member ID, remap it
                if (memberByAgentId.TryGetValue(d.AssignedMemberId, out var remappedId))
                    return d with { AssignedMemberId = remappedId };
                return d;
            }).ToList();

            // Round-robin for any truly invalid IDs
            int rrIdx = 0;
            var memberList = members.Select(m => m.Id).ToArray();
            var validDirectives = remappedDirectives
                .Select(d => memberIds.Contains(d.AssignedMemberId)
                    ? d
                    : d with { AssignedMemberId = memberList[rrIdx++ % memberList.Length] })
                .Take(MaxTasksPerRound)
                .ToList();

            // Persist tasks
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbf.CreateDbContextAsync();

            var tasks = new List<CollectiveTask>();
            foreach (var d in validDirectives)
            {
                var task = new CollectiveTask
                {
                    CollectiveId     = collective.Id,
                    AssignedMemberId = d.AssignedMemberId,
                    Round            = round,
                    Title            = d.Title ?? "Task",
                    Instruction      = d.Instruction ?? d.Title ?? "No instruction provided.",
                    Status           = CollectiveTaskStatus.Pending,
                };
                db.CollectiveTasks.Add(task);
                tasks.Add(task);
            }
            await db.SaveChangesAsync();

            if (!string.IsNullOrEmpty(collective.OriginNodeId))
            {
                foreach (var t in tasks)
                {
                    await _bridgeHive.UpsertTaskContentAsync(
                        collective.UserId, collective.Id, t.Id, collective.OriginNodeId,
                        t.Title, t.Instruction, null, null);
                }
            }

            // Mark Blocked tasks (those whose DependsOn titles reference other tasks in this batch)
            // We use simple index-based depends: if dependsOn array has ints, treat as task index
            for (int i = 0; i < validDirectives.Count; i++)
            {
                var d = validDirectives[i];
                if (d.DependsOn is { Length: > 0 })
                {
                    var depTaskIds = d.DependsOn
                        .Where(idx => idx >= 0 && idx < tasks.Count && idx != i)
                        .Select(idx => tasks[idx].Id)
                        .ToArray();
                    if (depTaskIds.Length > 0)
                    {
                        tasks[i].DependsOnJson = JsonSerializer.Serialize(depTaskIds);
                        tasks[i].Status        = CollectiveTaskStatus.Blocked;
                    }
                }
            }
            await db.SaveChangesAsync();

            foreach (var t in tasks)
            {
                await AppendEventAsync(collective.Id, CollectiveEventType.Planned,
                    $"Planned: [{t.Title}] → member #{t.AssignedMemberId}", t.AssignedMemberId, t.Id);
            }

            _logger.LogInformation("[HIVE] {Id} round {R}: PLAN complete — {N} directives", collective.Id, round, tasks.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HIVE] {Id} round {R}: PLAN phase error", collective.Id, round);
            await AppendEventAsync(collective.Id, CollectiveEventType.Failed, $"PLAN error: {ex.Message}", null, null);
            return false;
        }
    }

    // ── DISPATCH phase ────────────────────────────────────────────────────

    private async Task RunDispatchPhaseAsync(AgentCollective collective, CancellationToken ct)
    {
        _logger.LogInformation("[HIVE] {Id}: DISPATCH phase starting", collective.Id);
        await AppendEventAsync(collective.Id, CollectiveEventType.Info, "DISPATCH phase: running drones.", null, null);

        // Inner loop to handle dependency chains within a single dispatch pass
        const int maxPasses = 10;
        for (int pass = 0; pass < maxPasses && !ct.IsCancellationRequested; pass++)
        {
            var runnable = await GetRunnableTasksAsync(collective.Id);
            if (runnable.Count == 0) break;

            // Run all runnable tasks in parallel, bounded by semaphore
            var taskRuns = runnable.Select(task => RunDroneTaskAsync(collective, task, ct)).ToList();
            await Task.WhenAll(taskRuns);

            // Re-evaluate: any Blocked tasks now runnable?
            var stillBlocked = await GetBlockedTasksAsync(collective.Id);
            if (stillBlocked.Count == 0) break;

            // Check if all blocked deps are done
            bool anyUnblocked = false;
            foreach (var blocked in stillBlocked)
            {
                if (await AreDepsSatisfiedAsync(blocked.Id, collective.Id))
                {
                    await UpdateTaskStatusAsync(blocked.Id, CollectiveTaskStatus.Pending);
                    anyUnblocked = true;
                }
            }
            if (!anyUnblocked) break;
        }

        _logger.LogInformation("[HIVE] {Id}: DISPATCH phase complete", collective.Id);
    }

    private async Task RunDroneTaskAsync(AgentCollective collective, CollectiveTask task, CancellationToken ct)
    {
        // Load member before acquiring the semaphore — needed for gate check
        var member = await GetMemberAsync(task.AssignedMemberId!.Value);
        if (member == null)
        {
            await MarkTaskFailedAsync(task.Id, "Member not found");
            return;
        }

        var sentinelCogId = -collective.Id;

        // Edge conditions: skip this drone unless its condition(s) pass.
        if (member.EdgeNodes.Any(n => n.NodeType == EdgeNodeType.Condition))
        {
            SetDrone(collective.Id, member.Id, DroneRunState.Running);
            var condInstr = task.Instruction ?? task.Title ?? "";
            if (!await EvaluateConditionsAsync(collective, member, condInstr, ct))
            {
                await UpdateTaskStatusAsync(task.Id, CollectiveTaskStatus.Skipped);
                SetDrone(collective.Id, member.Id, DroneRunState.Skipped);
                await AppendEventAsync(collective.Id, CollectiveEventType.Info,
                    $"{member.SubAgent.DisplayName} skipped — edge condition not met.", task.AssignedMemberId, task.Id);
                FireChanged(collective.Id);
                return;
            }
        }

        // Pre-dispatch gate — runs BEFORE semaphore so waiting for human doesn't block other drone slots
        if (member.RequiresHumanApproval && !member.GateAfterResponse)
        {
            var rawPre = await BuildDroneInstructionAsync(task, collective.Id);
            var preInstruction = member.EdgeNodes.Count > 0
                ? CollectiveService.ApplyTransforms(member.EdgeNodes, rawPre)
                : rawPre;
            var gateTcsPre = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _memberGates[(sentinelCogId, member.Id)] = gateTcsPre;
            SetDrone(collective.Id, member.Id, DroneRunState.AwaitingGate);
            OnHiveMemberGatePending?.Invoke(sentinelCogId, member.Id, member.SubAgent.DisplayName, preInstruction);
            using var gt1 = new CancellationTokenSource(TimeSpan.FromHours(2));
            using var lk1 = CancellationTokenSource.CreateLinkedTokenSource(gt1.Token, ct);
            try { await gateTcsPre.Task.WaitAsync(lk1.Token); }
            catch (OperationCanceledException) { /* timed out or cancelled — proceed anyway */ }
            _memberGates.TryRemove((sentinelCogId, member.Id), out _);
            OnHiveMemberGateResolved?.Invoke(sentinelCogId, member.Id);
        }

        await _droneSemaphore.WaitAsync(ct);
        try
        {
            _logger.LogInformation("[HIVE] {Id}: Dispatching task {TaskId} '{Title}' to member {MemberId}",
                collective.Id, task.Id, task.Title, task.AssignedMemberId);

            var rawInstruction = await BuildDroneInstructionAsync(task, collective.Id);
            var instruction = member.EdgeNodes.Count > 0
                ? CollectiveService.ApplyTransforms(member.EdgeNodes, rawInstruction)
                : rawInstruction;

            await MarkTaskRunningAsync(task.Id, instruction);
            SetDrone(collective.Id, member.Id, DroneRunState.Running);
            await AppendEventAsync(collective.Id, CollectiveEventType.Dispatched,
                $"Dispatching [{task.Title}] to {member.SubAgent.DisplayName}", task.AssignedMemberId, task.Id);
            FireChanged(collective.Id);
            await AppendEventAsync(collective.Id, CollectiveEventType.DroneStarted,
                $"{member.SubAgent.DisplayName} started [{task.Title}]", task.AssignedMemberId, task.Id);
            FireChanged(collective.Id);

            try
            {
                var result = await _executor.RunHeadlessAsync(
                    userId:     collective.UserId,
                    subAgentId: member.SubAgentId,
                    prompt:     instruction,
                    sourceName: member.SubAgent.ModelSourceName,
                    modelId:    member.SubAgent.ModelId,
                    ct:         ct);

                await MarkTaskCompletedAsync(task.Id, result);
                await AppendEventAsync(collective.Id, CollectiveEventType.DroneResult,
                    $"{member.SubAgent.DisplayName} completed [{task.Title}]: {(result.Length > 100 ? result[..100] + "…" : result)}",
                    task.AssignedMemberId, task.Id);

                // Post-response gate — pause after result is saved so the user can review it
                if (member.RequiresHumanApproval && member.GateAfterResponse)
                {
                    var gateTcsPost = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _memberGates[(sentinelCogId, member.Id)] = gateTcsPost;
                    SetDrone(collective.Id, member.Id, DroneRunState.AwaitingGate);
                    OnHiveMemberGatePending?.Invoke(sentinelCogId, member.Id, member.SubAgent.DisplayName, result);
                    using var gt2 = new CancellationTokenSource(TimeSpan.FromHours(2));
                    using var lk2 = CancellationTokenSource.CreateLinkedTokenSource(gt2.Token, ct);
                    try { await gateTcsPost.Task.WaitAsync(lk2.Token); }
                    catch (OperationCanceledException) { }
                    _memberGates.TryRemove((sentinelCogId, member.Id), out _);
                    OnHiveMemberGateResolved?.Invoke(sentinelCogId, member.Id);
                }
                SetDrone(collective.Id, member.Id, DroneRunState.Done);
            }
            catch (Exception ex)
            {
                await MarkTaskFailedAsync(task.Id, ex.Message);
                SetDrone(collective.Id, member.Id, DroneRunState.Done);
                await AppendEventAsync(collective.Id, CollectiveEventType.Failed,
                    $"{member.SubAgent.DisplayName} FAILED [{task.Title}]: {ex.Message}",
                    task.AssignedMemberId, task.Id);
            }
            FireChanged(collective.Id);
        }
        finally
        {
            _droneSemaphore.Release();
        }
    }

    // ── REVIEW phase ──────────────────────────────────────────────────────

    private async Task<(string Decision, string? Summary, string? Feedback)> RunReviewPhaseAsync(
        AgentCollective collective, int round, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[HIVE] {Id} round {R}: REVIEW phase", collective.Id, round);
            await AppendEventAsync(collective.Id, CollectiveEventType.Reviewed,
                $"Round {round} — REVIEW phase: Overmind evaluating results.", null, null);

            var tasks = await GetRoundTasksAsync(collective.Id, round);
            var taskTable = string.Join("\n", tasks.Select(t =>
                $"- [{t.Title}] ({t.Status}) → {(t.Result?.Length > 150 ? t.Result[..150] + "…" : t.Result ?? t.ErrorMessage ?? "no result")}"));

            var reviewSystemPrompt = collective.Behavior == CollectiveBehavior.HiveMind
                ? """
                  You are the OVERMIND. The drones each independently attempted the SAME objective — the results below
                  are competing answers. Judge them, then either pick the single best answer or merge their strongest
                  parts into one superior answer. Respond ONLY as JSON:
                  { "decision": "COMPLETE" | "CONTINUE" | "ABORT",
                    "summary": "the best/merged final answer if COMPLETE, else short rationale",
                    "feedback": "if CONTINUE, how the drones should improve next round" }
                  """
                : """
                  You are the OVERMIND reviewing the directives completed this round. Decide whether the OBJECTIVE is
                  fully satisfied. Respond ONLY as JSON:
                  { "decision": "COMPLETE" | "CONTINUE" | "ABORT",
                    "summary": "final synthesized answer if COMPLETE, else short rationale",
                    "feedback": "if CONTINUE, what still needs doing (guides next PLAN)" }
                  """;

            var reviewUserMessage = $"""
                OBJECTIVE: {collective.Objective}

                TASKS THIS ROUND:
                {taskTable}
                """;

            var reviewHistory = new List<ChatMessage>
            {
                new(ChatRole.System, reviewSystemPrompt),
            };

            var reviewText = await _executor.RunHeadlessAsync(
                userId:      collective.UserId,
                subAgentId:  collective.OvermindSubAgentId,
                prompt:      reviewUserMessage,
                sourceName:  collective.OvermindSourceName,
                modelId:     collective.OvermindModelId,
                seedHistory: reviewHistory,
                instructionsPrefix: OvermindPrefix(collective),
                ct:          ct);

            var review = TryParseReview(reviewText);
            if (review == null)
            {
                _logger.LogWarning("[HIVE] {Id} round {R}: Could not parse review, defaulting CONTINUE", collective.Id, round);
                return ("CONTINUE", null, "Could not parse Overmind review — proceeding.");
            }

            _logger.LogInformation("[HIVE] {Id} round {R}: Review decision = {D}", collective.Id, round, review.Decision);
            return (review.Decision.ToUpperInvariant(), review.Summary, review.Feedback);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HIVE] {Id} round {R}: REVIEW phase error", collective.Id, round);
            return ("CONTINUE", null, $"Review error: {ex.Message}");
        }
    }
}
