using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.Cogitations;

public class CogitationService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<Cogitation> CreateAsync(string userId, int? subAgentId = null, string? ariaAvatarKey = null, string? originNodeId = null, int? collectiveId = null, int? folderId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var cog = new Cogitation
        {
            UserId        = userId,
            SubAgentId    = subAgentId,
            AriaAvatarKey = ariaAvatarKey,
            OriginNodeId  = originNodeId,
            CollectiveId  = collectiveId,
            FolderId      = folderId,
        };
        db.Cogitations.Add(cog);
        await db.SaveChangesAsync();
        return cog;
    }

    public async Task MoveToFolderAsync(int cogitationId, int? folderId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Cogitations
            .Where(c => c.Id == cogitationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FolderId, folderId));
    }

    /// <summary>Repoints a cogitation to a different bridge node (after its content has been copied
    /// there) so future reads/continuations route to the new node.</summary>
    public async Task SetOriginNodeAsync(int cogitationId, string? originNodeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Cogitations
            .Where(c => c.Id == cogitationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.OriginNodeId, originNodeId));
    }

    public async Task<int> CountByFolderAsync(string userId, int folderId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Cogitations.CountAsync(c => c.UserId == userId && c.FolderId == folderId);
    }

    public async Task SetSuggestedFilingDismissedAsync(int cogitationId, bool dismissed = true)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Cogitations
            .Where(c => c.Id == cogitationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.SuggestedFilingDismissed, dismissed));
    }

    public async Task<Cogitation?> GetAsync(int cogitationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Cogitations
            .Include(c => c.SubAgent)
            .Include(c => c.Collective)
            .Include(c => c.Folder)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cogitationId);
    }

    public async Task<string?> GetOriginNodeIdAsync(int cogitationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Cogitations
            .AsNoTracking()
            .Where(c => c.Id == cogitationId)
            .Select(c => c.OriginNodeId)
            .FirstOrDefaultAsync();
    }

    public async Task SetTitleAsync(int cogitationId, string title)
    {
        var truncated = title.Length > 60 ? title[..60] : title;
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Cogitations
            .Where(c => c.Id == cogitationId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Title, truncated)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow));
    }

    /// <summary>Bumps UpdatedAt on the server index row (used for bridge-owned conversations).</summary>
    public async Task TouchAsync(int cogitationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Cogitations
            .Where(c => c.Id == cogitationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, DateTime.UtcNow));
    }

    /// <summary>
    /// Adds a message to a legacy (server-stored) cogitation. For bridge-owned cogitations this is a no-op;
    /// the caller writes directly to the bridge.
    /// </summary>
    public async Task AddMessageAsync(int cogitationId, string role, string content, string? thinkingContent = null,
        string? sectionsJson = null, string? imageBase64 = null, string? imageMediaType = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var origin = await db.Cogitations
            .AsNoTracking()
            .Where(c => c.Id == cogitationId)
            .Select(c => c.OriginNodeId)
            .FirstOrDefaultAsync();

        // Bridge-owned content lives on the node; do not persist it on the server.
        if (origin != null) return;

        db.CogitationMessages.Add(new CogitationMessage
        {
            CogitationId    = cogitationId,
            Role            = role,
            Content         = content,
            ThinkingContent = thinkingContent,
            SectionsJson    = sectionsJson,
            ImageBase64     = imageBase64,
            ImageMediaType  = imageMediaType,
        });
        await db.Cogitations
            .Where(c => c.Id == cogitationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Updates the mutable sections JSON of a single server-stored message (e.g. to persist a
    /// diff-card reverted flag). For bridge-owned cogitations use <see cref="BridgeCogitationClient"/>.
    /// </summary>
    public async Task<bool> UpdateMessageSectionsAsync(int cogitationId, int messageId, string sectionsJson)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var message = await db.CogitationMessages.FirstOrDefaultAsync(m => m.Id == messageId && m.CogitationId == cogitationId);
        if (message is null) return false;

        message.SectionsJson = sectionsJson;
        await db.Cogitations
            .Where(c => c.Id == cogitationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Replaces all messages of a legacy (server-stored) cogitation with a single summary message
    /// (used by "/compact"). For bridge-owned cogitations this is a no-op; the caller compacts
    /// directly on the bridge instead.
    /// </summary>
    public async Task CompactAsync(int cogitationId, string summary)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var origin = await db.Cogitations
            .AsNoTracking()
            .Where(c => c.Id == cogitationId)
            .Select(c => c.OriginNodeId)
            .FirstOrDefaultAsync();

        if (origin != null) return;

        await db.CogitationMessages
            .Where(m => m.CogitationId == cogitationId)
            .ExecuteDeleteAsync();

        db.CogitationMessages.Add(new CogitationMessage
        {
            CogitationId = cogitationId,
            Role         = "assistant",
            Content      = summary,
        });
        await db.Cogitations
            .Where(c => c.Id == cogitationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    public async Task<int> CountByAgentAsync(string userId, int subAgentId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Cogitations.CountAsync(c => c.UserId == userId && c.SubAgentId == subAgentId);
    }

    /// <summary>All of a user's cogitations, newest first — unscoped by agent so history from every
    /// channel/Hive is findable in one place. Includes SubAgent/Collective for the nav list's subtitle
    /// (agent name, or Hive name for a collective run).</summary>
    public async Task<List<Cogitation>> GetListAsync(string userId, int limit = 50)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Cogitations
            .Include(c => c.SubAgent)
            .Include(c => c.Collective)
            .Include(c => c.Folder)
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .Take(limit)
            .ToListAsync();
    }

    /// <summary>
    /// Returns messages for legacy (server-stored) cogitations. For bridge-owned cogitations the
    /// content lives on the bridge; callers should fetch it via <see cref="BridgeCogitationClient"/>.
    /// </summary>
    public async Task<List<CogitationMessage>> GetMessagesAsync(int cogitationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var origin = await db.Cogitations
            .AsNoTracking()
            .Where(c => c.Id == cogitationId)
            .Select(c => c.OriginNodeId)
            .FirstOrDefaultAsync();

        if (origin != null) return [];

        return await db.CogitationMessages
            .Where(m => m.CogitationId == cogitationId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<Cogitation>> SearchAsync(string userId, string query, int limit = 10)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = query.Trim().ToLower();
        return await db.Cogitations
            .Where(c => c.UserId == userId && (q == "" || c.Title.ToLower().Contains(q)))
            .OrderByDescending(c => c.UpdatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task DeleteAsync(int cogitationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.CogitationMessages.Where(m => m.CogitationId == cogitationId).ExecuteDeleteAsync();
        await db.Cogitations.Where(c => c.Id == cogitationId).ExecuteDeleteAsync();
    }
}
