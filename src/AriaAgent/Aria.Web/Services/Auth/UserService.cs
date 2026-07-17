using System.Globalization;
using System.Text;
using Aria.Web.Data;
using Aria.Web.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.Auth;

public class UserService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<User>> GetUsersAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.OrderBy(u => u.Name).ToListAsync();
    }

    /// <summary>Returns the new User, or null if the name is already taken (case/diacritic-insensitive).</summary>
    public async Task<User?> CreateUserAsync(string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var trimmed    = name.Trim();
        var normalized = Normalize(trimmed);
        var existing   = await db.Users.Select(u => u.Name).ToListAsync();
        if (existing.Any(n => Normalize(n) == normalized))
            return null;
        var user = new User { Name = trimmed, AvatarSpriteKey = AgentSprites.PickSpriteKey() };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static string Normalize(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var stripped   = new string(decomposed
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray());
        return stripped.ToLowerInvariant();
    }

    public async Task SaveLastModelSourceAsync(string userId, string? source)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastModelSource, source));
    }

    /// <summary>Finds an existing soul by email or creates a new one. Used by OAuth login.</summary>
    public async Task<User> FindOrCreateByEmailAsync(string email, string displayName)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (existing != null) return existing;
        var user = new User { Name = displayName.Trim(), Email = email, AvatarSpriteKey = AgentSprites.PickSpriteKey() };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Assigns a random portrait if the user has none (e.g., accounts created before this feature).</summary>
    public async Task EnsureAvatarAsync(User user)
    {
        if (!string.IsNullOrEmpty(user.AvatarSpriteKey)) return;
        user.AvatarSpriteKey = AgentSprites.PickSpriteKey();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users
            .Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.AvatarSpriteKey, user.AvatarSpriteKey));
    }

    public async Task SaveTimezoneAsync(string userId, string? timezone)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Timezone, timezone));
    }

    public async Task<bool> GetKeepTelemetryExpandedAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.KeepTelemetryExpanded)
            .FirstOrDefaultAsync();
    }

    public async Task SaveKeepTelemetryExpandedAsync(string userId, bool keepExpanded)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.KeepTelemetryExpanded, keepExpanded));
    }

    public async Task DeleteUserAsync(string id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync();
    }

    public async Task<User?> GetByIdAsync(string id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
    }

    public async Task ClearPublicKeyAsync(string id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.PublicKey, (string?)null));
    }

    public async Task<User?> GetByPublicKeyAsync(string publicKey)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.PublicKey == publicKey);
    }

    public async Task<string?> GetSourcePreferenceAsync(string userId, string sourceName)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pref = await db.UserSourcePreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.SourceName == sourceName);
        return pref?.ModelId;
    }

    public async Task SaveSourcePreferenceAsync(string userId, string sourceName, string modelId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.UserSourcePreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.SourceName == sourceName);
        if (existing != null)
        {
            await db.UserSourcePreferences
                .Where(p => p.UserId == userId && p.SourceName == sourceName)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ModelId, modelId));
        }
        else
        {
            db.UserSourcePreferences.Add(new UserSourcePreference
            {
                UserId     = userId,
                SourceName = sourceName,
                ModelId    = modelId
            });
            await db.SaveChangesAsync();
        }
    }
}
