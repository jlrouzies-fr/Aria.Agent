using System.Security.Cryptography;
using Aria.Shared;
using Aria.Web.Data;
using Aria.Web.Helpers;
using Aria.Web.Services;
using Aria.Web.Services.Auth;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.ModelBridge;

/// <summary>
/// SignalR hub connecting the server to the Aria.Bridge daemon (direct outbound connection).
/// The server pushes HandleRequest / HandleLocalRest down the connection; the daemon streams
/// chunks back via SendChunk / CompleteRequest / CompleteLocalRest.
/// </summary>
public class ModelBridgeHub : Hub
{
    private readonly ModelBridgeRegistry         _registry;
    private readonly AgentService                _agentService;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly UiAccessKnockService        _knockService;
    private readonly TerminalPtyService          _terminalPty;
    private readonly ILogger<ModelBridgeHub>     _log;

    public ModelBridgeHub(ModelBridgeRegistry registry, AgentService agentService,
        IDbContextFactory<AppDbContext> dbFactory, UiAccessKnockService knockService,
        TerminalPtyService terminalPty, ILogger<ModelBridgeHub> log)
    {
        _registry     = registry;
        _agentService = agentService;
        _dbFactory    = dbFactory;
        _knockService = knockService;
        _terminalPty  = terminalPty;
        _log          = log;
    }

