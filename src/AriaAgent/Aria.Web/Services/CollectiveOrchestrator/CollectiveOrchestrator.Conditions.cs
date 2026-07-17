using System.Text;
using System.Text.Json;
using Aria.Web.Data;
using Microsoft.Extensions.AI;

namespace Aria.Web.Services.CollectiveOrchestrator;

public partial class CollectiveOrchestrator
{
    // Returns true if the drone should run; false if a Condition node says skip it.
    // "contains" conditions are evaluated locally; "llm" conditions ask the Overmind for a YES/NO.
    private async Task<bool> EvaluateConditionsAsync(
        AgentCollective collective, CollectiveMember member, string instruction, CancellationToken ct)
    {
        foreach (var node in member.EdgeNodes
            .Where(n => n.NodeType == EdgeNodeType.Condition).OrderBy(n => n.Position))
        {
            var (mode, value, negate) = CollectiveService.ParseCondition(node.Config);
            if (string.IsNullOrWhiteSpace(value)) continue; // empty condition is a no-op

            bool pass;
            if (mode == "llm")
            {
                var history = new List<ChatMessage>
                {
                    new(ChatRole.System,
                        "You judge whether a drone fits a task. Reply on ONE line as " +
                        "'YES — <brief reason>' or 'NO — <brief reason>'. Keep the reason to one short sentence.")
                };
                // Include the drone's capabilities so the condition can judge *fit*, not just the task
                // (e.g. "is this drone suited to handle this?"). Opt-in: only runs for drones you give
                // an llm condition — there is no automatic per-drone fit-check.
                var answer = await _executor.RunHeadlessAsync(
                    userId: collective.UserId, subAgentId: collective.OvermindSubAgentId,
                    prompt: $"DRONE:\n{BuildRosterLine(member).Trim()}\n\n" +
                            $"CONDITION: {value}\n\nTASK:\n{instruction}\n\n" +
                            "Considering the DRONE's role, persona, tools and skills, does it satisfy the " +
                            "CONDITION for this TASK? Answer 'YES — reason' or 'NO — reason'.",
                    sourceName: collective.OvermindSourceName, modelId: collective.OvermindModelId,
                    seedHistory: history, ct: ct);

                var verdict = answer.TrimStart().StartsWith("YES", StringComparison.OrdinalIgnoreCase);
                var reason  = ExtractJudgeReason(answer);
                pass = verdict;

                // Surface the AI judgment in the timeline so the user sees *why* a drone was kept/dropped.
                await AppendEventAsync(collective.Id, CollectiveEventType.Reviewed,
                    $"Overmind deemed {member.SubAgent.DisplayName} {(verdict ? "FIT" : "NOT FIT")} — {reason}",
                    member.Id, null);
                FireChanged(collective.Id);
            }
            else
            {
                pass = EvaluateLocalCondition(mode, value, instruction);
            }

            if (negate) pass = !pass;
            if (!pass) return false; // any failing condition skips the drone
        }
        return true;
    }

    // Pulls the human-readable reason out of a "YES — reason" / "NO — reason" / "<id> — reason" judge reply.
    private static string ExtractJudgeReason(string? answer)
    {
        var t = (answer ?? "").Trim();
        int sep = t.IndexOfAny(new[] { '—', '-', ':' });
        if (sep >= 0 && sep < t.Length - 1)
            t = t[(sep + 1)..].Trim();
        else if (t.StartsWith("YES", StringComparison.OrdinalIgnoreCase))
            t = t[3..].Trim();
        else if (t.StartsWith("NO", StringComparison.OrdinalIgnoreCase))
            t = t[2..].Trim();
        if (t.Length > 240) t = t[..240] + "…";
        return string.IsNullOrWhiteSpace(t) ? "no reason given" : t;
    }

    // Deterministic (non-LLM) condition modes, evaluated against the dispatched instruction.
    private static bool EvaluateLocalCondition(string mode, string value, string instruction)
    {
        switch (mode)
        {
            case "regex":
                try { return System.Text.RegularExpressions.Regex.IsMatch(
                        instruction, value, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
                catch { return false; } // invalid pattern → treat as not-matched (drone skipped)

            case "any":
                return value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)
                            .Any(k => instruction.Contains(k, StringComparison.OrdinalIgnoreCase));

            case "all":
                var keys = value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                return keys.Count > 0 && keys.All(k => instruction.Contains(k, StringComparison.OrdinalIgnoreCase));

            default: // "contains"
                return instruction.Contains(value, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Describes a drone for the Overmind's plan prompt so it can judge fit: role, persona summary,
    // and the NAMES of the tools/skills the drone has (never the full skill content).
    private static string BuildRosterLine(CollectiveMember m)
    {
        var a  = m.SubAgent;
        var sb = new StringBuilder();
        sb.Append($"  memberId={m.Id} name=\"{a.DisplayName}\" role=\"{m.RoleLabel ?? a.ArchetypeName}\"");

        var persona = !string.IsNullOrWhiteSpace(a.UserDirectives) ? a.UserDirectives! : a.GeneratedPersonality;
        if (!string.IsNullOrWhiteSpace(persona))
        {
            persona = persona.Replace("\n", " ").Trim();
            if (persona.Length > 200) persona = persona[..200] + "…";
            sb.Append($"\n      persona: {persona}");
        }

        var tools = a.ToolStates.Where(t => t.Enabled)
                                .Select(t => ToolRegistry.Get(t.ToolId)?.Label ?? t.ToolId)
                                .ToList();
        if (!string.IsNullOrWhiteSpace(a.EnabledMcpNamesJson))
        {
            try
            {
                var names = JsonSerializer.Deserialize<List<string>>(a.EnabledMcpNamesJson!);
                if (names != null) tools.AddRange(names.Select(n => $"MCP:{n}"));
            }
            catch { }
        }
        if (tools.Count > 0)
            sb.Append($"\n      tools: {string.Join(", ", tools)}");

        var skills = a.SubAgentSkills
            .Where(s => s.Skill != null && !string.IsNullOrWhiteSpace(s.Skill.Name))
            .Select(s => s.Skill.Name).ToList();
        if (skills.Count > 0)
            sb.Append($"\n      skills: {string.Join(", ", skills)}");

        return sb.ToString();
    }
}
