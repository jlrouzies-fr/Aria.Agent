using Aria.Web.Data;
using Aria.Web.Data.Cogitations;
using Aria.Web.Services.ModelBridge;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.Cogitations;

public class CogitationFolderService(IDbContextFactory<AppDbContext> dbFactory, BridgeSyncService? sync = null)
{
    public async Task<CogitationFolder> CreateAsync(
        string userId,
        string name,
        string? color = null,
        int? defaultSubAgentId = null,
        string? defaultProjectPath = null,
        string? standingDirective = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var maxSort = await db.CogitationFolders
            .Where(f => f.UserId == userId)
            .Select(f => (int?)f.SortOrder)
            .MaxAsync();

        var folder = new CogitationFolder
        {
            UserId             = userId,
            Name               = name.Trim(),
            Color              = string.IsNullOrWhiteSpace(color) ? null : color.Trim(),
            SortOrder          = (maxSort ?? -1) + 1,
            DefaultSubAgentId  = defaultSubAgentId,
            DefaultProjectPath = string.IsNullOrWhiteSpace(defaultProjectPath) ? null : defaultProjectPath.Trim(),
            StandingDirective  = string.IsNullOrWhiteSpace(standingDirective) ? null : standingDirective.Trim(),
        };
        db.CogitationFolders.Add(folder);
        await db.SaveChangesAsync();
        _ = sync?.PushSnapshotAsync(userId);
        return folder;
    }

    public async Task<CogitationFolder?> GetByIdAsync(int folderId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CogitationFolders
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == folderId);
    }

    public async Task<List<CogitationFolder>> GetListAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CogitationFolders
            .Where(f => f.UserId == userId)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Name)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task UpdateAsync(
        string userId,
        int folderId,
        string name,
        string? color = null,
        int? defaultSubAgentId = null,
        string? defaultProjectPath = null,
        string? standingDirective = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.CogitationFolders
            .Where(f => f.Id == folderId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.Name, name.Trim())
                .SetProperty(f => f.Color, string.IsNullOrWhiteSpace(color) ? null : color.Trim())
                .SetProperty(f => f.DefaultSubAgentId, defaultSubAgentId)
                .SetProperty(f => f.DefaultProjectPath, string.IsNullOrWhiteSpace(defaultProjectPath) ? null : defaultProjectPath.Trim())
                .SetProperty(f => f.StandingDirective, string.IsNullOrWhiteSpace(standingDirective) ? null : standingDirective.Trim()));
        _ = sync?.PushSnapshotAsync(userId);
    }

    public async Task DeleteAsync(string userId, int folderId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Cogitations
            .Where(c => c.FolderId == folderId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FolderId, (int?)null));

        await db.CogitationFolders
            .Where(f => f.Id == folderId)
            .ExecuteDeleteAsync();
        _ = sync?.PushSnapshotAsync(userId);
    }

    public async Task<int> CountByFolderAsync(string userId, int folderId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Cogitations.CountAsync(c => c.UserId == userId && c.FolderId == folderId);
    }

    public async Task MoveCogitationAsync(int cogitationId, int? folderId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Cogitations
            .Where(c => c.Id == cogitationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FolderId, folderId));
    }
}
