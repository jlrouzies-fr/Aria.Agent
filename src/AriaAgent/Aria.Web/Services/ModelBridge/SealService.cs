using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aria.Harness.Governance;
using Aria.Shared;
using Aria.Web.Data;
using Aria.Web.Services.Node;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.ModelBridge;

/// <summary>
/// Drives the Inquisitorial Seal from the server side: sends a nonce to the node over the tunnel,
/// polls for the human's local decision, and cryptographically verifies the returned signature
/// against the soul public key. A valid signature is proof a human at the node authorised the
/// action — the server cannot produce it itself.
/// </summary>
public sealed class SealService(
    ModelBridgeRegistry registry,
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<SealService> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan MaxWait      = TimeSpan.FromMinutes(3);

    public async Task<bool> RequestSealAsync(string userId, ActionDescriptor desc, CancellationToken ct)
        => !string.IsNullOrEmpty(await RequestSealIdAsync(userId, desc, ct));

    /// <summary>
    /// Drives the seal ceremony and returns the approved seal id if the node granted and signed it,
    /// verified against the soul public key. Returns null if rejected, expired, or unreachable.
    /// The signature covers a canonical human-readable statement of capability, scope, and expiry
    /// (F-6 sign-what-you-show), and the server verifies that statement matches the requested action.
    /// </summary>
    public async Task<string?> RequestSealIdAsync(string userId, ActionDescriptor desc, CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var result = await RunCeremonyAsync(userId, desc, nonce, signStatement: true, ct);
        return result?.Id;
    }

    /// <summary>
    /// Runs the Seal ceremony over caller-supplied bytes and returns the node's base64 signature over
    /// exactly those bytes (verified against the soul public key), or null if rejected/expired/
    /// unreachable. This is the reusable primitive behind node-signed grants (defense-in-depth plan
    /// §5): the caller passes canonical grant bytes (<see cref="NodeCrypto.GrantPayload"/>) and gets
    /// back a signature it can persist and later re-verify without any server-held nonce.
    /// </summary>
    public async Task<string?> RequestSignatureAsync(
        string userId, ActionDescriptor desc, byte[] payloadToSign, CancellationToken ct)
    {
        var result = await RunCeremonyAsync(userId, desc, payloadToSign, signStatement: false, ct);
        return result?.Signature;
    }

    /// <summary>
    /// Core ceremony: send the signing payload as the seal nonce, poll for the human's verdict, and on
    /// approval verify the returned signature against the soul public key.
    /// When <paramref name="signStatement"/> is true the node signs a canonical human-readable statement
    /// (F-6); the server reconstructs that statement and checks it matches the requested capability and
    /// payload before accepting the seal. When false the node signs the raw payload bytes, used for
    /// durable grants that are verified independently by <see cref="GrantVerifier"/>.
    /// </summary>
    private async Task<(string Id, string Signature)?> RunCeremonyAsync(
        string userId, ActionDescriptor desc, byte[] payloadToSign, bool signStatement, CancellationToken ct)
    {
        var reqBody = JsonSerializer.Serialize(new
        {
            toolName      = desc.ToolName,
            argsPreview   = desc.ArgsPreview,
            reason        = desc.Reason,
            nonceBase64   = Convert.ToBase64String(payloadToSign),
            signStatement
        });

        var start = await registry.SendLocalRestAsync(userId, "POST", "/seal/request", reqBody);
        if (start is not { StatusCode: 200, Body: { } startBody })
        {
            logger.LogWarning("Seal request could not reach the node for user {User}", userId);
            return null;
        }

        string id;
        try
        {
            using var doc = JsonDocument.Parse(startBody);
            id = doc.RootElement.GetProperty("id").GetString() ?? "";
        }
        catch { return null; }
        if (string.IsNullOrEmpty(id)) return null;

        var deadline = DateTime.UtcNow + MaxWait;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(PollInterval, ct);

            var poll = await registry.SendLocalRestAsync(userId, "POST", "/seal/poll",
                JsonSerializer.Serialize(new { id }));
            if (poll is not { StatusCode: 200, Body: { } pollBody }) continue;

            string status; string? sig; string? statement; DateTime? expiresAt;
            try
            {
                using var doc = JsonDocument.Parse(pollBody);
                status    = doc.RootElement.GetProperty("status").GetString() ?? "pending";
                sig       = doc.RootElement.TryGetProperty("signatureBase64", out var se) ? se.GetString() : null;
                statement = doc.RootElement.TryGetProperty("statement", out var st) ? st.GetString() : null;
                expiresAt = doc.RootElement.TryGetProperty("expiresAt", out var ex) && ex.ValueKind == JsonValueKind.String
                    ? ex.GetDateTime()
                    : null;
            }
            catch { continue; }

            switch (status)
            {
                case "approved" when sig != null:
                    var pub = await GetSoulPublicKeyAsync(userId);
                    if (pub == null)
                    {
                        logger.LogWarning("Seal approved but no soul public key for user {User}", userId);
                        return null;
                    }

                    bool ok;
                    if (signStatement)
                    {
                        if (string.IsNullOrEmpty(statement) || !expiresAt.HasValue)
                        {
                            logger.LogWarning("Seal statement missing for user {User}", userId);
                            return null;
                        }

                        var expected = SealStatement.Build(desc.ToolName, desc.Reason, desc.ArgsPreview, expiresAt.Value, payloadToSign);
                        if (!string.Equals(expected, statement, StringComparison.Ordinal))
                        {
                            logger.LogWarning("Seal statement mismatch for user {User}", userId);
                            return null;
                        }

                        ok = NodeCrypto.Verify(pub, Encoding.UTF8.GetBytes(statement), sig);
                    }
                    else
                    {
                        ok = NodeCrypto.Verify(pub, payloadToSign, sig);
                    }

                    if (!ok)
                    {
                        logger.LogWarning("Seal signature failed verification for user {User}", userId);
                        return null;
                    }
                    return (id, sig);
                case "rejected":
                case "expired":
                    return null;
            }
        }

        logger.LogInformation("Seal timed out for user {User}", userId);
        return null;
    }

    private async Task<string?> GetSoulPublicKeyAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        return user?.PublicKey;
    }
}
