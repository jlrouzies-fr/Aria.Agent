namespace Aria.Web.Data.Agents;

public enum CronJobStatus { Pending, Running, Completed, Failed, Cancelled }

public class AgentCronJob
{
    public int           Id            { get; set; }
    public string        UserId        { get; set; } = "";
    public User          User          { get; set; } = null!;

    public int?          SubAgentId    { get; set; }
    public SubAgent?     SubAgent      { get; set; }

    public int?          TargetCogitationId { get; set; }  // continue this cogitation (set at booking)
    public int?          CogitationId       { get; set; }  // result cogitation (set after completion)

    /// <summary>
    /// The bridge node that should execute this vigil. Null = any currently-connected node.
    /// </summary>
    public string?       BridgeNodeId  { get; set; }

    public string        TaskPrompt    { get; set; } = "";
    public string?       SourceName    { get; set; }
    public string?       ModelId       { get; set; }

    public DateOnly      ScheduledDate { get; set; }
    public int           ScheduledHour { get; set; }  // 0–23 UTC

    public CronJobStatus Status        { get; set; } = CronJobStatus.Pending;

    public DateTime      CreatedAt     { get; set; } = DateTime.UtcNow;
    public DateTime?     StartedAt     { get; set; }
    public DateTime?     CompletedAt   { get; set; }

    public string?       ResultSummary { get; set; }
    public string?       ErrorMessage  { get; set; }

    public bool          IsSeenByUser  { get; set; } = true;
}
