using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.Collective;

/// <summary>
/// Scoped CRUD service for Agent Collectives (Hive).
/// Content fields (objective, instructions, results, event messages) are stored on the bridge
/// node that owns the collective; the server keeps only metadata and scheduling state.
/// Legacy collectives with <see cref="AgentCollective.OriginNodeId"/> == null still read/write
/// content from the server DB.
/// </summary>
public class CollectiveService(
    IDbContextFactory<AppDbContext> dbFactory,
    ModelBridgeRegistry bridgeRegistry,
    BridgeHiveClient bridgeHive)
{
    // ── Create / Get / Delete ─────────────────────────────────────────────

    private static readonly Random _rng = Random.Shared;
    private const int OvermindAvatarCount = 29;

    /// <summary>Fired as the user types a rename in the Hive page (debounced, pre-persist) so other
    /// components sharing this circuit-scoped instance — e.g. the nav menu's hive list — can reflect
    /// the live name immediately instead of waiting for blur + a full list reload.</summary>
    public event Action<int, string>? CollectiveRenamed;
    public void NotifyRenamed(int collectiveId, string name) => CollectiveRenamed?.Invoke(collectiveId, name);

    public async Task<AgentCollective> CreateAsync(string userId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var avatarNum = _rng.Next(1, OvermindAvatarCount + 1);
        var originNodeId = GetDefaultNodeId(userId);

        var c = new AgentCollective
        {
            UserId             = userId,
            Name               = name.Trim(),
            OriginNodeId       = originNodeId,
            OvermindAvatarPath = $"avatars/overmind-{avatarNum}.png",
        };
        db.AgentCollectives.Add(c);
        await db.SaveChangesAsync();

        if (!string.IsNullOrEmpty(originNodeId))
        {
            await bridgeHive.EnsureCollectiveAsync(userId, c.Id, originNodeId);
        }

        return c;
    }

    public async Task<AgentCollective?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = await db.AgentCollectives
            .Include(c => c.Members).ThenInclude(m => m.SubAgent).ThenInclude(a => a.ToolStates)
            .Include(c => c.Members).ThenInclude(m => m.SubAgent).ThenInclude(a => a.SubAgentSkills).ThenInclude(s => s.Skill)
            .Include(c => c.Members).ThenInclude(m => m.EdgeNodes)
            .Include(c => c.Tasks)
            .Include(c => c.Events)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id);

        if (c == null) return null;
        await HydrateContentAsync(c);
        return c;
    }

    public async Task<List<AgentCollective>> GetListAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AgentCollectives
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = await db.AgentCollectives.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (c != null && !string.IsNullOrEmpty(c.OriginNodeId))
        {
            _ = bridgeHive.DeleteCollectiveAsync(c.UserId, c.Id, c.OriginNodeId);
        }
        await db.AgentCollectives.Where(c => c.Id == id).ExecuteDeleteAsync();
    }

    // ── Config ────────────────────────────────────────────────────────────

    public async Task UpdateConfigAsync(
        int id, string name, string objective,
        int? overmindSubAgentId, string? overmindSource, string? overmindModel,
        int maxRounds, bool requiresHumanApproval = false,
        CollectiveBehavior behavior = CollectiveBehavior.HiveMind)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = await db.AgentCollectives.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);

        if (c != null && !string.IsNullOrEmpty(c.OriginNodeId))
        {
            // Bridge-owned: content lives on the node.
            await bridgeHive.EnsureCollectiveAsync(c.UserId, id, c.OriginNodeId);
            await bridgeHive.UpdateCollectiveContentAsync(c.UserId, id, c.OriginNodeId, objective: objective);
            await db.AgentCollectives
                .Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Name,                 name.Trim())
                    .SetProperty(x => x.OvermindSubAgentId,   overmindSubAgentId)
                    .SetProperty(x => x.OvermindSourceName,   overmindSource)
                    .SetProperty(x => x.OvermindModelId,      overmindModel)
                    .SetProperty(x => x.MaxRounds,            maxRounds)
                    .SetProperty(x => x.RequiresHumanApproval, requiresHumanApproval)
                    .SetProperty(x => x.Behavior,             behavior)
                    .SetProperty(x => x.UpdatedAt,            DateTime.UtcNow));
        }
        else
        {
            // Legacy/server-stored.
            await db.AgentCollectives
                .Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Name,                 name.Trim())
                    .SetProperty(x => x.Objective,            objective)
                    .SetProperty(x => x.OvermindSubAgentId,   overmindSubAgentId)
                    .SetProperty(x => x.OvermindSourceName,   overmindSource)
                    .SetProperty(x => x.OvermindModelId,      overmindModel)
                    .SetProperty(x => x.MaxRounds,            maxRounds)
                    .SetProperty(x => x.RequiresHumanApproval, requiresHumanApproval)
                    .SetProperty(x => x.Behavior,             behavior)
                    .SetProperty(x => x.UpdatedAt,            DateTime.UtcNow));
        }
    }

    // ── Members ───────────────────────────────────────────────────────────

    public async Task<CollectiveMember> AddMemberAsync(int collectiveId, int subAgentId, string? roleLabel)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var member = new CollectiveMember
        {
            CollectiveId = collectiveId,
            SubAgentId   = subAgentId,
            RoleLabel    = string.IsNullOrWhiteSpace(roleLabel) ? null : roleLabel.Trim(),
        };
        db.CollectiveMembers.Add(member);
        await db.SaveChangesAsync();

        // Recompute auto-layout for all members
        await RecomputeLayoutAsync(collectiveId, db);
        return member;
    }

    public async Task RemoveMemberAsync(int memberId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var member = await db.CollectiveMembers.FirstOrDefaultAsync(m => m.Id == memberId);
        if (member == null) return;
        var collectiveId = member.CollectiveId;
        await db.CollectiveMembers.Where(m => m.Id == memberId).ExecuteDeleteAsync();
        await RecomputeLayoutAsync(collectiveId, db);
    }

    public async Task<List<CollectiveMember>> GetMembersAsync(int collectiveId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CollectiveMembers
            .Include(m => m.SubAgent).ThenInclude(a => a.ToolStates)
            .Include(m => m.SubAgent).ThenInclude(a => a.SubAgentSkills).ThenInclude(s => s.Skill)
            .Include(m => m.EdgeNodes)
            .AsSplitQuery()
            .AsNoTracking()
            .Where(m => m.CollectiveId == collectiveId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();
    }

    // ── Tasks ─────────────────────────────────────────────────────────────

    public async Task<List<CollectiveTask>> GetTasksAsync(int collectiveId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var tasks = await db.CollectiveTasks
            .AsNoTracking()
            .Where(t => t.CollectiveId == collectiveId)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();

        var c = await db.AgentCollectives.AsNoTracking().FirstOrDefaultAsync(c => c.Id == collectiveId);
        if (c != null && !string.IsNullOrEmpty(c.OriginNodeId))
        {
            var content = await bridgeHive.GetTasksAsync(c.UserId, collectiveId, c.OriginNodeId);
            foreach (var task in tasks)
            {
                var bridgeTask = content.FirstOrDefault(t => t.Id == BridgeHiveClient.TaskId(task.Id));
                if (bridgeTask != null)
                {
                    task.Title                = bridgeTask.Title;
                    task.Instruction          = bridgeTask.Instruction;
                    task.EffectiveInstruction = bridgeTask.EffectiveInstruction;
                    task.Result               = bridgeTask.Result;
                }
            }
        }
        return tasks;
    }

    /// <summary>Writes task content to the bridge for bridge-owned collectives.</summary>
    public async Task UpdateTaskContentAsync(
        int collectiveId, int taskId,
        string? title = null, string? instruction = null,
        string? effectiveInstruction = null, string? result = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = await db.AgentCollectives.AsNoTracking().FirstOrDefaultAsync(c => c.Id == collectiveId);
        if (c == null || string.IsNullOrEmpty(c.OriginNodeId)) return;

        await bridgeHive.UpsertTaskContentAsync(
            c.UserId, collectiveId, taskId, c.OriginNodeId,
            title, instruction, effectiveInstruction, result);
    }

    // ── Events ────────────────────────────────────────────────────────────

    public async Task<List<CollectiveEvent>> GetEventsAsync(int collectiveId, int? sinceId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.CollectiveEvents.AsNoTracking()
            .Where(e => e.CollectiveId == collectiveId);
        if (sinceId.HasValue)
            q = q.Where(e => e.Id > sinceId.Value);
        var events = await q.OrderByDescending(e => e.Id).Take(200).ToListAsync();

        var c = await db.AgentCollectives.AsNoTracking().FirstOrDefaultAsync(c => c.Id == collectiveId);
        if (c != null && !string.IsNullOrEmpty(c.OriginNodeId))
        {
            var content = await bridgeHive.GetEventsAsync(c.UserId, collectiveId, c.OriginNodeId);
            foreach (var ev in events)
            {
                var bridgeEvent = content.FirstOrDefault(e => e.Id == BridgeHiveClient.EventId(ev.Id));
                if (bridgeEvent != null)
                    ev.Message = bridgeEvent.Message;
            }
        }
        return events;
    }

    public async Task<CollectiveEvent> AppendEventAsync(
        int collectiveId, CollectiveEventType type, string message,
        int? actorMemberId = null, int? taskId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ev = new CollectiveEvent
        {
            CollectiveId  = collectiveId,
            Type          = type,
            Message       = message,
            ActorMemberId = actorMemberId,
            TaskId        = taskId,
            Timestamp     = DateTime.UtcNow,
        };
        db.CollectiveEvents.Add(ev);
        await db.SaveChangesAsync();

        var c = await db.AgentCollectives.AsNoTracking().FirstOrDefaultAsync(c => c.Id == collectiveId);
        if (c != null && !string.IsNullOrEmpty(c.OriginNodeId))
        {
            await bridgeHive.EnsureCollectiveAsync(c.UserId, collectiveId, c.OriginNodeId);
            await bridgeHive.AppendEventAsync(
                c.UserId, collectiveId, c.OriginNodeId,
                ev.Timestamp, type.ToString(), actorMemberId, taskId, message);
            ev.Message = ""; // server row stores only metadata
        }

        return ev;
    }

    // ── Synapse Memory ────────────────────────────────────────────────────

    public async Task SaveSynapseMemoryAsync(int collectiveId, string? memory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = await db.AgentCollectives.AsNoTracking().FirstOrDefaultAsync(c => c.Id == collectiveId);
        if (c == null) return;

        if (!string.IsNullOrEmpty(c.OriginNodeId))
        {
            await bridgeHive.EnsureCollectiveAsync(c.UserId, collectiveId, c.OriginNodeId);
            await bridgeHive.UpdateCollectiveContentAsync(
                c.UserId, collectiveId, c.OriginNodeId, synapseMemory: memory);
        }
        else
        {
            await db.AgentCollectives
                .Where(x => x.Id == collectiveId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.SynapseMemory, memory)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow));
        }
    }

    // ── Member gate ───────────────────────────────────────────────────────

    public async Task ToggleMemberGateAsync(int memberId, bool value)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.CollectiveMembers
            .Where(m => m.Id == memberId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.RequiresHumanApproval, value));
    }

    public async Task SetGateAfterResponseAsync(int memberId, bool gateAfterResponse)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.CollectiveMembers
            .Where(m => m.Id == memberId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.GateAfterResponse, gateAfterResponse));
    }

    // ── Edge nodes ────────────────────────────────────────────────────────

    public async Task<MemberEdgeNode> AddEdgeNodeAsync(int memberId, EdgeNodeType type, int position, string? config = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var node = new MemberEdgeNode { MemberId = memberId, NodeType = type, Position = position, Config = config };
        db.MemberEdgeNodes.Add(node);
        await db.SaveChangesAsync();
        return node;
    }

    public async Task RemoveEdgeNodeAsync(int nodeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.MemberEdgeNodes.Where(n => n.Id == nodeId).ExecuteDeleteAsync();
    }

    public async Task UpdateEdgeNodeConfigAsync(int nodeId, string? config)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.MemberEdgeNodes
            .Where(n => n.Id == nodeId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Config, config));
    }

    public async Task<List<MemberEdgeNode>> GetEdgeNodesAsync(int memberId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.MemberEdgeNodes
            .AsNoTracking()
            .Where(n => n.MemberId == memberId)
            .OrderBy(n => n.Position)
            .ToListAsync();
    }

    /// <summary>
    /// Parses a Condition node's config → (mode, value, negate).
    /// mode = "contains" (case-insensitive substring) or "llm" (a yes/no test judged by the Overmind).
    /// </summary>
    public static (string Mode, string Value, bool Negate) ParseCondition(string? config)
    {
        if (string.IsNullOrWhiteSpace(config)) return ("contains", "", false);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(config);
            var root   = doc.RootElement;
            var mode   = root.TryGetProperty("mode",   out var m) ? (m.GetString() ?? "contains") : "contains";
            var value  = root.TryGetProperty("value",  out var v) ? (v.GetString() ?? "") : "";
            var negate = root.TryGetProperty("negate", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.True;
            var known  = mode is "llm" or "regex" or "any" or "all" ? mode : "contains";
            return (known, value, negate);
        }
        catch { return ("contains", "", false); }
    }

    // Applies Transform edge nodes to an instruction in pipeline order (position ascending, before gate at 500)
    public static string ApplyTransforms(List<MemberEdgeNode> nodes, string instruction)
    {
        var result = instruction;
        foreach (var n in nodes.Where(n => n.NodeType == EdgeNodeType.Transform).OrderBy(n => n.Position))
        {
            var template = ExtractTemplate(n.Config);
            if (!string.IsNullOrWhiteSpace(template))
                result = template.Replace("{{original}}", result, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    private static string? ExtractTemplate(string? config)
    {
        if (string.IsNullOrWhiteSpace(config)) return null;
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(config);
            return doc.RootElement.TryGetProperty("template", out var t) ? t.GetString() : null;
        }
        catch { return null; }
    }

    // ── Canvas ────────────────────────────────────────────────────────────

    public async Task SaveCanvasAsync(int collectiveId, double zoom, double panX, double panY)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.AgentCollectives
            .Where(c => c.Id == collectiveId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.CanvasZoom, zoom)
                .SetProperty(c => c.CanvasPanX, panX)
                .SetProperty(c => c.CanvasPanY, panY));
    }

    public async Task SaveMemberPositionAsync(int memberId, double x, double y)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.CollectiveMembers
            .Where(m => m.Id == memberId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.CanvasX, x)
                .SetProperty(m => m.CanvasY, y));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private string? GetDefaultNodeId(string userId)
    {
        var node = bridgeRegistry.GetNodes(userId).FirstOrDefault();
        return node?.NodeId;
    }

    private async Task HydrateContentAsync(AgentCollective c)
    {
        if (string.IsNullOrEmpty(c.OriginNodeId)) return;

        var content = await bridgeHive.LoadContentAsync(c.UserId, c.Id, c.OriginNodeId);
        if (content == null) return;

        c.Objective     = content.Collective.Objective;
        c.ResultSummary = content.Collective.ResultSummary;
        c.LastFeedback  = content.Collective.LastFeedback;
        c.SynapseMemory = content.Collective.SynapseMemory;

        var taskByBridgeId = content.Tasks.ToDictionary(t => t.Id);
        foreach (var task in c.Tasks)
        {
            if (taskByBridgeId.TryGetValue(BridgeHiveClient.TaskId(task.Id), out var bt))
            {
                task.Title                = bt.Title;
                task.Instruction          = bt.Instruction;
                task.EffectiveInstruction = bt.EffectiveInstruction;
                task.Result               = bt.Result;
            }
        }

        var eventByBridgeId = content.Events.ToDictionary(e => e.Id);
        foreach (var ev in c.Events)
        {
            if (eventByBridgeId.TryGetValue(BridgeHiveClient.EventId(ev.Id), out var be))
                ev.Message = be.Message;
        }
    }

    // ── Auto-layout helper ────────────────────────────────────────────────

    /// <summary>
    /// Recomputes CanvasX/Y for all members: Overmind stays off-canvas (not a member row),
    /// drones spread horizontally at y=280, evenly spaced around x=400.
    /// </summary>
    private static async Task RecomputeLayoutAsync(int collectiveId, AppDbContext db)
    {
        var members = await db.CollectiveMembers
            .Where(m => m.CollectiveId == collectiveId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        var count = members.Count;
        if (count == 0) return;

        // centerX matches the Overmind node centre (left:350 + width 150/2 = 425); spacing clears the
        // 150px-wide drone blocks with breathing room now that they carry channel/model/bridge badges.
        // droneY sits far below the Overmind (edge start ~130) so inserted edge nodes (transform / gate /
        // condition) have room along the link and aren't hidden behind the now-larger node blocks.
        const double centerX = 425;
        const double droneY  = 494;   // ~5% closer to the Overmind than the original 520
        const double spacing = 200;

        const double halfNode = 75;   // node width 150 → CanvasX is the left edge, so shift by half to centre
        const double centerSlot = centerX - halfNode;   // CanvasX of a drone sitting directly under the Overmind

        // Only place members that have never been positioned (CanvasX/Y still at the (0,0) default). Members
        // the user has dragged keep their custom coordinates — adding or removing a drone must not reset them.
        var unplaced = members.Where(m => m.CanvasX == 0 && m.CanvasY == 0).ToList();
        if (unplaced.Count == 0) return;

        // Drones fan out symmetrically around the Overmind. Slot k sits at centerSlot + k*spacing; the fill
        // order is 0, +1, -1, +2, -2, … so new drones alternate right / left instead of all marching right.
        // Slots already occupied (by earlier or user-moved drones, matched to the nearest slot) are skipped.
        var occupied = members
            .Where(m => m.CanvasX != 0 || m.CanvasY != 0)
            .Select(m => (int)Math.Round((m.CanvasX - centerSlot) / spacing))
            .ToHashSet();

        foreach (var m in unplaced)
        {
            var k = NextFreeSlot(occupied);
            occupied.Add(k);
            m.CanvasX = centerSlot + k * spacing;
            m.CanvasY = droneY;
        }
        await db.SaveChangesAsync();
    }

    // Walk the fan order 0, +1, -1, +2, -2, … and return the first slot not already occupied.
    private static int NextFreeSlot(HashSet<int> occupied)
    {
        if (!occupied.Contains(0)) return 0;
        for (int d = 1; ; d++)
        {
            if (!occupied.Contains(d))  return d;
            if (!occupied.Contains(-d)) return -d;
        }
    }
}
