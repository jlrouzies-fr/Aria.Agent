namespace Aria.Web.Data.Agents;

public class SubAgentSkill
{
    public int      Id         { get; set; }
    public int      SubAgentId { get; set; }
    public SubAgent SubAgent   { get; set; } = null!;
    public int      SkillId    { get; set; }
    public Skill    Skill      { get; set; } = null!;
}
