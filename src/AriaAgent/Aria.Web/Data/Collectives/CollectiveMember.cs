namespace Aria.Web.Data.Collectives;

public class CollectiveMember
{
    public int           Id           { get; set; }
    public int           CollectiveId { get; set; }
    public AgentCollective Collective { get; set; } = null!;
    public int           SubAgentId   { get; set; }
    public SubAgent      SubAgent     { get; set; } = null!;
    public string?       RoleLabel    { get; set; }
    public double        CanvasX      { get; set; } = 0;
    public double        CanvasY      { get; set; } = 0;
    public bool          RequiresHumanApproval { get; set; } = false;
    public bool          GateAfterResponse     { get; set; } = true;  // true = review drone reply; false = approve before dispatch
    public DateTime      CreatedAt    { get; set; } = DateTime.UtcNow;

    public List<MemberEdgeNode> EdgeNodes { get; set; } = [];
}
