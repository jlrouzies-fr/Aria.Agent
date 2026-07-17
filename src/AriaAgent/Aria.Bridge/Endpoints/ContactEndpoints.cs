using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

public static class ContactEndpoints
{
    public static void MapContactEndpoints(this WebApplication app)
    {
        app.MapGet("/contacts", async (BridgeDbContext db) =>
        {
            var contacts = await db.Contacts.AsNoTracking()
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name, publicKey = c.PublicKey, avatarSpriteKey = c.AvatarSpriteKey, addedAt = c.AddedAt })
                .ToListAsync();
            return Results.Ok(contacts);
        });

        app.MapPost("/contacts", async (AddContactRequest req, BridgeDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest("Name required");
            if (string.IsNullOrWhiteSpace(req.PublicKey))
                return Results.BadRequest("PublicKey required");
            if (await db.Contacts.AnyAsync(c => c.PublicKey == req.PublicKey.Trim()))
                return Results.Conflict("Contact with this public key already exists");

            var contact = new BridgeContact
            {
                Name            = req.Name.Trim(),
                PublicKey       = req.PublicKey.Trim(),
                AvatarSpriteKey = req.AvatarSpriteKey,
            };
            db.Contacts.Add(contact);
            await db.SaveChangesAsync();
            return Results.Created($"/contacts/{contact.Id}", new { contact.Id });
        });

        app.MapDelete("/contacts/{id}", async (string id, BridgeDbContext db) =>
        {
            var rows = await db.Contacts.Where(c => c.Id == id).ExecuteDeleteAsync();
            return rows > 0 ? Results.Ok() : Results.NotFound();
        });
    }
}

public record AddContactRequest(string Name, string PublicKey, string? AvatarSpriteKey);
