using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.Agent;

public class SkillService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<Skill>> GetForUserAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Skills
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<List<Skill>> GetForAgentAsync(int agentId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SubAgentSkills
            .Where(x => x.SubAgentId == agentId)
            .Include(x => x.Skill)
            .Select(x => x.Skill)
            .OrderBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<HashSet<int>> GetAssignedIdsAsync(int agentId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = await db.SubAgentSkills
            .Where(x => x.SubAgentId == agentId)
            .Select(x => x.SkillId)
            .ToListAsync();
        return [.. ids];
    }

    public async Task<Skill> CreateAsync(string userId, string name, string markdownContent)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var skill = new Skill { UserId = userId, Name = name.Trim(), MarkdownContent = markdownContent };
        db.Skills.Add(skill);
        await db.SaveChangesAsync();
        return skill;
    }

    public async Task<Skill> UpdateAsync(int skillId, string name, string markdownContent)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var skill = await db.Skills.FirstAsync(s => s.Id == skillId);
        skill.Name            = name.Trim();
        skill.MarkdownContent = markdownContent;
        await db.SaveChangesAsync();
        return skill;
    }

    public async Task DeleteAsync(int skillId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.SubAgentSkills.Where(x => x.SkillId == skillId).ExecuteDeleteAsync();
        await db.Skills.Where(s => s.Id == skillId).ExecuteDeleteAsync();
    }

    public async Task SetAgentSkillsAsync(int agentId, IEnumerable<int> skillIds)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.SubAgentSkills.Where(x => x.SubAgentId == agentId).ExecuteDeleteAsync();
        foreach (var id in skillIds)
            db.SubAgentSkills.Add(new SubAgentSkill { SubAgentId = agentId, SkillId = id });
        await db.SaveChangesAsync();
    }
}
