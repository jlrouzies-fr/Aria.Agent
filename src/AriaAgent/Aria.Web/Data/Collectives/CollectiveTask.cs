namespace Aria.Web.Data.Collectives;

public enum CollectiveTaskStatus { Pending, Blocked, Dispatched, Running, Completed, Failed, Skipped }

public class CollectiveTask
{
    public int                  Id               { get; set; }
    public int                  CollectiveId     { get; set; }
    public AgentCollective      Collective       { get; set; } = null!;
    public int?                 AssignedMemberId { get; set; }
    public CollectiveMember?    AssignedMember   { get; set; }
    public int                  Round            { get; set; } = 0;
    public string               Title            { get; set; } = "";
    public string               Instruction      { get; set; } = "";
    public string?              EffectiveInstruction { get; set; }  // post-transform instruction actually sent to drone
    public string?              DependsOnJson    { get; set; }  // JSON array of CollectiveTask.Id
    public CollectiveTaskStatus Status           { get; set; } = CollectiveTaskStatus.Pending;
    public string?              Result           { get; set; }
    public int?                 CogitationId     { get; set; }
    public string?              ErrorMessage     { get; set; }
    public DateTime             CreatedAt        { get; set; } = DateTime.UtcNow;
    public DateTime?            StartedAt        { get; set; }
    public DateTime?            CompletedAt      { get; set; }
}
