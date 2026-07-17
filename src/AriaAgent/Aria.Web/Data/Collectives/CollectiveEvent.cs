namespace Aria.Web.Data.Collectives;

public enum CollectiveEventType { Planned, Dispatched, DroneStarted, DroneResult, Reviewed, Completed, Failed, Info }

public class CollectiveEvent
{
    public int                 Id           { get; set; }
    public int                 CollectiveId { get; set; }
    public AgentCollective     Collective   { get; set; } = null!;
    public DateTime            Timestamp    { get; set; } = DateTime.UtcNow;
    public CollectiveEventType Type         { get; set; }
    public int?                ActorMemberId { get; set; }
    public CollectiveMember?   ActorMember  { get; set; }
    public int?                TaskId       { get; set; }
    public string              Message      { get; set; } = "";
}
