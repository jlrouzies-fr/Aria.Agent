using Aria.Web.Data;

namespace Aria.Web.Services.WargameService;

public static class WargameMapGenerator
{
    public const int MapWidth  = 12;
    public const int MapHeight = 12;

    private static readonly (int X, int Y)[][] Spawns =
    [
        [],
        [(6, 6)],
        [(2, 2),  (9, 9)],
        [(2, 2),  (9, 2),  (5, 9)],
        [(2, 2),  (9, 2),  (2, 9), (9, 9)],
    ];

    public static List<WargameTile> GenerateTiles(int mapId, int width, int height, int seed,
        IReadOnlyList<WargameFaction> factions)
    {
        var rng      = new Random(seed);
        var tiles    = new List<WargameTile>(width * height);
        var spawns   = factions.Count <= 4 ? Spawns[factions.Count] : [];
        var clearZone = new HashSet<(int, int)>();
        foreach (var (sx, sy) in spawns)
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                clearZone.Add((sx + dx, sy + dy));

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            TileType type;
            if (clearZone.Contains((x, y)))
                type = TileType.Plains;
            else
            {
                var roll = rng.NextDouble();
                type = roll < 0.12 ? TileType.Mountains
                     : roll < 0.22 ? TileType.Forest
                     : roll < 0.28 ? TileType.Ruins
                     : TileType.Plains;
            }
            tiles.Add(new WargameTile { MapId = mapId, X = x, Y = y, Type = type });
        }
        return tiles;
    }

    public static (List<WargameUnit> Units, List<WargameBuilding> Buildings) GenerateUnitsAndBuildings(
        IReadOnlyList<WargameFaction> factions, List<WargameTile> tiles, int currentTurn = 0)
    {
        var spawns   = factions.Count <= 4 ? Spawns[factions.Count] : [];
        var tileDict = tiles.ToDictionary(t => (t.X, t.Y));
        var units    = new List<WargameUnit>();
        var buildings = new List<WargameBuilding>();
        int maxX     = tileDict.Keys.Max(k => k.X);
        int maxY     = tileDict.Keys.Max(k => k.Y);

        for (int i = 0; i < factions.Count; i++)
        {
            var faction = factions[i];
            var (sx, sy) = i < spawns.Length ? spawns[i] : (3 + i * 4, 3 + i * 4);
            var (_, startHp) = RaceStats.Get(faction.Race);

            // Starting resources
            faction.Wood  = 10;
            faction.Stone = 8;
            faction.Food  = 10;
            faction.Gold  = 2;

            // Keep at spawn point
            if (tileDict.TryGetValue((sx, sy), out var spawnTile))
                spawnTile.OwnerFactionId = faction.Id;
            buildings.Add(new WargameBuilding { FactionId = faction.Id, X = sx, Y = sy, Type = BuildingType.Keep, BuiltTurn = 0 });

            // 3 units in an L-shape around spawn
            foreach (var (dx, dy) in new[] { (1, 0), (0, 1), (1, 1) })
            {
                int ux = Math.Clamp(sx + dx, 0, maxX);
                int uy = Math.Clamp(sy + dy, 0, maxY);
                if (tileDict.TryGetValue((ux, uy), out var t))
                    t.OwnerFactionId = faction.Id;
                units.Add(new WargameUnit { FactionId = faction.Id, X = ux, Y = uy, Health = startHp, MaxHealth = startHp });
            }
        }

        return (units, buildings);
    }
}
