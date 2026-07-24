using System.Text.Json;
using Aria.Web.Services.Cogitations;

namespace Aria.Web.Services.ModelBridge;

/// <summary>
/// Drives the reactive in-chat context approval ceremony (defense-in-depth plan §4, Phase 2B).
/// The browser asks a node that holds a signing key and has a human present to approve a session-
/// scoped context grant; on approval the grant is immediately replicated to the user's other nodes
/// and the halted cogitation turn is retried.
///
/// The same ceremony backs any other server-relayed sensitive surface — the Explorer panel, the "#"
/// file picker, git operations — via <see cref="RequestGrantAsync"/>: the grant is session-scoped and
/// shared, so whichever surface asks first covers all of them for the grant's lifetime.
/// </summary>
public sealed class ContextApprovalService(
    ModelBridgeRegistry registry,
    GrantReplicationService replication,
    CogitationRunRegistry runRegistry,
    ILogger<ContextApprovalService> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan MaxWait      = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long a pre-authorised vigil's grant stays live, measured from its scheduled slot start: the
    /// booked hour plus a one-hour allowance covering late/overdue dispatch and the 30-minute run cap.
    /// Deliberately tight — the seal covers only this vigil's window, nothing more.
    /// </summary>
    public static readonly TimeSpan VigilGrantWindow = TimeSpan.FromHours(2);

    /// <summary>How long a pre-authorised Hive collective's grant stays live, from launch. A collective
    /// run isn't slot-bound (it fans out across drones and can iterate for a while), so this is a flat
    /// run window rather than a tight slot — long enough to cover a full Overmind→drones→synthesis pass.</summary>
    public static readonly TimeSpan HiveGrantWindow = TimeSpan.FromHours(8);

    /// <summary>
    /// Requests a session-scoped context grant from a connected node, polls for the human's decision,
    /// replicates the grant to siblings on approval, and retries the halted cogitation turn.
    /// Returns true if approved and the retry was started; false if rejected, expired, or unreachable.
    /// </summary>
    public async Task<bool> RequestApprovalAsync(
        string userId, string? sessionId, int cogitationId, CancellationToken ct = default)
    {
        var granted = await RequestGrantAsync(userId, sessionId, ct);
        if (!granted)
        {
            // Refused / expired / timed out: release the halted run so the cogitation isn't bricked —
            // it would otherwise sit in the registry forever, rejecting every new send as busy.
            await runRegistry.AbandonContextApprovalAsync(cogitationId,
                "\n// SEAL REFUSED OR EXPIRED — the action was not performed. Send a new message to continue. //");
            return false;
        }

        var retried = runRegistry.RetryContextApproval(cogitationId);
        if (retried == null)
            logger.LogWarning("Context approval retry failed to start for cogitation {CogitationId}", cogitationId);
        return retried != null;
    }

    /// <summary>
    /// Requests a session-scoped context grant from a connected node and polls for the human's
    /// decision — the approval ceremony without the cogitation retry, for non-chat surfaces
    /// (Explorer, file picker, git). On approval the grant is replicated to the user's other nodes;
    /// the caller then simply retries its own blocked operation.
    ///
    /// The grant is shared with the chat agent: if a live grant already covers this session — or one
    /// appears while we poll (the human approved a parallel ceremony from another surface) — this
    /// returns immediately without re-prompting.
    /// Returns true when a grant is live for the session; false if rejected, expired, or unreachable.
    /// </summary>
    public async Task<bool> RequestGrantAsync(
        string userId, string? sessionId, CancellationToken ct = default)
    {
        // Already covered? Then there is nothing to ask for. (Also the fast path when a parallel
        // ceremony from another surface approved between our blocked call and this request.)
        if (await HasLiveGrantAsync(userId, sessionId)) return true;

        // Open the ceremony on the node the human pinned for approvals (else the default). The /poll below
        // MUST hit the same node — the pending approval lives in-memory on the node that received /request.
        var approvalNode = registry.ResolveApprovalNode(userId);

        var reqBody = JsonSerializer.Serialize(new { sessionId });
        var start = await registry.SendLocalRestAsync(userId, "POST", "/context/approve/request", reqBody, approvalNode);
        if (start is not { StatusCode: 200, Body: { } startBody })
        {
            logger.LogWarning("Context approval request could not reach the node for user {User}", userId);
            return false;
        }

        string? id;
        try
        {
            using var doc = JsonDocument.Parse(startBody);
            id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }
        catch { return false; }

        if (string.IsNullOrEmpty(id))
        {
            logger.LogWarning("Context approval request returned no id for user {User}", userId);
            return false;
        }

        var deadline = DateTime.UtcNow + MaxWait;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(PollInterval, ct);

            var poll = await registry.SendLocalRestAsync(userId, "POST", $"/context/approve/{id}/poll", nodeId: approvalNode);
            if (poll is not { StatusCode: 200, Body: { } pollBody }) continue;

            string? status;
            try
            {
                using var doc = JsonDocument.Parse(pollBody);
                status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "pending";
            }
            catch { continue; }

            switch (status)
            {
                case "approved":
                    logger.LogInformation("Context approval {ApprovalId} granted for user {User}", id[..8], userId);
                    await ReplicateBestEffortAsync(userId);
                    return true;
                case "rejected":
                case "expired":
                    // Our ceremony was refused — but a parallel one (another surface, another tab)
                    // may have granted the session in the meantime. The grant is shared, so honour it.
                    if (await HasLiveGrantAsync(userId, sessionId))
                    {
                        await ReplicateBestEffortAsync(userId);
                        return true;
                    }
                    return false;
            }

            // Still pending on our request — but if the human approved a DIFFERENT ceremony for this
            // session (e.g. a halted chat turn while the Explorer was waiting), the shared grant now
            // covers us and there is no reason to keep the human on our page.
            if (await HasLiveGrantAsync(userId, sessionId))
            {
                logger.LogInformation("Context approval {ApprovalId} superseded by a parallel grant for user {User}", id[..8], userId);
                await ReplicateBestEffortAsync(userId);
                return true;
            }
        }

        logger.LogInformation("Context approval {ApprovalId} timed out for user {User}", id[..8], userId);
        return false;
    }

    /// <summary>Outcome of a pre-authorisation ceremony driven while the human is present (vigil booking
    /// or Hive launch), for an unattended run that will happen later or on another node.</summary>
    public enum VigilPreauthResult
    {
        /// <summary>The relevant node isn't enforcing Layer B — no grant needed.</summary>
        NotRequired,
        /// <summary>A node-signed grant now covers the run; it can proceed unattended.</summary>
        Granted,
        /// <summary>The human refused, or the ceremony timed out — the run must not be scheduled/started.</summary>
        Refused,
        /// <summary>No node could be reached to run the ceremony.</summary>
        NodeUnavailable,
    }

    /// <summary>Outcome of a Hive pre-authorisation, plus what the human chose. <paramref name="OneShot"/>
    /// is true for a "this run only" seal — the caller must revoke the grant when the run ends (via
    /// <see cref="RevokeHiveSealAsync"/>) so the next launch re-asks. <paramref name="ApprovalNode"/> is
    /// the node the grant was signed on (where the revoke must be sent).</summary>
    public readonly record struct HivePreauthResult(VigilPreauthResult Outcome, bool OneShot, string? ApprovalNode);

    /// <summary>
    /// Pre-authorises ONE scheduled vigil while the human is present at booking time. Grant scoped to
    /// <c>vigil:{jobId}</c>, lapsing at <paramref name="slotExpiry"/>. See <see cref="PreauthorizeUnattendedAsync"/>.
    /// </summary>
    public async Task<VigilPreauthResult> PreauthorizeVigilAsync(
        string userId, int jobId, string? preferredNode, DateTimeOffset slotExpiry,
        string taskPreview, string slotLabel, CancellationToken ct = default)
        => (await PreauthorizeUnattendedAsync(
            userId, $"vigil:{jobId}", slotExpiry, "vigil", taskPreview, slotLabel, preferredNode, ct)).Outcome;

    /// <summary>
    /// Pre-authorises a Hive collective run while the human is present at launch. One grant scoped to
    /// <c>hive:{collectiveId}</c> (lapsing after <see cref="HiveGrantWindow"/>) covers the whole
    /// Overmind→drones→synthesis fan-out; the orchestrator stamps that same session on every headless
    /// sub-call (see <c>AgentBackgroundExecutor.WithAmbientSession</c>). Because the grant is replicated,
    /// it reaches drones bound to remote, unattended bridges too. See <see cref="PreauthorizeUnattendedAsync"/>.
    /// </summary>
    public Task<HivePreauthResult> PreauthorizeHiveAsync(
        string userId, int collectiveId, string? preferredNode, string objectivePreview,
        string collectiveLabel, CancellationToken ct = default)
        => PreauthorizeUnattendedAsync(
            userId, $"hive:{collectiveId}", DateTimeOffset.UtcNow + HiveGrantWindow,
            "hive", objectivePreview, collectiveLabel, preferredNode, ct);

    /// <summary>
    /// Shared pre-authorisation ceremony for an unattended run (vigil or Hive). The seal ceremony runs on
    /// whichever node the human is at (the default node, exactly like the in-chat approval) — NOT
    /// necessarily the node that will run the work. On approval the node-signed grant (scoped to
    /// <paramref name="sessionId"/>, lapsing at <paramref name="expiry"/>) is replicated across the
    /// soul's nodes, so it reaches and persists on whichever bridge — including a remote, unattended one —
    /// later runs the work. Enforcement is probed on <paramref name="preferredNode"/> (else the default);
    /// a no-op when that node isn't enforcing.
    ///
    /// Delivery caveat: replication reaches a node only while it is connected. Forcing a pass now covers
    /// every node online at approval time; a node offline now picks the grant up from the background
    /// replication loop the next time it and a grant-holder are connected together.
    /// </summary>
    private async Task<HivePreauthResult> PreauthorizeUnattendedAsync(
        string userId, string sessionId, DateTimeOffset expiry, string kind,
        string taskPreview, string label, string? preferredNode, CancellationToken ct)
    {
        // The grant is SIGNED on the node the human approves at (the pinned approval node, else default),
        // so that is where a live grant provably lives on a re-run — check idempotency THERE, not on some
        // other node that only has it if cross-node replication both reached and verified it. Enforcement,
        // by contrast, is a property of the node that will RUN the work (preferredNode, else the approval
        // node): if it isn't enforcing, no grant is needed.
        var approvalNode = registry.ResolveApprovalNode(userId);
        if (approvalNode == null) return new(VigilPreauthResult.NodeUnavailable, false, null);
        var runNode = preferredNode ?? approvalNode;

        // Enforcement on the running node.
        var runStatus = await registry.SendLocalRestAsync(
            userId, "GET", $"/context/status?session={Uri.EscapeDataString(sessionId)}", nodeId: runNode);
        if (runStatus is not { StatusCode: 200, Body: { } runBody })
            return new(VigilPreauthResult.NodeUnavailable, false, approvalNode);
        bool enforcing;
        try
        {
            using var doc = JsonDocument.Parse(runBody);
            enforcing = doc.RootElement.TryGetProperty("enforcementEnabled", out var e) && e.ValueKind == JsonValueKind.True;
        }
        catch { return new(VigilPreauthResult.NodeUnavailable, false, approvalNode); }
        if (!enforcing) return new(VigilPreauthResult.NotRequired, false, approvalNode);

        // Already approved? Read the grant from the approval node (where it was signed). Reuse the run
        // node's answer when they're the same node, to avoid a second round-trip.
        if (await NodeReportsGrantAsync(userId, sessionId, approvalNode, approvalNode == runNode ? runBody : null))
            return new(VigilPreauthResult.Granted, false, approvalNode);   // idempotent re-run

        var reqBody = JsonSerializer.Serialize(new
        {
            sessionId,
            expiryUnix  = expiry.ToUnixTimeSeconds(),
            kind,
            taskPreview,
            slotLabel   = label,
        });
        var start = await registry.SendLocalRestAsync(userId, "POST", "/context/approve/request", reqBody, approvalNode);
        if (start is not { StatusCode: 200, Body: { } startBody })
            return new(VigilPreauthResult.NodeUnavailable, false, approvalNode);

        string? id;
        try
        {
            using var doc = JsonDocument.Parse(startBody);
            id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }
        catch { return new(VigilPreauthResult.NodeUnavailable, false, approvalNode); }
        if (string.IsNullOrEmpty(id)) return new(VigilPreauthResult.NodeUnavailable, false, approvalNode);

        var deadline = DateTime.UtcNow + MaxWait;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(PollInterval, ct);

            var poll = await registry.SendLocalRestAsync(userId, "POST", $"/context/approve/{id}/poll", nodeId: approvalNode);
            if (poll is not { StatusCode: 200, Body: { } pollBody }) continue;

            string? st;
            bool oneShot = false;
            try
            {
                using var doc = JsonDocument.Parse(pollBody);
                st      = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "pending";
                oneShot = doc.RootElement.TryGetProperty("oneShot", out var o) && o.ValueKind == JsonValueKind.True;
            }
            catch { continue; }

            switch (st)
            {
                case "approved":
                    // Push the signed grant to every connected sibling now — the executing node included,
                    // if it is online. Offline nodes catch it from the background replication loop later.
                    await ReplicateBestEffortAsync(userId);
                    logger.LogInformation("{Kind} {Session} pre-authorised for user {User}{OneShot} (replicated to siblings)",
                        kind, sessionId, userId, oneShot ? " one-shot" : "");
                    return new(VigilPreauthResult.Granted, oneShot, approvalNode);
                case "rejected":
                case "expired":
                    return new(VigilPreauthResult.Refused, false, approvalNode);
            }
        }

        logger.LogInformation("{Kind} {Session} pre-authorisation timed out for user {User}", kind, sessionId, userId);
        return new(VigilPreauthResult.Refused, false, approvalNode);   // no verdict in time ≈ not authorised
    }

    // True when a node reports a live grant for the session. Parses a status body already fetched from
    // that node when supplied (prefetched), else fetches /context/status from it.
    private async Task<bool> NodeReportsGrantAsync(string userId, string sessionId, string nodeId, string? prefetchedBody)
    {
        var body = prefetchedBody;
        if (body == null)
        {
            var status = await registry.SendLocalRestAsync(
                userId, "GET", $"/context/status?session={Uri.EscapeDataString(sessionId)}", nodeId: nodeId);
            if (status is not { StatusCode: 200, Body: { } b }) return false;
            body = b;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("granted", out var g) && g.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    /// <summary>True if the node reports a live context grant for this session (session-scoped or
    /// soul-wide). Read-only status endpoint — never prompts.</summary>
    private async Task<bool> HasLiveGrantAsync(string userId, string? sessionId)
    {
        // Same node the ceremony runs on (pinned approval node, else default): the grant provably lives
        // where it was signed. Asking the default node instead means a freshly-joined device (it becomes
        // the default, being the most recent connection) answers "no grant" for a live seal and the
        // human gets re-prompted.
        var path = "/context/status" + (string.IsNullOrEmpty(sessionId) ? "" : $"?session={Uri.EscapeDataString(sessionId)}");
        var resp = await registry.SendLocalRestAsync(userId, "GET", path, nodeId: registry.ResolveApprovalNode(userId));
        if (resp is not { StatusCode: 200, Body: { } body }) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("granted", out var g) && g.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    /// <summary>
    /// Wave 5 "/scope add": asks the pinned approval node to mint a session-scoped, node-signed PATH
    /// grant for <paramref name="path"/> (the human consents on the same local ceremony page as a
    /// context seal). The server only relays the ask — the grant is minted and stored node-side, then
    /// replicated to siblings. Returns true when a live grant covers the path for this session.
    /// </summary>
    public async Task<bool> RequestPathGrantAsync(
        string userId, string sessionId, string path, CancellationToken ct = default)
    {
        // The pending approval lives in-memory on the node that receives /request, so the /poll below
        // MUST hit that same node — the pinned approval node, exactly like the context ceremony.
        var approvalNode = registry.ResolveApprovalNode(userId);
        if (approvalNode == null) return false;

        // Already covered (e.g. a parallel ceremony, or a grant from an earlier ask)? Nothing to do.
        if (await HasLivePathGrantAsync(userId, sessionId, path, approvalNode)) return true;

        var reqBody = JsonSerializer.Serialize(new { sessionId, kind = "scope", path });
        var start = await registry.SendLocalRestAsync(userId, "POST", "/context/approve/request", reqBody, approvalNode);
        if (start is not { StatusCode: 200, Body: { } startBody })
        {
            logger.LogWarning("Path expansion request could not reach the node for user {User}", userId);
            return false;
        }

        string? id;
        try
        {
            using var doc = JsonDocument.Parse(startBody);
            id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }
        catch { return false; }

        if (string.IsNullOrEmpty(id))
        {
            logger.LogWarning("Path expansion request returned no id for user {User}", userId);
            return false;
        }

        var deadline = DateTime.UtcNow + MaxWait;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(PollInterval, ct);

            var poll = await registry.SendLocalRestAsync(userId, "POST", $"/context/approve/{id}/poll", nodeId: approvalNode);
            if (poll is not { StatusCode: 200, Body: { } pollBody }) continue;

            string? status;
            try
            {
                using var doc = JsonDocument.Parse(pollBody);
                status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "pending";
            }
            catch { continue; }

            switch (status)
            {
                case "approved":
                    logger.LogInformation("Path expansion {ApprovalId} granted for user {User}: {Path}", id[..8], userId, path);
                    await ReplicateBestEffortAsync(userId);
                    return true;
                case "rejected":
                case "expired":
                    // A parallel ceremony may have granted this path in the meantime — honour it.
                    if (await HasLivePathGrantAsync(userId, sessionId, path, approvalNode))
                    {
                        await ReplicateBestEffortAsync(userId);
                        return true;
                    }
                    return false;
            }
        }

        logger.LogInformation("Path expansion {ApprovalId} timed out for user {User}", id[..8], userId);
        return false;
    }

    /// <summary>The session's live path expansions as reported by the node they were minted on —
    /// read-only, never prompts. Used by the chat "/scope" display and to refresh the governance
    /// scope-lock's soft copy. Empty when the node is unreachable.</summary>
    public async Task<IReadOnlyList<string>> GetLivePathExpansionsAsync(
        string userId, string sessionId, string? nodeId = null)
    {
        var resp = await registry.SendLocalRestAsync(
            userId, "GET", $"/scope/list?session={Uri.EscapeDataString(sessionId)}",
            nodeId: nodeId ?? registry.ResolveApprovalNode(userId));
        if (resp is not { StatusCode: 200, Body: { } body }) return [];
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("path", out var p) ? p.GetString() : null)
                .Where(p => !string.IsNullOrEmpty(p))
                .Cast<string>()
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>Revokes a session's path expansion on the node that minted it ("/scope remove").
    /// A narrowing operation — safe for the server to relay. The node signs a revocation tombstone
    /// on revoke; it reaches siblings through the same replication channel as the grants, so the
    /// replicated copies die too rather than merely lapsing at expiry.</summary>
    public async Task<bool> RevokePathGrantAsync(
        string userId, string sessionId, string path, string? nodeId = null)
    {
        var body = JsonSerializer.Serialize(new { sessionId, path });
        var resp = await registry.SendLocalRestAsync(
            userId, "POST", "/scope/revoke", body, nodeId ?? registry.ResolveApprovalNode(userId));
        if (resp is not { StatusCode: 200 }) return false;
        await ReplicateBestEffortAsync(userId);   // push the fresh tombstone to siblings now
        return true;
    }

    /// <summary>True when the node reports a live, verified path grant for this exact path and
    /// session. Read-only status endpoint — never prompts.</summary>
    private async Task<bool> HasLivePathGrantAsync(string userId, string sessionId, string path, string? nodeId)
    {
        var resp = await registry.SendLocalRestAsync(
            userId, "GET",
            $"/scope/status?session={Uri.EscapeDataString(sessionId)}&path={Uri.EscapeDataString(path)}",
            nodeId: nodeId);
        if (resp is not { StatusCode: 200, Body: { } body }) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("granted", out var g) && g.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    private async Task ReplicateBestEffortAsync(string userId)
    {
        try { await replication.ReplicateAsync(userId); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Grant replication failed after context approval for user {User}", userId);
        }
    }
}
