namespace Aria.Web.Data.Wargame;

public enum FactionCategory { Aggressive, Defensive, Pacifist, Heretic }

public enum WargameRace { Empire, Greenskins, Chaos, Undead }

public class WargameFaction
{
    public int             Id               { get; set; }
    public string          Name             { get; set; } = "";
    public FactionCategory Category         { get; set; }
    public WargameRace     Race             { get; set; } = WargameRace.Empire;
    public string          Color            { get; set; } = "#cc4444";
    public string          UserId           { get; set; } = "";
    public string?        SourceName       { get; set; }
    public string?        ModelId          { get; set; }
    public string?        CompactedContext { get; set; }
    public int            TurnCount        { get; set; }
    public bool           IsAlive          { get; set; } = true;

    // Resources
    public int  Wood  { get; set; } = 0;
    public int  Stone { get; set; } = 0;
    public int  Food  { get; set; } = 0;
    public int  Gold  { get; set; } = 0;

    public ICollection<WargameUnit>     Units     { get; set; } = [];
    public ICollection<WargameTurnLog>  TurnLogs  { get; set; } = [];
    public ICollection<WargameBuilding> Buildings { get; set; } = [];
}
