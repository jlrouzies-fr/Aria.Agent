namespace Aria.Web.Data.Wargame;

public class WargameMap
{
    public int      Id                  { get; set; }
    public int      Width               { get; set; } = 20;
    public int      Height              { get; set; } = 20;
    public int      Seed                { get; set; }
    public int      CurrentTurn         { get; set; }
    public bool     IsRunning           { get; set; }
    public int      TurnIntervalSeconds { get; set; } = 10;
    public DateTime CreatedAt           { get; set; }

    public ICollection<WargameTile> Tiles { get; set; } = [];
}