    public override Task OnConnectedAsync()
    {
        _log.LogInformation("[Bridge] Client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    /// <summary>Called by the daemon to deliver a raw SSE chunk for a pending request.</summary>
    public Task SendChunk(string requestId, string data)
    {
        _registry.WriteChunk(requestId, data, Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>Called by the daemon to signal that a request has finished (or failed).</summary>
    public Task CompleteRequest(string requestId, bool success, string? error)
    {
        _registry.Complete(requestId, success, error, Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>Called by the daemon after completing a local REST call.</summary>
    public Task CompleteLocalRest(string requestId, int statusCode, string? body)
    {
        _registry.CompleteLocalRest(requestId, statusCode, body, Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>Called by the daemon to stream a chunk of PTY output to the listening web panel.</summary>
    public Task TerminalChunk(string sessionId, string bytesBase64)
    {
        _ = _terminalPty.DispatchChunkAsync(sessionId, Convert.FromBase64String(bytesBase64), Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>Called by the daemon when a PTY session exits.</summary>
    public Task TerminalClosed(string sessionId, int? exitCode)
    {
        _ = _terminalPty.DispatchClosedAsync(sessionId, exitCode, Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called by the Aria.Bridge daemon. Issues a single-use nonce for the subsequent
    /// RegisterDirectBridge call. The nonce is bound to this connection and discarded after use.
    /// </summary>
    public Task<string> GetDaemonChallenge(string userId)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        _registry.StoreDaemonChallenge(Context.ConnectionId, nonce);
        _log.LogInformation("[Bridge/Direct] Challenge issued for userId={UserId} connId={ConnId}",
            userId, Context.ConnectionId);
        return Task.FromResult(Convert.ToBase64String(nonce));
    }

    /// <summary>
    /// Called by the Aria.Bridge daemon after signing the nonce with its NODE key. The connection is
    /// accepted iff the node key is the soul key itself (the primary bridge) OR is in this soul's
    /// non-revoked allow-list (an enrolled additional node — §9.3). On success the node is registered
    /// (keyed by thumbprint) and the soul becomes verified.
    /// </summary>
    public async Task<bool> RegisterDirectBridge(string userId, string nodePublicKeyB64,
        string label, string platform, string nonceBase64, string signatureBase64)
    {
        var nonce = _registry.TakeDaemonChallenge(Context.ConnectionId);
        if (nonce == null || Convert.ToBase64String(nonce) != nonceBase64)
        {
            _log.LogWarning("[Bridge/Direct] Challenge mismatch for connId={ConnId}", Context.ConnectionId);
            return false;
        }

        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(nodePublicKeyB64)) return false;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (string.IsNullOrEmpty(user?.PublicKey))
        {
            _log.LogWarning("[Bridge/Direct] No public key on record for userId={UserId}", userId);
            return false;
        }

        // The signature must verify under the presented node key…
        if (!NodeCrypto.Verify(nodePublicKeyB64, nonce, signatureBase64))
        {
            _log.LogWarning("[Bridge/Direct] Node signature verification failed for userId={UserId}", userId);
            return false;
        }

        // …and that node key must be authorized: the soul key (primary) or a non-revoked allow-list entry.
        var thumb     = NodeCrypto.Thumbprint(nodePublicKeyB64);
        var isPrimary = nodePublicKeyB64 == user.PublicKey;
        var existing  = await db.SoulNodeKeys.FirstOrDefaultAsync(k => k.UserId == userId && k.NodeId == thumb);
        if (!isPrimary && existing is not { Revoked: false })
        {
            _log.LogWarning("[Bridge/Direct] Node {Thumb} not enrolled (or revoked) for userId={UserId}", thumb, userId);
            return false;
        }

        // Seed/refresh the allow-list row so the node shows in the device list (primary auto-seeds).
        if (existing == null)
        {
            db.SoulNodeKeys.Add(new SoulNodeKey
            {
                UserId = userId, NodeId = thumb, NodePublicKeyBase64 = nodePublicKeyB64,
                Label = label, Platform = platform, IsPrimary = isPrimary,
            });
        }
        else
        {
            existing.Label = label; existing.Platform = platform; existing.LastSeenAt = DateTime.UtcNow;
            if (isPrimary) existing.IsPrimary = true;
        }
        await db.SaveChangesAsync();

        _registry.RegisterNode(userId, thumb, label, platform, Context.ConnectionId);
        return true;
    }

    /// <summary>
    /// Called by the Aria.Bridge daemon to announce its public IP for the dynamic UI access gate.
    /// When an authenticated bridge knocks, its source IP is recorded so requests from the same
    /// network are allowed for a short TTL even before the browser has proven control.
    /// </summary>
    public async Task UiAccessKnock()
    {
        try
        {
            if (!_registry.TryGetUserId(Context.ConnectionId, out var userId)) return;
            var ip = ClientIpResolver.GetClientIp(Context.GetHttpContext())?.ToString();
            if (string.IsNullOrEmpty(ip)) return;
            await _knockService.RecordAsync(userId, ip, TimeSpan.FromMinutes(10));
            _log.LogInformation("[Bridge/Knock] Recorded UI access knock for user {UserId} from IP {Ip}", userId, ip);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Bridge/Knock] Failed to record UI access knock");
            throw;
        }
    }

    /// <summary>
    /// Called by a freshly-enrolled bridge after it connects, to fetch the sync DEK that the approving
    /// node wrapped to its public key at enrollment (§11). Returns the opaque wrapped blob (or null if
    /// none stored yet); only the holder of the node private key can unwrap it, so the server relaying
    /// it leaks nothing.
    /// </summary>
    public async Task<string?> GetWrappedDek(string userId, string nodePublicKeyB64)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(nodePublicKeyB64)) return null;
        var thumb = NodeCrypto.Thumbprint(nodePublicKeyB64);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.SoulNodeKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.UserId == userId && k.NodeId == thumb && !k.Revoked);
        return row?.WrappedDek;
    }

    /// <summary>
    /// Returns the verifiable node roster for a soul. Each non-revoked enrolled node is returned with
    /// its public key, the enrollment certificate signed by its approver, and the approver's public key.
    /// The bridge re-verifies the certificate locally before trusting any node as a grant signer.
    /// </summary>
    public async Task<IReadOnlyList<SoulNodeRosterEntry>> GetSoulNodeRoster(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return [];
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.SoulNodeKeys.AsNoTracking()
            .Where(k => k.UserId == userId && !k.Revoked)
            .ToListAsync();

        return rows.Select(k => new SoulNodeRosterEntry(
            k.NodePublicKeyBase64,
            k.EnrollmentCertB64,
            k.ApproverPublicKeyBase64,
            k.Label ?? "",
            k.EnrollmentExpiryUnix ?? 0,
            k.IsPrimary)).ToList();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _log.LogInformation("[Bridge] Client disconnected: {ConnectionId} reason={Reason}",
            Context.ConnectionId, exception?.Message ?? "clean");
        _registry.Unregister(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}


