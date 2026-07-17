using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.WargameService;

public partial class WargameService
{
    // ── Game loop ─────────────────────────────────────────────────────────────

    private void StartLoop()
    {
        _loopCts?.Cancel();
        _loopCts = new CancellationTokenSource();
        _ = Task.Run(() => RunLoopAsync(_loopCts.Token));
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var interval = ActiveMap?.TurnIntervalSeconds ?? 90;
            NextTurnAt   = DateTime.UtcNow.AddSeconds(interval);
            OnStateChanged?.Invoke();

            try { await Task.Delay(TimeSpan.FromSeconds(interval), ct); }
            catch (OperationCanceledException) { break; }

            if (ct.IsCancellationRequested) break;

            await ProcessTurnAsync(ct);
        }

        NextTurnAt = null;
    }

    // ── Public manual trigger (used by debug API) ─────────────────────────────

    public async Task TriggerTurnAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var map = await db.WargameMaps.FirstOrDefaultAsync();
        if (map == null) return;
        map.IsRunning = true;   // ensure turn processing runs even when paused
        await db.SaveChangesAsync();
        await ProcessTurnAsync(CancellationToken.None);
        // Restore IsRunning to whatever it was
        await using var db2 = await _dbFactory.CreateDbContextAsync();
        var map2 = await db2.WargameMaps.FirstOrDefaultAsync();
        if (map2 != null) { map2.IsRunning = ActiveMap?.IsRunning ?? false; await db2.SaveChangesAsync(); }
        await RefreshStateAsync();
        OnStateChanged?.Invoke();
    }

    // ── Turn processing ───────────────────────────────────────────────────────

    private async Task ProcessTurnAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var map = await db.WargameMaps.FirstOrDefaultAsync(ct);
        if (map == null || !map.IsRunning) return;

        map.CurrentTurn++;
        await db.SaveChangesAsync(ct);

        var factions = await db.WargameFactions
            .Where(f => f.IsAlive)
            .Include(f => f.Units)
            .Include(f => f.Buildings)
            .AsSplitQuery()
            .ToListAsync(ct);

        var tiles = await db.WargameTiles
            .Where(t => t.MapId == map.Id)
            .ToListAsync(ct);

        var tileDict = tiles.ToDictionary(t => (t.X, t.Y));

        // Resource income phase — runs before any AI acts this turn
        foreach (var faction in factions)
        {
            var ownedTileCount = tiles.Count(t => t.OwnerFactionId == faction.Id);
            faction.Gold  += Math.Max(1, ownedTileCount / 3); // 1 gold per 3 owned tiles
            foreach (var b in faction.Buildings)
            {
                switch (b.Type)
                {
                    case BuildingType.Keep:       faction.Gold  += 2; break;
                    case BuildingType.Farm:        faction.Food  += 3; break;
                    case BuildingType.LumberMill:  faction.Wood  += 3; break;
                }
            }
        }
        await db.SaveChangesAsync(ct);

        foreach (var faction in factions)
        {
            if (ct.IsCancellationRequested) break;

            StatusText = $"// TURN {map.CurrentTurn} — {faction.Name.ToUpper()} COGITATING...";
            OnStateChanged?.Invoke();

            try
            {
                var recentLogs = await db.WargameTurnLogs
                    .Where(l => l.FactionId == faction.Id)
                    .OrderByDescending(l => l.TurnNumber)
                    .Take(5)
                    .ToListAsync(ct);

                var allUnits = await db.WargameUnits
                    .Include(u => u.Faction)
                    .Where(u => u.Faction.IsAlive)
                    .ToListAsync(ct);

                var allBuildings = await db.WargameBuildings.ToListAsync(ct);

                var (movePoints, _) = RaceStats.Get(faction.Race);
                var prompt  = BuildPrompt(faction, tiles, tileDict, allUnits, allBuildings, map.CurrentTurn, recentLogs, movePoints);

                LogToFile($"--- T{map.CurrentTurn} {faction.Name.ToUpper()} ({faction.Category}) ---");
                LogToFile($"PROMPT:\n{prompt}");

                var raw     = await CallLlmAsync(faction, prompt, ct);
                var action  = ParseAction(raw);
                var parsedStr = action == null ? "null" : $"{action.Action} unit={action.UnitId} to=({action.ToX},{action.ToY})";
                _logger.LogInformation("[Wargame] Faction {Name} parsed action: {Action}", faction.Name, parsedStr);
                LogToFile($"PARSED: {parsedStr}");

                var summary = await ExecuteActionAsync(faction, action, tileDict, allUnits, allBuildings, db, map.CurrentTurn, ct);
                LogToFile($"RESULT: {summary}");

                faction.TurnCount++;

                // Context compaction every 10 turns — asks the faction's LLM to
                // compress its accumulated history into a short strategic summary.
                // No dedicated C# package exists for this; we use a direct LLM call.
                if (faction.TurnCount % 10 == 0)
                    await CompactContextAsync(faction, recentLogs, ct);

                db.WargameTurnLogs.Add(new WargameTurnLog
                {
                    FactionId  = faction.Id,
                    TurnNumber = map.CurrentTurn,
                    ActionJson = raw ?? "{}",
                    Summary    = summary,
                    CreatedAt  = DateTime.UtcNow
                });

                await db.SaveChangesAsync(ct);

                // Check elimination
                var remaining = await db.WargameUnits.CountAsync(u => u.FactionId == faction.Id, ct);
                if (remaining == 0)
                {
                    faction.IsAlive = false;
                    // Release owned tiles
                    await db.WargameTiles
                        .Where(t => t.OwnerFactionId == faction.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.OwnerFactionId, (int?)null), ct);
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Turn processing failed for faction {Faction}", faction.Name);
            }
        }

        // Game-end check
        var survivors = factions.Where(f => f.IsAlive).ToList();
        if (survivors.Count <= 1)
        {
            map.IsRunning = false;
            WinnerName = survivors.Count == 1 ? survivors[0].Name : null;
            StatusText = WinnerName != null
                ? $"// VICTORY — {WinnerName.ToUpper()} CONQUERS ALL"
                : "// DRAW — TOTAL ANNIHILATION";
            await db.SaveChangesAsync(ct);
            _loopCts?.Cancel();
        }
        else
        {
            StatusText = $"// TURN {map.CurrentTurn} COMPLETE";
        }

        await RefreshStateAsync();
        OnStateChanged?.Invoke();
    }

    // ── Prompt builder ────────────────────────────────────────────────────────

    // ── Building helpers ──────────────────────────────────────────────────────
    private static readonly Dictionary<string, (int Wood, int Stone, int Food, int Gold)> BuildCosts = new()
    {
        ["Farm"]       = (4, 0, 0, 0),
        ["LumberMill"] = (0, 4, 0, 0),
        ["Barracks"]   = (5, 4, 0, 0),
    };
    private static bool CanAfford(WargameFaction f, (int Wood, int Stone, int Food, int Gold) cost) =>
        f.Wood >= cost.Wood && f.Stone >= cost.Stone && f.Food >= cost.Food && f.Gold >= cost.Gold;

    private static string BuildPrompt(
        WargameFaction faction,
        List<WargameTile> tiles,
        Dictionary<(int X, int Y), WargameTile> tileDict,
        List<WargameUnit> allUnits,
        List<WargameBuilding> allBuildings,
        int turn,
        List<WargameTurnLog> recentLogs,
        int movementPoints = 2)
    {
        var myUnits    = faction.Units.ToList();
        var enemyUnits = allUnits.Where(u => u.FactionId != faction.Id).ToList();

        static IEnumerable<(int X, int Y)> Orthogonal(int x, int y) =>
            new[] { (x-1,y), (x+1,y), (x,y-1), (x,y+1) };

        // Priority: attack enemy (3) > unclaimed tile (2) > enemy-owned tile (1) > own tile patrol (0)
        static int MovePriority(WargameTile t, WargameFaction faction, List<WargameUnit> enemies)
        {
            if (enemies.Any(e => e.X == t.X && e.Y == t.Y)) return 3;
            if (t.OwnerFactionId == null)                    return 2;
            if (t.OwnerFactionId != faction.Id)              return 1;
            return 0;
        }

        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(faction.CompactedContext))
            sb.AppendLine($"Strategic memory: {faction.CompactedContext}");

        // Determine which of our units is closest to any enemy — flag it for the model
        var closestUnit = myUnits
            .Select(u => (u, dist: enemyUnits.Count == 0 ? int.MaxValue
                : enemyUnits.Min(e => Math.Abs(u.X - e.X) + Math.Abs(u.Y - e.Y))))
            .OrderBy(x => x.dist)
            .FirstOrDefault();

        sb.AppendLine($"Turn {turn}. You own {tiles.Count(t => t.OwnerFactionId == faction.Id)} tiles, have {myUnits.Count} unit(s).");

        if (recentLogs.Count > 0)
        {
            sb.Append("Recent: ");
            sb.AppendLine(string.Join(" | ", recentLogs.OrderByDescending(l => l.TurnNumber)
                .Take(3).Select(l => $"T{l.TurnNumber}: {l.Summary}")));
        }

        sb.AppendLine();
        var closestUnitHint = closestUnit.u != null && enemyUnits.Count > 0
            ? $" ← MOVE THIS ONE FIRST (closest to enemy, dist={closestUnit.dist})" : "";
        sb.AppendLine($"YOUR UNITS (move unit {closestUnit.u?.Id ?? 0}{closestUnitHint}):");

        GameAction? bestAction   = null;
        int         bestPriority = -1;

        foreach (var u in myUnits)
        {
            // All orthogonal moves: in-bounds, not mountain, not occupied by own unit
            var validMoves = Orthogonal(u.X, u.Y)
                .Where(c => tileDict.TryGetValue(c, out var t)
                            && t.Type != TileType.Mountains
                            && !myUnits.Any(m => m.X == c.X && m.Y == c.Y))
                .Select(c =>
                {
                    var t    = tileDict[c];
                    var enemy = enemyUnits.FirstOrDefault(e => e.X == c.X && e.Y == c.Y);
                    var pri  = MovePriority(t, faction, enemyUnits);
                    var label = pri == 3 ? $"*** ATTACK {enemy!.Faction.Name} hp:{enemy.Health} ***"
                              : pri == 2 ? "unclaimed (expand)"
                              : pri == 1 ? "enemy territory (capture)"
                              :            "own tile (reposition)";
                    return (c.X, c.Y, label, pri);
                })
                .OrderByDescending(m => m.pri)
                .ToList();

            var movesStr = validMoves.Count == 0
                ? "BLOCKED (no valid moves)"
                : string.Join(", ", validMoves.Select(m => $"({m.X},{m.Y})={m.label}"));

            sb.AppendLine($"  unit {u.Id} @ ({u.X},{u.Y}) hp:{u.Health} → {movesStr}");

            if (validMoves.Count > 0 && validMoves[0].pri > bestPriority)
            {
                bestPriority = validMoves[0].pri;
                bestAction   = new GameAction("move", u.Id, validMoves[0].X, validMoves[0].Y);
            }
        }

        // Resources + buildings
        var myBuildings = allBuildings.Where(b => b.FactionId == faction.Id).ToList();
        var buildingSet = myBuildings.Select(b => (b.X, b.Y)).ToHashSet();
        sb.AppendLine();
        sb.AppendLine($"RESOURCES: 🪵 Wood:{faction.Wood}  ⛏ Stone:{faction.Stone}  🌾 Food:{faction.Food}  💰 Gold:{faction.Gold}");
        if (myBuildings.Count > 0)
            sb.AppendLine("YOUR BUILDINGS: " + string.Join(", ", myBuildings.Select(b => $"{b.Type}@({b.X},{b.Y})")));

        // Affordable builds on owned empty tiles
        var ownedEmpty = tiles
            .Where(t => t.OwnerFactionId == faction.Id && !buildingSet.Contains((t.X, t.Y)))
            .Take(3).ToList();
        var affordable = BuildCosts
            .Where(kv => CanAfford(faction, kv.Value))
            .Select(kv => $"{kv.Key}(costs 🪵{kv.Value.Wood} ⛏{kv.Value.Stone})")
            .ToList();
        if (affordable.Count > 0 && ownedEmpty.Count > 0)
        {
            var tile0 = ownedEmpty[0];
            sb.AppendLine($"CAN BUILD on owned tiles — pick one:");
            foreach (var kv in BuildCosts.Where(kv => CanAfford(faction, kv.Value)))
                sb.AppendLine($"  {{\"action\":\"build\",\"building\":\"{kv.Key}\",\"to_x\":{tile0.X},\"to_y\":{tile0.Y}}}  [{kv.Key}: 🪵{kv.Value.Wood} ⛏{kv.Value.Stone}]");
        }

        // Recruit if Barracks available
        var barracks = myBuildings.Where(b => b.Type == BuildingType.Barracks).ToList();
        if (barracks.Count > 0 && faction.Food >= 4 && faction.Gold >= 3)
        {
            var bar = barracks[0];
            if (!allUnits.Any(u => u.X == bar.X && u.Y == bar.Y))
                sb.AppendLine($"CAN RECRUIT (costs 🌾4 💰3): {{\"action\":\"recruit\",\"to_x\":{bar.X},\"to_y\":{bar.Y}}}");
        }

        // Show all known enemies with distance
        if (enemyUnits.Count > 0)
        {
            var nearest = enemyUnits
                .Select(e => (e, dist: myUnits.Min(u => Math.Abs(u.X - e.X) + Math.Abs(u.Y - e.Y))))
                .OrderBy(x => x.dist)
                .Take(4)
                .ToList();

            sb.AppendLine();
            sb.AppendLine("ENEMY POSITIONS (move toward them):");
            foreach (var (e, dist) in nearest)
                sb.AppendLine($"  {e.Faction.Name} unit at ({e.X},{e.Y}) hp:{e.Health} — {dist} tiles away");
        }

        sb.AppendLine();
        sb.AppendLine($"MOVEMENT: {movementPoints} step(s) this turn. Move ONE unit up to {movementPoints} steps.");
        sb.AppendLine("OUTPUT RULES:");
        sb.AppendLine("  - Output EXACTLY one JSON object, single line, no markdown, no explanation.");
        if (movementPoints > 1)
        {
            sb.AppendLine($"  - {{\"action\":\"move\",\"unit_id\":N,\"path\":[[x1,y1],[x2,y2],...]}}  ← up to {movementPoints} steps, each adjacent (N/S/E/W only)");
            sb.AppendLine("  - Each step MUST be adjacent (distance 1) to the previous position. No diagonals.");
        }
        else
        {
            sb.AppendLine("  - {\"action\":\"move\",\"unit_id\":N,\"to_x\":X,\"to_y\":Y}  ← MUST be one of the coords listed above.");
            sb.AppendLine("  - NO diagonal moves. Only N/S/E/W.");
        }
        sb.AppendLine("  - {\"action\":\"idle\"} ONLY if every unit shows BLOCKED. Otherwise FORBIDDEN.");
        sb.AppendLine();

        string exampleJson;
        if (bestAction != null && movementPoints > 1)
        {
            // Build a 2-step example path toward the nearest enemy
            exampleJson = $"{{\"action\":\"move\",\"unit_id\":{bestAction.UnitId},\"path\":[[{bestAction.ToX},{bestAction.ToY}]]}}";
        }
        else if (bestAction != null)
        {
            exampleJson = $"{{\"action\":\"move\",\"unit_id\":{bestAction.UnitId},\"to_x\":{bestAction.ToX},\"to_y\":{bestAction.ToY}}}";
        }
        else
        {
            exampleJson = "{\"action\":\"idle\"}";
        }
        sb.AppendLine($"Your move: {exampleJson}");

        return sb.ToString();
    }

    // ── Action execution ──────────────────────────────────────────────────────

    private async Task<string> ExecuteActionAsync(
        WargameFaction faction,
        GameAction? action,
        Dictionary<(int X, int Y), WargameTile> tileDict,
        List<WargameUnit> allUnits,
        List<WargameBuilding> allBuildings,
        AppDbContext db,
        int currentTurn,
        CancellationToken ct)
    {
        var myUnits = allUnits.Where(u => u.FactionId == faction.Id).ToList();

        if (action == null || action.Action == "idle")
            return "idled";

        // ── Build ──────────────────────────────────────────────────────────────
        if (action.Action == "build" && !string.IsNullOrWhiteSpace(action.Building) &&
            action.ToX.HasValue && action.ToY.HasValue)
        {
            var bname = action.Building;
            if (!BuildCosts.TryGetValue(bname, out var cost))
                return $"unknown building type '{bname}'";
            if (!CanAfford(faction, cost))
                return $"cannot afford {bname} (need 🪵{cost.Wood} ⛏{cost.Stone})";

            int bx = action.ToX.Value, by = action.ToY.Value;
            if (!tileDict.TryGetValue((bx, by), out var bTile) || bTile.OwnerFactionId != faction.Id)
                return $"cannot build at ({bx},{by}) — not owned";
            if (allBuildings.Any(b => b.X == bx && b.Y == by))
                return $"tile ({bx},{by}) already has a building";

            if (!Enum.TryParse<BuildingType>(bname, out var btype))
                return $"invalid building type";

            faction.Wood  -= cost.Wood;
            faction.Stone -= cost.Stone;
            faction.Food  -= cost.Food;
            faction.Gold  -= cost.Gold;

            var newBuilding = new WargameBuilding { FactionId = faction.Id, X = bx, Y = by, Type = btype, BuiltTurn = currentTurn };
            db.WargameBuildings.Add(newBuilding);
            allBuildings.Add(newBuilding);
            return $"built {bname} at ({bx},{by})";
        }

        // ── Recruit ────────────────────────────────────────────────────────────
        if (action.Action == "recruit" && action.ToX.HasValue && action.ToY.HasValue)
        {
            int rx = action.ToX.Value, ry = action.ToY.Value;
            if (!allBuildings.Any(b => b.FactionId == faction.Id && b.X == rx && b.Y == ry && b.Type == BuildingType.Barracks))
                return $"no Barracks at ({rx},{ry})";
            if (allUnits.Any(u => u.X == rx && u.Y == ry))
                return $"tile ({rx},{ry}) occupied — cannot recruit";
            if (faction.Food < 4 || faction.Gold < 3)
                return $"cannot recruit (need 🌾4 💰3, have 🌾{faction.Food} 💰{faction.Gold})";

            faction.Food -= 4;
            faction.Gold -= 3;
            var (_, startHp) = RaceStats.Get(faction.Race);
            var newUnit = new WargameUnit { FactionId = faction.Id, X = rx, Y = ry, Health = startHp, MaxHealth = startHp };
            db.WargameUnits.Add(newUnit);
            allUnits.Add(newUnit);
            return $"recruited new unit at ({rx},{ry})";
        }

        if (action.Action == "fortify")
        {
            var unit = myUnits.FirstOrDefault(u => u.Id == action.UnitId) ?? myUnits[0];
            // Fortify: claim the tile the unit is standing on
            if (tileDict.TryGetValue((unit.X, unit.Y), out var standTile) && standTile.OwnerFactionId != faction.Id)
            {
                standTile.OwnerFactionId = faction.Id;
                return $"unit {unit.Id} fortified ({unit.X},{unit.Y})";
            }
            return $"unit {unit.Id} held position";
        }

        if (action.Action == "move" && action.UnitId.HasValue)
        {
            var unit = myUnits.FirstOrDefault(u => u.Id == action.UnitId);
            if (unit == null) return "invalid unit — idled";

            // Build the step list — path takes priority, then single to_x/to_y
            var steps = new List<(int X, int Y)>();
            if (action.Path is { Count: > 0 })
            {
                foreach (var p in action.Path)
                    if (p.Length >= 2) steps.Add((p[0], p[1]));
            }
            else if (action.ToX.HasValue && action.ToY.HasValue)
            {
                steps.Add((action.ToX.Value, action.ToY.Value));
            }

            if (steps.Count == 0) return $"unit {unit.Id} no steps provided — idled";

            var outcomes = new List<string>();
            foreach (var (tx, ty) in steps)
            {
                // Validate adjacency
                if (Math.Abs(tx - unit.X) + Math.Abs(ty - unit.Y) != 1)
                {
                    outcomes.Add($"invalid step ({tx},{ty}) — stopped");
                    break;
                }

                if (!tileDict.TryGetValue((tx, ty), out var target))
                    break; // hit map edge — keep progress, stop silently

                if (target.Type == TileType.Mountains)
                {
                    outcomes.Add($"blocked by mountains at ({tx},{ty})");
                    break;
                }

                if (allUnits.Any(u => u.FactionId == faction.Id && u.X == tx && u.Y == ty))
                {
                    outcomes.Add($"ally at ({tx},{ty}) — stopped");
                    break;
                }

                // Enemy on target → combat, then stop moving
                var enemy = allUnits.FirstOrDefault(u => u.FactionId != faction.Id && u.X == tx && u.Y == ty);
                if (enemy != null)
                {
                    enemy.Health--;
                    unit.Health--;
                    string combatResult;
                    if (enemy.Health <= 0)
                    {
                        db.WargameUnits.Remove(enemy);
                        allUnits.Remove(enemy);
                        unit.X = tx; unit.Y = ty;
                        target.OwnerFactionId = faction.Id;
                        combatResult = $"unit {unit.Id} destroyed {enemy.Faction.Name} unit at ({tx},{ty})";
                    }
                    else
                    {
                        combatResult = $"unit {unit.Id} attacked {enemy.Faction.Name} at ({tx},{ty}), both took damage";
                    }

                    if (unit.Health <= 0)
                    {
                        db.WargameUnits.Remove(unit);
                        allUnits.Remove(unit);
                        combatResult += ", attacker destroyed";
                    }
                    outcomes.Add(combatResult);
                    break; // combat ends movement
                }

                // Plain step
                unit.X = tx; unit.Y = ty;
                target.OwnerFactionId = faction.Id;
                outcomes.Add($"→({tx},{ty})");
            }

            return outcomes.Count > 0 ? $"unit {unit.Id} " + string.Join(" ", outcomes) : $"unit {unit.Id} idled";
        }

        // Unknown action verb — if movement params are present, treat as move
        // (models use flavor verbs like "charge", "advance", "attack")
        if (action.UnitId.HasValue && (action.Path?.Count > 0 || action.ToX.HasValue))
            return await ExecuteActionAsync(faction,
                action with { Action = "move" },
                tileDict, allUnits, allBuildings, db, currentTurn, ct);

        return "idled";
    }
}
