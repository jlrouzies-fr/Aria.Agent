namespace Aria.Web.Data.Agents;

public class SubAgent
{
    public int      Id                   { get; set; }
    public string   UserId               { get; set; } = "";
    public User     User                 { get; set; } = null!;
    public string   GeneratedName        { get; set; } = "";
    public string   ArchetypeName        { get; set; } = "";
    public string   GeneratedPersonality { get; set; } = "";
    public string?  UserDirectives       { get; set; }
    public string   AccentColor          { get; set; } = "#8B0000";
    public string?  ModelSourceName      { get; set; }
    public string?  ModelId              { get; set; }
    public string?  EnabledMcpNamesJson  { get; set; }
    public string?  AvatarSpriteKey     { get; set; }
    public string?  Nickname            { get; set; }
    public DateTime CreatedAt            { get; set; } = DateTime.UtcNow;

    public List<SubAgentSkill> SubAgentSkills { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? GeneratedName : Nickname;

    public List<SubAgentToolState> ToolStates { get; set; } = [];
}
