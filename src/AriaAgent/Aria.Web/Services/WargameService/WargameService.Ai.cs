using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aria.Agent;
using Aria.Harness.Formats;
using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;
using OpenAI.Chat;

namespace Aria.Web.Services.WargameService;

public partial class WargameService
{
    // ── System prompt ─────────────────────────────────────────────────────────

    private static string GenerateSystemPrompt(WargameFaction faction) => faction.Race switch
    {
        WargameRace.Empire =>
            $"You are {faction.Name}, an Empire of Man warband in Warhammer Fantasy. " +
            "Your soldiers are disciplined and strategic. Build Farms for food, a Barracks to recruit more troops, then march to war. " +
            "Move toward enemies aggressively. Build when it strengthens your position. NEVER idle.",
        WargameRace.Greenskins =>
            $"You are {faction.Name}, a Greenskins WAAAGH! in Warhammer Fantasy. " +
            "Da boyz live for da fight but even Orks need a Barracks to get MORE BOYZ! " +
            "Build fast, recruit fast, charge faster. NEVER idle — da warboss will execute you for cowardice!",
        WargameRace.Chaos =>
            $"You are {faction.Name}, Warriors of Chaos in Warhammer Fantasy. " +
            "You are slow but unstoppable. Build Farms and a Barracks to bolster your dark host before the final assault. " +
            "Advance relentlessly. Build to grow stronger. NEVER idle.",
        WargameRace.Undead =>
            $"You are {faction.Name}, Vampire Counts in Warhammer Fantasy. " +
            "Your swarms are fragile — you must recruit constantly. Build a Barracks immediately, then raise endless undead. " +
            "Always advance. Always recruit when possible. NEVER idle.",
        _ =>
            $"You are {faction.Name}. Build, expand, conquer. NEVER idle."
    };

    // ── LLM call — uses ChatClientFactory + UniversalReasoningHandler ────────────
    // Thinking tokens are stripped by the handler at the HTTP/SSE level, exactly
    // as Aria's chat does, so we always receive clean content regardless of model.

    private async Task<ChatClient?> GetOrBuildClientAsync(WargameFaction faction, CancellationToken ct)
    {
        if (_factionClients.TryGetValue(faction.Id, out var cached)) return cached;

        await _agentService.EnsureUserLocalSourcesLoadedAsync(faction.UserId);
        var availableSources = _agentService.GetSourcesForUser(faction.UserId);
        var source = availableSources.FirstOrDefault(s => s.Name == faction.SourceName);
        if (source == null)
        {
            _logger.LogWarning("[Wargame] Faction {Name}: source '{Source}' not found. Available: {All}",
                faction.Name, faction.SourceName,
                string.Join(", ", availableSources.Select(s => s.Name)));
            return null;
        }

        var model = faction.ModelId ?? source.Models.FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(model))
        {
            _logger.LogWarning("[Wargame] Faction {Name}: no model ID — set it in the faction config", faction.Name);
            return null;
        }

        // Detect thinking format once; AgentService caches the result per (source, model)
        var format = await _agentService.DetectThinkingFormatAsync(source.Name, model, ct, faction.UserId);
        _logger.LogInformation("[Wargame] Faction {Name}: thinking format = {Format}", faction.Name, format);

        // Per-faction buffer captures reasoning tokens at [DONE].
        // If the model only writes to reasoning_content (never to content), this is the
        // only place the decision text appears — ParseAction uses it as fallback.
        var buf = new StringBuilder();
        _reasoningBuffers[faction.Id] = buf;

        var handler = new UniversalReasoningHandler
        {
            InnerHandler       = new HttpClientHandler(),
            OnReasoningContent = text => buf.Append(text),
            StartsInThinkMode  = format == ThinkingFormat.StartsInThinkMode
        };

