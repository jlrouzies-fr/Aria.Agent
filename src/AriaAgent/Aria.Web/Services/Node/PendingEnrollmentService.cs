using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Aria.Web.Services.Node;

/// <summary>
/// One device awaiting pairing approval. The new bridge POSTs its node pubkey + a short join code
/// (we keep only the code's hash); the human approves it from a soul-verified Aria.Web session by
/// typing the code shown on the device. Transient + in-memory: a pending request is not an account
/// grant, it expires, and nothing is enrolled until the code matches AND an approver bridge signs.
/// </summary>
public record PendingEnrollment(string NodeId, string NodePublicKey, string Label, string Platform,
    string CodeHash, DateTime CreatedAt);

/// <summary>UI-facing view (never exposes the code hash or full pubkey).</summary>
public record PendingNodeInfo(string NodeId, string Label, string Platform, DateTime CreatedAt);

public class PendingEnrollmentService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    // userId → (nodeId → pending). nodeId = thumbprint of the node pubkey.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PendingEnrollment>> _pending = new();

    /// <summary>Fired when a user's pending set changes (added/approved/expired). Argument = userId.</summary>
    public event Action<string>? Changed;

    public static string HashCode(string code) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim())));

    /// <summary>Records (or refreshes) a pending enrollment for a device. Returns the derived nodeId.</summary>
    public void Add(string userId, PendingEnrollment pending)
    {
        var map = _pending.GetOrAdd(userId, _ => new ConcurrentDictionary<string, PendingEnrollment>());
        map[pending.NodeId] = pending;
        Changed?.Invoke(userId);
    }

    /// <summary>Live pending devices for a user (drops expired entries first).</summary>
    public IReadOnlyList<PendingNodeInfo> List(string userId)
    {
        Prune(userId);
        return _pending.TryGetValue(userId, out var map)
            ? map.Values.OrderBy(p => p.CreatedAt)
                 .Select(p => new PendingNodeInfo(p.NodeId, p.Label, p.Platform, p.CreatedAt)).ToList()
            : [];
    }

    /// <summary>Returns the pending entry iff it exists, is unexpired, and the code matches.</summary>
    public PendingEnrollment? TakeIfCodeMatches(string userId, string nodeId, string code)
    {
        Prune(userId);
        if (!_pending.TryGetValue(userId, out var map) || !map.TryGetValue(nodeId, out var pe)) return null;
        if (pe.CodeHash != HashCode(code)) return null;
        map.TryRemove(nodeId, out _);
        Changed?.Invoke(userId);
        return pe;
    }

    public void Remove(string userId, string nodeId)
    {
        if (_pending.TryGetValue(userId, out var map) && map.TryRemove(nodeId, out _))
            Changed?.Invoke(userId);
    }

    /// <summary>Drops every pending enrollment for a soul (e.g. after the soul is unlinked/wiped).</summary>
    public void ClearForUser(string userId)
    {
        if (_pending.TryRemove(userId, out _))
            Changed?.Invoke(userId);
    }

    private void Prune(string userId)
    {
        if (!_pending.TryGetValue(userId, out var map)) return;
        var cutoff = DateTime.UtcNow - Ttl;
        var stale  = map.Where(kv => kv.Value.CreatedAt < cutoff).Select(kv => kv.Key).ToList();
        foreach (var k in stale) map.TryRemove(k, out _);
        if (stale.Count > 0) Changed?.Invoke(userId);
    }
}
