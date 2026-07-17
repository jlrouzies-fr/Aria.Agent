using Aria.Web.Data.Collectives;

namespace Aria.Web.Data.Cogitations;

public class Cogitation
{
    public int      Id        { get; set; }
    public string   UserId    { get; set; } = "";
    public User     User      { get; set; } = null!;
    public string   Title     { get; set; } = "New Cogitation";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public int?     SubAgentId { get; set; }
    public SubAgent? SubAgent  { get; set; }

    // Set when this cogitation is a Hive collective run (SubAgentId then points at the
    // Overmind's own SubAgent) — lets the Chat UI brand the conversation as the Hive rather
    // than as a solo chat with that sub-agent.
    public int?              CollectiveId { get; set; }
    public AgentCollective?   Collective   { get; set; }

    public int?              FolderId { get; set; }
    public CogitationFolder? Folder   { get; set; }

    /// <summary>True when the user dismissed the "file this under X?" suggestion for this cogitation.</summary>
    public bool SuggestedFilingDismissed { get; set; }

    public string? AriaAvatarKey { get; set; }

    /// <summary>
    /// The bridge node that owns this cogitation's content.
    /// Null = legacy/server-stored content; non-null = content lives on the named bridge node.
    /// </summary>
    public string? OriginNodeId { get; set; }

    public List<CogitationMessage> Messages { get; set; } = [];
}
