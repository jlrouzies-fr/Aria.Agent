using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

public static class CogitationEndpoints
{
    public static void MapCogitationEndpoints(this WebApplication app)
    {
        // GET /cogitations?soulId=&subAgentId=&limit=&q=
        app.MapGet("/cogitations", async (
            string? soulId, string? subAgentId, int limit, string? q,
            BridgeDbContext db,
            ILogger<CogitationLogCategory> logger) =>
        {
            if (string.IsNullOrEmpty(soulId)) return Results.BadRequest("soulId required");
            limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 200);

            logger.LogInformation(
                "[Cogitations] Listing for soul={SoulId} subAgent={SubAgentId} limit={Limit} q={Query}",
                soulId, subAgentId ?? "(any)", limit, q ?? "(none)");

            var query = db.Cogitations
                .AsNoTracking()
                .Where(c => c.SoulId == soulId);

            // subAgentId="" means "no sub-agent" (general cogitations)
            query = subAgentId is null
                ? query
                : subAgentId == ""
                    ? query.Where(c => c.SubAgentId == null || c.SubAgentId == "")
                    : query.Where(c => c.SubAgentId == subAgentId);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var lower = q.Trim().ToLower();
                query = query.Where(c => c.Title.ToLower().Contains(lower));
            }

            var list = await query
                .OrderByDescending(c => c.UpdatedAt)
                .Take(limit)
                .Select(c => new CogitationDto(c.Id, c.SoulId, c.Title, c.AriaAvatarKey, c.SubAgentId, c.FolderId, c.CreatedAt, c.UpdatedAt))
                .ToListAsync();

            logger.LogInformation("[Cogitations] Returning {Count} cogitations for soul={SoulId}", list.Count, soulId);
            return Results.Ok(list);
        });

        // POST /cogitations/init — create a cogitation using a server-side userId.
        // Requires a named soul to already exist and be linked to this serverUserId.
        // Uses a caller-supplied Id so the server can derive it deterministically.
        app.MapPost("/cogitations/init", async (
            InitCogitationRequest req,
            BridgeDbContext db,
            ILogger<CogitationLogCategory> logger) =>
        {
            if (string.IsNullOrWhiteSpace(req.ServerUserId)) return Results.BadRequest("ServerUserId required");
            if (string.IsNullOrEmpty(req.Id)) return Results.BadRequest("Id required");

            // Only accept a soul that is both named and linked to this server user.
            var soul = await db.Souls.FirstOrDefaultAsync(s => s.ServerSoulId == req.ServerUserId && s.Name != "");
            if (soul == null)
            {
                logger.LogWarning(
                    "[Cogitations] Init refused: no named soul linked to serverUserId={ServerUserId}",
                    req.ServerUserId);
                return Results.Problem(
                    statusCode: 412,
                    title: "Soul not configured",
                    detail: $"No named soul linked to serverUserId={req.ServerUserId}. " +
                            "Open the bridge status page to create and link your soul first.");
            }

            var existing = await db.Cogitations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.Id);
            if (existing != null)
            {
                logger.LogInformation(
                    "[Cogitations] Init returned existing cogitation={CogitationId} soul={SoulId} subAgent={SubAgentId}",
                    existing.Id, existing.SoulId, existing.SubAgentId ?? "(none)");
                return Results.Ok(ToDto(existing));
            }

            var cog = new BridgeCogitation
            {
                Id            = req.Id,
                SoulId        = soul.Id,
                AriaAvatarKey = req.AriaAvatarKey,
                SubAgentId    = req.SubAgentId,
                FolderId      = req.FolderId,
            };
            db.Cogitations.Add(cog);
            await db.SaveChangesAsync();

            logger.LogInformation(
                "[Cogitations] Created cogitation={CogitationId} soul={SoulId} subAgent={SubAgentId} avatar={Avatar}",
                cog.Id, cog.SoulId, cog.SubAgentId ?? "(none)", cog.AriaAvatarKey ?? "(none)");

            return Results.Created($"/cogitations/{cog.Id}", ToDto(cog));
        });

        // POST /cogitations
        app.MapPost("/cogitations", async (
            CreateCogitationRequest req,
            BridgeDbContext db,
            ILogger<CogitationLogCategory> logger) =>
        {
            if (string.IsNullOrEmpty(req.SoulId)) return Results.BadRequest("SoulId required");
            if (!await db.Souls.AnyAsync(s => s.Id == req.SoulId))
                return Results.NotFound("Soul not found");

            var cog = new BridgeCogitation
            {
                SoulId        = req.SoulId,
                AriaAvatarKey = req.AriaAvatarKey,
                SubAgentId    = req.SubAgentId,
                FolderId      = req.FolderId,
            };
            db.Cogitations.Add(cog);
            await db.SaveChangesAsync();

            logger.LogInformation(
                "[Cogitations] Created cogitation={CogitationId} soul={SoulId} subAgent={SubAgentId}",
                cog.Id, cog.SoulId, cog.SubAgentId ?? "(none)");

            return Results.Created($"/cogitations/{cog.Id}", ToDto(cog));
        });

        // GET /cogitations/{id}
        app.MapGet("/cogitations/{id}", async (
            string id,
            BridgeDbContext db,
            ILogger<CogitationLogCategory> logger) =>
        {
            var cog = await db.Cogitations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (cog is null)
            {
                logger.LogWarning("[Cogitations] Cogitation={CogitationId} not found", id);
                return Results.NotFound();
            }

            logger.LogInformation(
                "[Cogitations] Fetched cogitation={CogitationId} title={Title} subAgent={SubAgentId}",
                cog.Id, cog.Title, cog.SubAgentId ?? "(none)");
            return Results.Ok(ToDto(cog));
        });

        // PUT /cogitations/{id} — update title / avatar
        app.MapPut("/cogitations/{id}", async (
            string id, UpdateCogitationRequest req,
            BridgeDbContext db,
            ILogger<CogitationLogCategory> logger) =>
        {
            var cog = await db.Cogitations.FirstOrDefaultAsync(c => c.Id == id);
            if (cog is null)
            {
                logger.LogWarning("[Cogitations] Update refused: cogitation={CogitationId} not found", id);
                return Results.NotFound();
            }

            if (req.Title is not null)
            {
                var oldTitle = cog.Title;
                cog.Title = req.Title.Length > 60 ? req.Title[..60] : req.Title;
                logger.LogInformation(
                    "[Cogitations] Updated title for cogitation={CogitationId}: '{OldTitle}' -> '{NewTitle}'",
                    cog.Id, oldTitle, cog.Title);
            }
            if (req.AriaAvatarKey is not null)
            {
                logger.LogInformation(
                    "[Cogitations] Updated avatar for cogitation={CogitationId}: {Avatar}",
                    cog.Id, req.AriaAvatarKey);
                cog.AriaAvatarKey = req.AriaAvatarKey;
            }
            if (req.FolderId is not null)
            {
                logger.LogInformation(
                    "[Cogitations] Updated folder for cogitation={CogitationId}: {FolderId}",
                    cog.Id, req.FolderId);
                cog.FolderId = req.FolderId;
            }
            cog.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            return Results.Ok(ToDto(cog));
        });

        // DELETE /cogitations/{id}
        app.MapDelete("/cogitations/{id}", async (
            string id,
            BridgeDbContext db,
            ILogger<CogitationLogCategory> logger) =>
        {
            var msgDeleted = await db.Messages.Where(m => m.CogitationId == id).ExecuteDeleteAsync();
            var cogDeleted = await db.Cogitations.Where(c => c.Id == id).ExecuteDeleteAsync();

            logger.LogInformation(
                "[Cogitations] Deleted cogitation={CogitationId}; rows: cogitations={CogRows} messages={MsgRows}",
                id, cogDeleted, msgDeleted);

            return Results.NoContent();
        });

        // GET /cogitations/{id}/messages
        app.MapGet("/cogitations/{id}/messages", async (
            string id,
            BridgeDbContext db,
            ILogger<CogitationLogCategory> logger) =>
        {
            if (!await db.Cogitations.AnyAsync(c => c.Id == id))
            {
                logger.LogWarning("[Cogitations] Messages requested for unknown cogitation={CogitationId}", id);
                return Results.NotFound();
            }

            var msgs = await db.Messages
                .AsNoTracking()
                .Where(m => m.CogitationId == id)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new MessageDto(m.Id, m.CogitationId, m.Role, m.Content, m.ThinkingContent, m.SectionsJson, m.CreatedAt, m.ImageBase64, m.ImageMediaType))
                .ToListAsync();

            logger.LogInformation(
                "[Cogitations] Fetched {Count} messages for cogitation={CogitationId}",
                msgs.Count, id);

            return Results.Ok(msgs);
        });

        // POST /cogitations/{id}/messages
        app.MapPost("/cogitations/{id}/messages", async (
            string id, AddMessageRequest req,
            BridgeDbContext db,
            ILogger<CogitationLogCategory> logger) =>
        {
            var cog = await db.Cogitations.FirstOrDefaultAsync(c => c.Id == id);
            if (cog is null)
            {
                logger.LogWarning("[Cogitations] Add message refused: cogitation={CogitationId} not found", id);
                return Results.NotFound();
            }

            var msg = new BridgeMessage
            {
                CogitationId    = id,
                Role            = req.Role,
                Content         = req.Content,
                ThinkingContent = req.ThinkingContent,
                SectionsJson    = req.SectionsJson,
                ImageBase64     = req.ImageBase64,
                ImageMediaType  = req.ImageMediaType,
            };
            db.Messages.Add(msg);
            cog.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            logger.LogInformation(
                "[Cogitations] Added message id={MessageId} role={Role} len={ContentLength} thinkingLen={ThinkingLength} " +
                "to cogitation={CogitationId} subAgent={SubAgentId}",
                msg.Id, msg.Role, msg.Content?.Length ?? 0, msg.ThinkingContent?.Length ?? 0,
                cog.Id, cog.SubAgentId ?? "(none)");

            return Results.Created($"/cogitations/{id}/messages/{msg.Id}",
                new MessageDto(msg.Id, msg.CogitationId, msg.Role, msg.Content ?? "", msg.ThinkingContent, msg.SectionsJson, msg.CreatedAt, msg.ImageBase64, msg.ImageMediaType));
        });

        // PUT /cogitations/{id}/messages/{messageId} — update the mutable sections of a message
        // (currently used to persist diff-card reverted state without rewriting the whole transcript).
        app.MapPut("/cogitations/{id}/messages/{messageId}", async (
            string id, string messageId, UpdateMessageRequest req,
            BridgeDbContext db,
            ILogger<CogitationLogCategory> logger) =>
        {
            var cog = await db.Cogitations.FirstOrDefaultAsync(c => c.Id == id);
            if (cog is null)
            {
                logger.LogWarning("[Cogitations] Update message refused: cogitation={CogitationId} not found", id);
                return Results.NotFound();
            }

            var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId && m.CogitationId == id);
            if (msg is null)
            {
                logger.LogWarning("[Cogitations] Update message refused: message={MessageId} not found in cogitation={CogitationId}", messageId, id);
                return Results.NotFound();
            }

            if (req.SectionsJson is not null)
                msg.SectionsJson = req.SectionsJson;
            if (req.Content is not null)
                msg.Content = req.Content;
            if (req.ThinkingContent is not null)
                msg.ThinkingContent = req.ThinkingContent;

            cog.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            logger.LogInformation(
                "[Cogitations] Updated message id={MessageId} in cogitation={CogitationId}",
                msg.Id, cog.Id);

            return Results.Ok(new MessageDto(msg.Id, msg.CogitationId, msg.Role, msg.Content, msg.ThinkingContent, msg.SectionsJson, msg.CreatedAt, msg.ImageBase64, msg.ImageMediaType));
        });

        // POST /cogitations/{id}/compact — replace all messages with a single summary message,
        // used by the chat "/compact" command to reclaim context window.
        app.MapPost("/cogitations/{id}/compact", async (
            string id, CompactCogitationRequest req,
            BridgeDbContext db,
            ILogger<CogitationLogCategory> logger) =>
        {
            var cog = await db.Cogitations.FirstOrDefaultAsync(c => c.Id == id);
            if (cog is null)
            {
                logger.LogWarning("[Cogitations] Compact refused: cogitation={CogitationId} not found", id);
                return Results.NotFound();
            }

            var removed = await db.Messages.Where(m => m.CogitationId == id).ExecuteDeleteAsync();

            var summary = new BridgeMessage
            {
                CogitationId = id,
                Role         = "assistant",
                Content      = req.Summary,
            };
            db.Messages.Add(summary);
            cog.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            logger.LogInformation(
                "[Cogitations] Compacted cogitation={CogitationId}: removed {Removed} messages, left 1 summary",
                id, removed);

            return Results.Ok(new MessageDto(summary.Id, summary.CogitationId, summary.Role, summary.Content, summary.ThinkingContent, summary.SectionsJson, summary.CreatedAt, summary.ImageBase64, summary.ImageMediaType));
        });

        // GET /debug/cogitations — stats for the status page
        app.MapGet("/debug/cogitations", async (BridgeDbContext db) =>
        {
            var soul    = await db.Souls.AsNoTracking().FirstOrDefaultAsync();
            var cogCount = await db.Cogitations.CountAsync();
            var msgCount = await db.Messages.CountAsync();
            return Results.Ok(new
            {
                soul        = soul is null ? null : new { soul.Id, soul.Name, soul.ServerSoulId },
                cogitations = cogCount,
                messages    = msgCount,
            });
        });

        static CogitationDto ToDto(BridgeCogitation c) =>
            new(c.Id, c.SoulId, c.Title, c.AriaAvatarKey, c.SubAgentId, c.FolderId, c.CreatedAt, c.UpdatedAt);
    }
}

// Marker type for ILogger category.
internal sealed class CogitationLogCategory;

public record CreateCogitationRequest(string SoulId, string? AriaAvatarKey, string? SubAgentId, int? FolderId = null);
public record InitCogitationRequest(string Id, string ServerUserId, string? AriaAvatarKey, string? SubAgentId, int? FolderId = null);
public record UpdateCogitationRequest(string? Title, string? AriaAvatarKey, int? FolderId = null);
public record AddMessageRequest(string Role, string Content, string? ThinkingContent, string? SectionsJson = null, string? ImageBase64 = null, string? ImageMediaType = null);
public record UpdateMessageRequest(string? SectionsJson = null, string? Content = null, string? ThinkingContent = null);
public record CompactCogitationRequest(string Summary);
public record CogitationDto(string Id, string SoulId, string Title, string? AriaAvatarKey, string? SubAgentId, int? FolderId, DateTime CreatedAt, DateTime UpdatedAt);
public record MessageDto(string Id, string CogitationId, string Role, string Content, string? ThinkingContent, string? SectionsJson, DateTime CreatedAt, string? ImageBase64 = null, string? ImageMediaType = null);
