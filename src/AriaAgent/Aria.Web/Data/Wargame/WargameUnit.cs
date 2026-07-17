namespace Aria.Web.Data.Wargame;

public class WargameUnit
{
    public int            Id        { get; set; }
    public int            FactionId { get; set; }
    public int            X         { get; set; }
    public int            Y         { get; set; }
    public int            Health    { get; set; } = 3;
    public int            MaxHealth { get; set; } = 3;

    public WargameFaction Faction   { get; set; } = null!;
}
