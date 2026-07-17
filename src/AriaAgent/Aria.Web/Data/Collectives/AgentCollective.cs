namespace Aria.Web.Data.Collectives;

public enum CollectiveStatus { Draft, Planning, Running, Paused, Completed, Failed }

/// <summary>How the collective tackles its objective each round.</summary>
public enum CollectiveBehavior
{
    HiveMind          = 0, // every drone runs the same directive; Overmind picks/merges the best output
    CohortSpecialists = 1  // decompose the objective into sub-directives, one best-fit specialist each
}

public class AgentCollective
{
    public int                Id               { get; set; }
    public string             UserId           { get; set; } = "";
    public User               User             { get; set; } = null!;
    public string             Name             { get; set; } = "";
    public string             Objective        { get; set; } = "";
    public CollectiveStatus   Status           { get; set; } = CollectiveStatus.Draft;
    public CollectiveBehavior Behavior         { get; set; } = CollectiveBehavior.HiveMind;

    // Overmind config
    public int?             OvermindSubAgentId  { get; set; }
    public SubAgent?        OvermindSubAgent    { get; set; }
    public string?          OvermindSourceName  { get; set; }
    public string?          OvermindModelId     { get; set; }
    public string?          OvermindAvatarPath  { get; set; }

    // Loop control
    public int              MaxRounds          { get; set; } = 6;
    public int              CurrentRound       { get; set; } = 0;
    public string?          ResultSummary      { get; set; }
    public string?          LastFeedback       { get; set; }  // CONTINUE feedback for next PLAN
    public string?          SynapseMemory      { get; set; }  // Overmind broadcast injected into all drone sessions
    public bool             RequiresHumanApproval { get; set; } = false; // Pause after drone phase for soul review

    // Canvas state
    public double           CanvasZoom         { get; set; } = 1;
    public double           CanvasPanX         { get; set; } = 0;
    public double           CanvasPanY         { get; set; } = 0;

    // Timestamps
    // Bridge ownership: null = legacy/server-stored content; non-null = content lives on that node.
    public string?          OriginNodeId       { get; set; }

    public DateTime         CreatedAt          { get; set; } = DateTime.UtcNow;
    public DateTime         UpdatedAt          { get; set; } = DateTime.UtcNow;
    public DateTime?        CompletedAt        { get; set; }

    // Navigation
    public List<CollectiveMember> Members      { get; set; } = [];
    public List<CollectiveTask>   Tasks        { get; set; } = [];
    public List<CollectiveEvent>  Events       { get; set; } = [];
}
