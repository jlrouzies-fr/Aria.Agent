using System.Net;
using System.Security.Cryptography;
using Aria.Shared;
using Aria.Web.Data;
using Aria.Web.Data.Bridge;
using Aria.Web.Data.Context;
using Aria.Web.Data.Users;
using Aria.Web.Services.ModelBridge;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aria.Tests.Web;

/// <summary>
/// End-to-end Layer A gate behaviour: a node-approved device (device-id cookie + a valid, node-signed
/// trust-device grant on record) passes the access gate from an external IP; a fresh/unapproved,
/// expired, or revoked device does not. Grants are minted exactly as the bridge signs them.
/// </summary>
public class TrustedDeviceGateTests : IClassFixture<WebApplicationFactory<Aria.Web.Program>>
{
    private readonly WebApplicationFactory<Aria.Web.Program> _factory;

    public TrustedDeviceGateTests(WebApplicationFactory<Aria.Web.Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GuestAccess:Codes"] = "UNUSED-CODE:2099-01-01T00:00:00Z"
                }));
            builder.ConfigureServices(services =>
            {
                var d = services.SingleOrDefault(x => x.ServiceType == typeof(IDbContextFactory<AppDbContext>));
                if (d != null) services.Remove(d);
                services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite("Data Source=aria-device-tests.db"));
            });
        });
    }

    private HttpClient ExternalClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost/"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.77");
        return client;
    }

    // Seeds a soul + a trusted-device row for it, signing the grant the way the bridge would.
    private async Task<string> SeedTrustedDeviceAsync(string deviceId, long expiryUnix, bool revoked = false)
    {
        var userId = $"soul-{Guid.NewGuid():N}";
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        var payload = NodeCrypto.GrantPayload(GrantService.DeviceGrant, deviceId, userId, expiryUnix);
        var sig     = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256));

        var f = _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await f.CreateDbContextAsync();
        db.Set<User>().Add(new User { Id = userId, Name = userId, PublicKey = pub });
        db.TrustedDevices.Add(new TrustedDevice
        {
            UserId = userId, DeviceId = deviceId, SignatureBase64 = sig,
            ExpiryUnix = expiryUnix, Revoked = revoked,
        });
        await db.SaveChangesAsync();
        return userId;
    }

    // Seeds a soul + an ENROLLED secondary node, and a device grant signed by that node key (not the
    // soul master key). Models "any bridge can approve." nodeRevoked flips the node's allow-list entry.
    private async Task SeedNodeApprovedDeviceAsync(string deviceId, long expiryUnix, bool nodeRevoked)
    {
        var userId = $"soul-{Guid.NewGuid():N}";
        using var soulKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var nodeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var soulPub = Convert.ToBase64String(soulKey.ExportSubjectPublicKeyInfo());
        var nodePub = Convert.ToBase64String(nodeKey.ExportSubjectPublicKeyInfo());

        var payload = NodeCrypto.GrantPayload(GrantService.DeviceGrant, deviceId, userId, expiryUnix);
        var sig     = Convert.ToBase64String(nodeKey.SignData(payload, HashAlgorithmName.SHA256));

        var f = _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await f.CreateDbContextAsync();
        db.Set<User>().Add(new User { Id = userId, Name = userId, PublicKey = soulPub });
        db.SoulNodeKeys.Add(new SoulNodeKey
        {
            UserId = userId, NodeId = NodeCrypto.Thumbprint(nodePub),
            NodePublicKeyBase64 = nodePub, Revoked = nodeRevoked,
        });
        db.TrustedDevices.Add(new TrustedDevice
        {
            UserId = userId, DeviceId = deviceId, SignatureBase64 = sig, ExpiryUnix = expiryUnix,
        });
        await db.SaveChangesAsync();
    }

    private static long InHours(int h) => DateTimeOffset.UtcNow.AddHours(h).ToUnixTimeSeconds();

    private HttpRequestMessage GetRootWithDevice(string? deviceId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        if (deviceId != null) req.Headers.Add("Cookie", $"aria-device={deviceId}");
        return req;
    }

    [Fact]
    public async Task ApprovedDevice_OpensGate()
    {
        var deviceId = $"dev-{Guid.NewGuid():N}";
        await SeedTrustedDeviceAsync(deviceId, InHours(24));

        var resp = await ExternalClient().SendAsync(GetRootWithDevice(deviceId));
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task NoDeviceCookie_IsForbidden()
    {
        var resp = await ExternalClient().SendAsync(GetRootWithDevice(null));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task UnknownDevice_IsForbidden()
    {
        var resp = await ExternalClient().SendAsync(GetRootWithDevice($"dev-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ExpiredGrant_IsForbidden()
    {
        var deviceId = $"dev-{Guid.NewGuid():N}";
        await SeedTrustedDeviceAsync(deviceId, InHours(-1));

        var resp = await ExternalClient().SendAsync(GetRootWithDevice(deviceId));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task RevokedDevice_IsForbidden()
    {
        var deviceId = $"dev-{Guid.NewGuid():N}";
        await SeedTrustedDeviceAsync(deviceId, InHours(24), revoked: true);

        var resp = await ExternalClient().SendAsync(GetRootWithDevice(deviceId));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task EnrolledNodeApproval_OpensGate()
    {
        // A device approved by a secondary node key (not the soul master) is trusted — "any bridge can approve".
        var deviceId = $"dev-{Guid.NewGuid():N}";
        await SeedNodeApprovedDeviceAsync(deviceId, InHours(24), nodeRevoked: false);

        var resp = await ExternalClient().SendAsync(GetRootWithDevice(deviceId));
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task RevokingApproverNode_DropsDevice()
    {
        // Revoking the node that approved the device invalidates the grant (its key is no longer accepted).
        var deviceId = $"dev-{Guid.NewGuid():N}";
        await SeedNodeApprovedDeviceAsync(deviceId, InHours(24), nodeRevoked: true);

        var resp = await ExternalClient().SendAsync(GetRootWithDevice(deviceId));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
