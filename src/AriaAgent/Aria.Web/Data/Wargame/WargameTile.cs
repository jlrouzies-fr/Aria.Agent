namespace Aria.Web.Data.Wargame;

public enum TileType { Plains, Forest, Mountains, Ruins }

public class WargameTile
{
    public int              Id              { get; set; }
    public int              MapId           { get; set; }
    public int              X               { get; set; }
    public int              Y               { get; set; }
    public TileType         Type            { get; set; }
    public int?             OwnerFactionId  { get; set; }

    public WargameMap       Map             { get; set; } = null!;
    public WargameFaction?  Owner           { get; set; }
}
