namespace Aria.Web.Data.Users;

public class UserLocalSource
{
    public int    Id         { get; set; }
    public string UserId     { get; set; } = "";
    public string Name       { get; set; } = "";
    public string Url        { get; set; } = "";
    public string ModelsJson { get; set; } = "[]";
    public bool   IsBridged  { get; set; }
    public int    SortOrder  { get; set; }
    // Optional: pin this local channel to a specific bridge node (§5 of the remote-nodes plan).
    public string? BridgeNodeId { get; set; }
    // Real node-side channel name for the bridge keyRef; set only when Name was disambiguated across nodes.
    public string? ChannelName { get; set; }

    /// <summary>Per-channel user override for context window, in tokens (null = use discovery/fallback).</summary>
    public int? ContextWindow { get; set; }

    public User User { get; set; } = null!;
}
