namespace Aria.Web.Data.Cogitations;

public class CogitationFolder
{
    public int      Id        { get; set; }
    public string   UserId    { get; set; } = "";
    public User     User      { get; set; } = null!;
    public string   Name      { get; set; } = "";
    public string?  Color     { get; set; }
    public int      SortOrder { get; set; }

    // Context defaults — applied to NEW cogitations created inside the folder.
    public int?     DefaultSubAgentId  { get; set; }
    public string?  DefaultProjectPath { get; set; }
    public string?  StandingDirective  { get; set; }

    public List<Cogitation> Cogitations { get; set; } = [];
}