        var client = ChatClientFactory.Build(source, model, handler);
        _factionClients[faction.Id] = client;
        return client;
    }

    private async Task<string?> CallLlmAsync(WargameFaction faction, string userPrompt, CancellationToken ct)
    {
        // Reset reasoning buffer before each call
        if (_reasoningBuffers.TryGetValue(faction.Id, out var reasoningBuf))
            reasoningBuf.Clear();

        var client = await GetOrBuildClientAsync(faction, ct);
        if (client == null) return null;

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(GenerateSystemPrompt(faction)),
            new UserChatMessage(userPrompt)
        };

        var options = new ChatCompletionOptions { MaxOutputTokenCount = 2048, Temperature = 0.7f };

        try
        {
            var sb = new StringBuilder();
            await foreach (var update in client.CompleteChatStreamingAsync(messages, options, ct))
                foreach (var part in update.ContentUpdate)
                    sb.Append(part.Text);

            var response = sb.ToString().Trim();

            // Fallback: model only wrote to reasoning_content (handler captured it via callback).
            // Parse action from the reasoning text — the decision is always deliberated there.
            if (string.IsNullOrWhiteSpace(response) &&
                _reasoningBuffers.TryGetValue(faction.Id, out var rb) && rb.Length > 0)
            {
                response = rb.ToString();
                LogToFile($"NOTE: content empty — using reasoning fallback ({rb.Length} chars)");
            }

            _logger.LogInformation("[Wargame] Faction {Name} response: {Resp}", faction.Name,
                response.Length > 200 ? response[..200] + "…" : response);
            LogToFile($"RESPONSE: {response}");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Wargame] Faction {Name}: LLM call failed", faction.Name);
            return null;
        }
    }

    private static GameAction? ParseAction(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var stripped = Regex.Replace(raw, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);
            stripped = Regex.Replace(stripped, @"^[\s\S]*?</think>", "", RegexOptions.IgnoreCase);
            stripped = stripped.Trim();

            // Try standard JSON parse first
            var jsonMatch = Regex.Match(stripped, @"\{[^{}]+\}");
            if (jsonMatch.Success)
            {
                try
                {
                    var ga = JsonSerializer.Deserialize<GameAction>(jsonMatch.Value,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (ga != null) return ga;
                }
                catch { /* fall through to regex extraction */ }
            }

            // Fallback: extract fields individually via regex (handles truncated/malformed JSON)
            var actionM = Regex.Match(stripped, @"""action""\s*:\s*""(\w+)""");
            if (!actionM.Success) return null;

            return new GameAction(
                Action:   actionM.Groups[1].Value.ToLower(),
                UnitId:   ExtractInt(stripped, "unit_id"),
                ToX:      ExtractInt(stripped, "to_x"),
                ToY:      ExtractInt(stripped, "to_y"),
                Building: ExtractStr(stripped, "building")
            );
        }
        catch { return null; }
    }

    private static int? ExtractInt(string s, string key)
    {
        var m = Regex.Match(s, $@"""{key}""\s*:\s*(-?\d+)""");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    private static string? ExtractStr(string s, string key)
    {
        var m = Regex.Match(s, $@"""{key}""\s*:\s*""([^""]+)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    // ── Context compaction ────────────────────────────────────────────────────

    private async Task CompactContextAsync(
        WargameFaction faction,
        List<WargameTurnLog> recentLogs,
        CancellationToken ct)
    {
        if (recentLogs.Count == 0) return;

        var history = string.Join("\n", recentLogs.OrderBy(l => l.TurnNumber)
            .Select(l => $"T{l.TurnNumber}: {l.Summary}"));

        var prompt =
            "Summarize your recent battle history in 2-3 concise sentences. " +
            "Focus on your strategic position, key victories or losses, and current threat level.\n\n" +
            history;

        var summary = await CallLlmAsync(faction, prompt, ct);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            // Strip thinking tokens from the summary text
            var clean = Regex.Replace(summary, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"^[\s\S]*?</think>", "", RegexOptions.IgnoreCase);
            faction.CompactedContext = clean.Trim();
        }
    }

    // ── Situation report (used by WargameTools) ───────────────────────────────

    public string BuildSituationReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// WAR.PLANNER — STRATEGIC SITUATION REPORT");
        sb.AppendLine();

        if (ActiveMap == null)
        {
            sb.AppendLine("No battlefield configured. Generate a map from the /wargame page.");
            return sb.ToString();
        }

        var status = ActiveMap.IsRunning ? "RUNNING" : (WinnerName != null ? "ENDED" : "PAUSED");
        sb.AppendLine($"Turn {ActiveMap.CurrentTurn}  |  Status: {status}  |  Map: {ActiveMap.Width}×{ActiveMap.Height}");
        if (WinnerName != null)
            sb.AppendLine($"VICTOR: {WinnerName.ToUpper()}");
        sb.AppendLine();

        var tilesByFaction = Tiles.GroupBy(t => t.OwnerFactionId ?? 0)
                                  .ToDictionary(g => g.Key, g => g.Count());

        foreach (var f in Factions)
        {
            var (movePoints, startHp) = RaceStats.Get(f.Race);
            var aliveLabel = f.IsAlive ? "ALIVE" : "ELIMINATED";
            sb.AppendLine($"{f.Name.ToUpper()} ({f.Race}) — {aliveLabel}");
            sb.AppendLine($"  Units: {Units.Count(u => u.FactionId == f.Id)}  " +
                          $"Tiles: {tilesByFaction.GetValueOrDefault(f.Id, 0)}  " +
                          $"Buildings: {Buildings.Count(b => b.FactionId == f.Id)}");
            sb.AppendLine($"  Resources: 🪵{f.Wood}  ⛏{f.Stone}  🌾{f.Food}  💰{f.Gold}");
            var bldgs = Buildings.Where(b => b.FactionId == f.Id).ToList();
            if (bldgs.Count > 0)
                sb.AppendLine($"  Buildings: {string.Join("  ", bldgs.Select(b => $"{b.Type}@({b.X},{b.Y})"))}");
            var fUnits = Units.Where(u => u.FactionId == f.Id).ToList();
            if (fUnits.Count > 0)
                sb.AppendLine($"  Unit positions: {string.Join("  ", fUnits.Select(u => $"u{u.Id}({u.X},{u.Y}) hp:{u.Health}/{u.MaxHealth}"))}");
            sb.AppendLine($"  Race stats: {movePoints} move(s)/turn, {startHp} HP per unit");
            sb.AppendLine();
        }

        if (RecentLogs.Count > 0)
        {
            sb.AppendLine("RECENT ENGAGEMENTS:");
            foreach (var log in RecentLogs.Take(10))
            {
                var fname = Factions.FirstOrDefault(f => f.Id == log.FactionId)?.Name ?? "?";
                sb.AppendLine($"  T{log.TurnNumber} {fname.ToUpper()} — {log.Summary}");
            }
        }

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<(int X, int Y)> GetAdjacentCoords(
        int x, int y,
        Dictionary<(int X, int Y), WargameTile> tileDict,
        int range = 1)
    {
        for (int dx = -range; dx <= range; dx++)
        for (int dy = -range; dy <= range; dy++)
        {
            var coord = (x + dx, y + dy);
            if (tileDict.ContainsKey(coord))
                yield return coord;
        }
    }
}
