using System.Text;
using System.Text.Json.Serialization;
using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;
using OpenAI.Chat;

namespace Aria.Web.Services.WargameService;

// ── Action DTO ────────────────────────────────────────────────────────────────

record GameAction(
    [property: JsonPropertyName("action")]   string        Action,
    [property: JsonPropertyName("unit_id")]  int?          UnitId   = null,
    [property: JsonPropertyName("to_x")]     int?          ToX      = null,
    [property: JsonPropertyName("to_y")]     int?          ToY      = null,
    [property: JsonPropertyName("path")]     List<int[]>?  Path     = null,
    [property: JsonPropertyName("building")] string?       Building = null  // for "build" action
);

// ── Race stats ────────────────────────────────────────────────────────────────

public static class RaceStats
{
    public static (int MovePoints, int StartHp) Get(WargameRace race) => race switch
    {
        WargameRace.Undead     => (3, 2), // Fast skeleton swarms, very fragile
        WargameRace.Greenskins => (2, 3), // Quick and savage
        WargameRace.Empire     => (2, 3), // Disciplined, balanced
        WargameRace.Chaos      => (1, 5), // Slow but nearly unkillable heavy plate
        _                      => (2, 3)
    };
}

// ── Service state & lifecycle ─────────────────────────────────────────────────

public partial class WargameService : IHostedService
{
    private readonly IDbContextFactory<AppDbContext>  _dbFactory;
    private readonly AgentService                     _agentService;
    private readonly ILogger<WargameService>          _logger;

    // In-memory state — refreshed from DB after every turn
    public WargameMap?           ActiveMap      { get; private set; }
    public List<WargameFaction>  Factions       { get; private set; } = [];
    public List<WargameTile>     Tiles          { get; private set; } = [];
    public List<WargameUnit>     Units          { get; private set; } = [];
    public List<WargameBuilding> Buildings      { get; private set; } = [];
    public List<WargameTurnLog>  RecentLogs     { get; private set; } = [];
    public string?               WinnerName     { get; private set; }
    public DateTime?            NextTurnAt     { get; private set; }
    public string?              StatusText     { get; private set; }

    // Faction staging — held in memory until GenerateMapAsync is called
    public List<WargameFaction> StagedFactions { get; } = [];

    public void StageFaction(WargameFaction faction) => StagedFactions.Add(faction);

    public event Action? OnStateChanged;

    private CancellationTokenSource? _loopCts;

    // ChatClient per faction, built once and reused across turns
    private readonly Dictionary<int, ChatClient>       _factionClients   = new();
    // Reasoning buffer per faction — captures thinking text so it can be used as
    // fallback when content is empty (models that always output to reasoning_content)
    private readonly Dictionary<int, StringBuilder>    _reasoningBuffers = new();

    // ── File logger ───────────────────────────────────────────────────────────
    public static readonly string LogFile =
        Path.Combine(AppContext.BaseDirectory, "wargame-debug.log");

