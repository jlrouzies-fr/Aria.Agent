namespace Aria.Web.Data.Wargame;

public class WargameTurnLog
{
    public int            Id         { get; set; }
    public int            FactionId  { get; set; }
    public int            TurnNumber { get; set; }
    public string         ActionJson { get; set; } = "{}";
    public string         Summary    { get; set; } = "";
    public DateTime       CreatedAt  { get; set; }

    public WargameFaction Faction    { get; set; } = null!;
}
