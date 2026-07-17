using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aria.Web.Endpoints;

// Accepts both JSON string and number for fields that were integers before the GUID migration.
class NumberOrStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
        reader.TokenType == JsonTokenType.Number ? reader.GetInt64().ToString() : reader.GetString();
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions o) =>
        writer.WriteStringValue(value);
}

// Server-issued, single-use, short-TTL nonces for the unlink challenge-response (replay protection).
static class UnlinkChallengeStore
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Nonce, DateTime Expiry)> _store = new();
    private static readonly TimeSpan _ttl = TimeSpan.FromMinutes(2);

    public static string Issue(string publicKey)
    {
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _store[publicKey] = (nonce, DateTime.UtcNow + _ttl);
        return nonce;
    }

    // Returns the nonce and removes it (single-use); null if absent or expired.
    public static string? Consume(string publicKey)
    {
        if (!_store.TryRemove(publicKey, out var entry)) return null;
        return entry.Expiry >= DateTime.UtcNow ? entry.Nonce : null;
    }
}

// Per (serverSoulId, newPublicKey) nonces for key rotation — prevents replay of rotation requests.
static class RotationChallengeStore
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Nonce, DateTime Expiry)> _store = new();
    private static readonly TimeSpan _ttl = TimeSpan.FromMinutes(2);

    private static string Key(string serverSoulId, string newPub) => $"{serverSoulId}|{newPub}";

    public static string Issue(string serverSoulId, string newPub)
    {
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _store[Key(serverSoulId, newPub)] = (nonce, DateTime.UtcNow + _ttl);
        return nonce;
    }

    public static string? Consume(string serverSoulId, string newPub)
    {
        var k = Key(serverSoulId, newPub);
        if (!_store.TryRemove(k, out var entry)) return null;
        return entry.Expiry >= DateTime.UtcNow ? entry.Nonce : null;
    }
}

static class ProfanityFilter
{
    // Substring check — any of these anywhere in the lowercased name triggers rejection.
    private static readonly string[] _terms =
    [
        "fuck", "shit", "cunt", "bitch", "asshole", "bastard", "whore", "slut",
        "nigger", "nigga", "faggot", "retard", "kike", "chink", "spic", "wetback",
        "cock", "dick", "pussy", "penis", "vagina", "anal", "porn", "rape",
        "nazi", "hitler",
    ];

    public static bool Contains(string text)
    {
        var lower = text.ToLowerInvariant();
        return _terms.Any(t => lower.Contains(t));
    }
}