    public void LogToFile(string message)
    {
        try { File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss}] {message}\n"); }
        catch { }
    }

    public void ClearLogFile()
    {
        try { File.WriteAllText(LogFile, ""); } catch { }
    }

    public WargameService(
        IDbContextFactory<AppDbContext> dbFactory,
        AgentService agentService,
        ILogger<WargameService> logger)
    {
        _dbFactory    = dbFactory;
        _agentService = agentService;
        _logger       = logger;
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            await RefreshStateAsync();
            if (ActiveMap?.IsRunning == true)
                StartLoop();
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _loopCts?.Cancel();
        return Task.CompletedTask;
    }

    // ── Public control methods ────────────────────────────────────────────────

    public async Task<bool> StartGameAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var map = await db.WargameMaps.FirstOrDefaultAsync();
        if (map == null) return false;
        if (Factions.Count < 2) return false;

        map.IsRunning = true;
        await db.SaveChangesAsync();
        await RefreshStateAsync();
        StartLoop();
        return true;
    }

    public async Task StopGameAsync()
    {
        _loopCts?.Cancel();
        _loopCts = null;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var map = await db.WargameMaps.FirstOrDefaultAsync();
        if (map != null) { map.IsRunning = false; await db.SaveChangesAsync(); }

        NextTurnAt = null;
        StatusText = "// PAUSED";
        await RefreshStateAsync();
        OnStateChanged?.Invoke();
    }

    public async Task GenerateMapAsync(List<WargameFaction> factionDefs)
    {
        _loopCts?.Cancel();
        _loopCts = null;
        _factionClients.Clear();
        _reasoningBuffers.Clear();

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Wipe existing game data
        db.WargameTurnLogs.RemoveRange(db.WargameTurnLogs);
        db.WargameBuildings.RemoveRange(db.WargameBuildings);
        db.WargameUnits.RemoveRange(db.WargameUnits);
        db.WargameFactions.RemoveRange(db.WargameFactions);
        db.WargameMaps.RemoveRange(db.WargameMaps);
        await db.SaveChangesAsync();

        // Create map
        var seed = Random.Shared.Next();
        var map  = new WargameMap
        {
            Width = WargameMapGenerator.MapWidth, Height = WargameMapGenerator.MapHeight, Seed = seed,
            CurrentTurn = 0, IsRunning = false,
            TurnIntervalSeconds = 10,
            CreatedAt = DateTime.UtcNow
        };
        db.WargameMaps.Add(map);
        await db.SaveChangesAsync(); // get map.Id

        // Create factions
        foreach (var f in factionDefs)
        {
            f.TurnCount = 0;
            f.IsAlive   = true;
            f.CompactedContext = null;
            db.WargameFactions.Add(f);
        }
        await db.SaveChangesAsync(); // get faction Ids

        var savedFactions = await db.WargameFactions.ToListAsync();

        // Generate and save tiles
        var tiles = WargameMapGenerator.GenerateTiles(map.Id, map.Width, map.Height, seed, savedFactions);
        db.WargameTiles.AddRange(tiles);
        await db.SaveChangesAsync(); // get tile OwnerFactionId patching needs faction Ids

        // Generate units + buildings; also patches tile ownership for spawn clusters
        var (units, buildings) = WargameMapGenerator.GenerateUnitsAndBuildings(savedFactions, tiles);
        db.WargameUnits.AddRange(units);
        db.WargameBuildings.AddRange(buildings);
        // Persist resource values set by the generator
        db.WargameFactions.UpdateRange(savedFactions);
        await db.SaveChangesAsync();

        NextTurnAt = null;
        StatusText = "// READY — start the battle";
        await RefreshStateAsync();
        OnStateChanged?.Invoke();
    }

    public async Task UpdateIntervalAsync(int seconds)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var map = await db.WargameMaps.FirstOrDefaultAsync();
        if (map == null) return;
        map.TurnIntervalSeconds = Math.Clamp(seconds, 10, 3600);
        await db.SaveChangesAsync();
        await RefreshStateAsync();
        OnStateChanged?.Invoke();
    }

    // ── State refresh ─────────────────────────────────────────────────────────

    private async Task RefreshStateAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            ActiveMap = await db.WargameMaps.AsNoTracking().FirstOrDefaultAsync();

            Factions = await db.WargameFactions
                .AsNoTracking()
                .OrderBy(f => f.Id)
                .ToListAsync();

            Tiles = await db.WargameTiles
                .AsNoTracking()
                .Where(t => t.MapId == (ActiveMap != null ? ActiveMap.Id : -1))
                .ToListAsync();

            Units = await db.WargameUnits
                .AsNoTracking()
                .ToListAsync();

            Buildings = await db.WargameBuildings
                .AsNoTracking()
                .ToListAsync();

            RecentLogs = await db.WargameTurnLogs
                .AsNoTracking()
                .Include(l => l.Faction)
                .OrderByDescending(l => l.Id)
                .Take(30)
                .ToListAsync();

            if (ActiveMap?.IsRunning == true && NextTurnAt == null)
                NextTurnAt = DateTime.UtcNow.AddSeconds(ActiveMap.TurnIntervalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh wargame state");
        }
    }
}
