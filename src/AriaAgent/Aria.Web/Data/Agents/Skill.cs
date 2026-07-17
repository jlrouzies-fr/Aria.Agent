namespace Aria.Web.Data.Agents;

public class Skill
{
    public int      Id              { get; set; }
    public string   UserId          { get; set; } = "";
    public User     User            { get; set; } = null!;
    public string   Name            { get; set; } = "";
    public string   MarkdownContent { get; set; } = "";
    public DateTime CreatedAt       { get; set; } = DateTime.UtcNow;

    public List<SubAgentSkill> SubAgentSkills { get; set; } = [];
}
