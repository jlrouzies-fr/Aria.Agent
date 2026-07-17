namespace Aria.Web.Data.Wargame;

public enum BuildingType { Keep, Farm, LumberMill, Barracks }

public class WargameBuilding
{
    public int          Id        { get; set; }
    public int          FactionId { get; set; }
    public int          X         { get; set; }
    public int          Y         { get; set; }
    public BuildingType Type      { get; set; }
    public int          BuiltTurn { get; set; }

    public WargameFaction Faction { get; set; } = null!;
}
