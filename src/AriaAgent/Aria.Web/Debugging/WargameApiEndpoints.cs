#if DEBUG
using Aria.Web.Data;
using Aria.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Debug;

// Debug-only API endpoints for autonomous testing without the browser UI.
// Usage:
//   curl -s http://localhost:5129/api/debug/wargame/state
//   curl -s -X POST http://localhost:5129/api/debug/wargame/generate
//   curl -s -X POST http://localhost:5129/api/debug/wargame/turn
//   curl -s http://localhost:5129/api/debug/wargame/logs

public static class WargameApiEndpoints
{
    public static void Register(WebApplication app)
    {
        var grp = app.MapGroup("/api/debug/wargame");

        // ── State ─────────────────────────────────────────────────────────────
        grp.MapGet("/state", (WargameService svc) =>
        {
            var tilesByFaction = svc.Tiles
                .GroupBy(t => t.OwnerFactionId)
                .ToDictionary(g => g.Key ?? 0, g => g.Count());

            return Results.Ok(new
            {
                map = svc.ActiveMap == null ? null : new
                {
                    svc.ActiveMap.CurrentTurn,
                    svc.ActiveMap.IsRunning,
                    svc.ActiveMap.TurnIntervalSeconds,
                    nextTurnAt = svc.NextTurnAt,
                    svc.ActiveMap.Width,
                    svc.ActiveMap.Height
                },
                status   = svc.StatusText,
                factions = svc.Factions.Select(f => new
                {
                    f.Id, f.Name, f.Race, f.Color, f.IsAlive, f.TurnCount,
                    tiles     = tilesByFaction.GetValueOrDefault(f.Id, 0),
                    units     = svc.Units.Count(u => u.FactionId == f.Id),
                    buildings = svc.Buildings.Count(b => b.FactionId == f.Id),
                    f.Wood, f.Stone, f.Food, f.Gold,
                    f.SourceName, f.ModelId
                }),
                units     = svc.Units.Select(u => new { u.Id, u.FactionId, u.X, u.Y, u.Health, u.MaxHealth }),
                buildings = svc.Buildings.Select(b => new { b.Id, b.FactionId, b.X, b.Y, type = b.Type.ToString(), b.BuiltTurn }),
                winner    = svc.WinnerName,
                recentLogs = svc.RecentLogs.Take(20).Select(l => new
                {
                    l.TurnNumber,
                    faction = svc.Factions.FirstOrDefault(f => f.Id == l.FactionId)?.Name,
                    l.Summary,
                    l.CreatedAt
                })
            });
        });

        // ── Generate map with default source/model ────────────────────────────
        grp.MapPost("/generate", async (GenerateRequest? req, WargameService svc, AgentService agentSvc) =>
        {
            // Prefer an explicitly named source, then the first source with a model list, then any source
            var wantedSource = req?.SourceName;
            var src = wantedSource != null
                ? agentSvc.AvailableModelSources.FirstOrDefault(s => s.Name == wantedSource)
                : agentSvc.AvailableModelSources.FirstOrDefault(s => s.Models.Count > 0)
                  ?? agentSvc.AvailableModelSources.FirstOrDefault();

            if (src == null) return Results.Problem("No LLM source configured");

            var model = req?.ModelId ?? src.Models.FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(model)) return Results.Problem("Source has no models listed — pass modelId explicitly");

            var palette = new[] { "#d44040", "#50b840", "#4080d0", "#d0a030" };

            var factionDefs = (req?.Factions ?? DefaultFactions).Select((f, i) =>
                new WargameFaction
                {
                    Name       = f.Name,
                    Race       = Enum.TryParse<WargameRace>(f.Race, true, out var race) ? race : WargameRace.Empire,
                    Category   = FactionCategory.Aggressive,
                    Color      = f.Color ?? palette[i % palette.Length],
                    SourceName = f.SourceName ?? src.Name,
                    ModelId    = f.ModelId    ?? model
                }).ToList();

            if (factionDefs.Count < 2)
                return Results.BadRequest("At least 2 factions required");

            svc.ClearLogFile();
            await svc.GenerateMapAsync(factionDefs);

            // Set a short interval for debug iteration speed
            await svc.UpdateIntervalAsync(req?.IntervalSeconds ?? 5);

            return Results.Ok(new { message = $"Map generated with {factionDefs.Count} factions", logFile = WargameService.LogFile });
        });

        // ── Start / Stop ──────────────────────────────────────────────────────
        grp.MapPost("/start", async (WargameService svc) =>
        {
            var ok = await svc.StartGameAsync();
            return ok ? Results.Ok("Game started") : Results.BadRequest("Could not start — need map + ≥2 alive factions");
        });

        grp.MapPost("/stop", async (WargameService svc) =>
        {
            await svc.StopGameAsync();
            return Results.Ok("Game stopped");
        });

        // ── Trigger one turn immediately (fire-and-forget) ────────────────────
        // Returns immediately; poll GET /state or GET /logs to see results.
        grp.MapPost("/turn", (WargameService svc) =>
        {
            if (svc.ActiveMap == null) return Results.BadRequest("No active map");
            _ = Task.Run(() => svc.TriggerTurnAsync());
            return Results.Accepted(value: new
            {
                message  = "Turn triggered — poll /state or /logs for results",
                logFile  = WargameService.LogFile
            });
        });

        // ── Reset ─────────────────────────────────────────────────────────────
        grp.MapDelete("/reset", async (WargameService svc) =>
        {
            await svc.StopGameAsync();
            await svc.GenerateMapAsync([]);
            svc.ClearLogFile();
            return Results.Ok("Reset complete");
        });

        // ── Tail the debug log file ────────────────────────────────────────────
        grp.MapGet("/logs", (int? tail) =>
        {
            if (!File.Exists(WargameService.LogFile))
                return Results.Ok(new { lines = Array.Empty<string>() });

            var lines = File.ReadAllLines(WargameService.LogFile);
            var result = tail.HasValue ? lines.TakeLast(tail.Value) : lines;
            return Results.Ok(new { totalLines = lines.Length, lines = result });
        });

        // ── Format cache ──────────────────────────────────────────────────────
        grp.MapGet("/formatcache", async (IDbContextFactory<AppDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var entries = await db.ModelFormatCaches.AsNoTracking().ToListAsync();
            return Results.Ok(entries);
        });

        grp.MapDelete("/formatcache", async (IDbContextFactory<AppDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.ModelFormatCaches.RemoveRange(db.ModelFormatCaches);
            await db.SaveChangesAsync();
            return Results.Ok("Format cache cleared — formats will be re-detected on next session");
        });
    }

    // ── Request DTOs ──────────────────────────────────────────────────────────

    private static readonly List<FactionRequest> DefaultFactions =
    [
        new("Iron Hammers", "Empire",     "#c8a830", null, null),
        new("Da Green Tide","Greenskins", "#50b840", null, null),
    ];

    public record FactionRequest(
        string  Name,
        string  Race,
        string? Color,
        string? SourceName,
        string? ModelId);

    // SourceName + ModelId override the auto-detected defaults for all factions
    public record GenerateRequest(List<FactionRequest> Factions, string? SourceName = null, string? ModelId = null, int? IntervalSeconds = null);
}
#endif
