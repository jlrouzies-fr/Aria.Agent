namespace Aria.Web.Data.Agents;

public class SubAgentToolState
{
    public int      Id         { get; set; }
    public int      SubAgentId { get; set; }
    public SubAgent SubAgent   { get; set; } = null!;
    public string   ToolId     { get; set; } = "";
    public bool     Enabled    { get; set; }
}
