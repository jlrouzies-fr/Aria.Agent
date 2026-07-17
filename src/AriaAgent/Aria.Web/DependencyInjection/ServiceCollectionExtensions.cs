using System.Threading.RateLimiting;
using Aria.Harness.Core;
using Aria.Harness.Formats;
using Aria.Web.Data;
using Aria.Web.Helpers;
using Aria.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.DependencyInjection;

public static class ServiceCollectionExtensions
{
    // Applied per resolved client IP. The anonymous, unauthenticated surfaces — the guest-code POST
    // and the /api/bridge/* REST endpoints (register-soul / challenge issuers / enroll / revoke) — are
    // the brute-force and user-row-spam targets; everything else is soul-verified or cheap.
    public const string GuestCodePolicy = "access-code";

    public static IServiceCollection AddAriaServices(this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        services.AddHttpContextAccessor();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Global limiter: throttle the anonymous /api/bridge/* REST surface per client IP — looser
            // than the guest gate (legit bridges reconnect in bursts) but a hard ceiling on grinding
            // challenge nonces and spamming register-soul into new user rows. The /api/modelbridge
            // SignalR hub and all authenticated app traffic are left unlimited here.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                ctx.Request.Path.StartsWithSegments("/api/bridge", StringComparison.OrdinalIgnoreCase)
                    ? RateLimitPartition.GetFixedWindowLimiter(ClientPartition(ctx),
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) })
                    : RateLimitPartition.GetNoLimiter("unlimited"));

            // Guest invite-code submission: tight, to defeat code guessing (applied to that endpoint).
            options.AddPolicy(GuestCodePolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ClientPartition(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(5) }));
        });

        // Aria.Harness orchestration layer
        services.AddSingleton<IFormatCache, WebFormatCache>();
        services.AddSingleton<WebHarnessRuntime>();
        services.AddSingleton<IHarnessRuntime>(sp => sp.GetRequiredService<WebHarnessRuntime>());
        services.AddSingleton<IHarness, Aria.Harness.Core.Harness>();

        services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddInteractiveWebAssemblyComponents();

        services.AddSignalR(o =>
        {
            o.MaximumReceiveMessageSize = 10 * 1024 * 1024; // 10 MB — large recall/reflect responses
            // Allow the bridge to interleave fast control messages (metrics, chunks, completions)
            // instead of serializing everything behind a long-running handler.
            o.MaximumParallelInvocationsPerClient = 10;
        });
        services.AddSingleton<ModelBridgeRegistry>();
        services.AddSingleton<UiAccessKnockService>();
        services.AddSingleton<TrustedDeviceService>();
        services.AddSingleton<PendingEnrollmentService>();
        services.AddSingleton<BridgeCogitationClient>();
        services.AddSingleton<BridgeHiveClient>();
        services.AddSingleton<BridgeMemoryClient>();
        services.AddSingleton<BridgeMetricsClient>();
        services.AddSingleton<ProjectFilesClient>();
        services.AddSingleton<TerminalClient>();
        services.AddSingleton<TerminalPtyService>();

        // EF Core — SQLite, connection string configurable via config (e.g. Fly volume at /data)
        // When the data source is a relative path, resolve it against the content root so the DB
        // survives build-output churn (different RIDs, clean builds, etc.).
        var connectionString = configuration.GetConnectionString("Default")
            ?? "Data Source=aria.db";
        var cb = new SqliteConnectionStringBuilder(connectionString);
        if (!Path.IsPathRooted(cb.DataSource))
            cb.DataSource = Path.Combine(contentRootPath, cb.DataSource);
        services.AddDbContextFactory<AppDbContext>(opts =>
            opts.UseSqlite(cb.ConnectionString));

        // Application services
        services.AddSingleton<AgentService>();
        services.AddSingleton<BridgeSyncService>();
        services.AddSingleton<BridgeChannelClient>();
        services.AddSingleton<WargameService>();
        services.AddHostedService(sp => sp.GetRequiredService<WargameService>());
        services.AddScoped<UserService>();
        services.AddScoped<UserToolService>();
        services.AddSingleton<BridgeMcpClient>();
        services.AddSingleton<UserLocalSourceService>();
        services.AddScoped<CogitationService>();
        services.AddScoped<CogitationFolderService>();
        services.AddScoped<VoxService>();
        services.AddScoped<SubAgentService>();
        services.AddScoped<SkillService>();
        services.AddScoped<NodeService>();
        services.AddScoped<UserSessionState>();
        services.AddScoped<CircuitAuthService>();
        services.AddSingleton<CronSlotService>();
        services.AddSingleton<SealService>();
        services.AddSingleton<ContextApprovalService>();
        services.AddSingleton<GrantService>();
        services.AddSingleton<GrantReplicationService>();
        services.AddHostedService<GrantReplicationBackgroundService>();
        services.AddSingleton<AgentBackgroundExecutor>();
        services.AddSingleton<CronSchedulerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<CronSchedulerHostedService>());
        services.AddScoped<CollectiveService>();
        services.AddSingleton<CollectiveOrchestrator>();
        services.AddHostedService(sp => sp.GetRequiredService<CollectiveOrchestrator>());
        services.AddSingleton<ExchangeSessionService>();
        services.AddSingleton<CogitationRunRegistry>();

        return services;
    }

    // Partition rate limits by the real client IP (Fly-Client-IP first — same resolver the access
    // gate trusts), so one abusive client can't consume another's budget and can't dodge the limit
    // by varying a spoofable header.
    private static string ClientPartition(HttpContext ctx) =>
        ClientIpResolver.GetClientIp(ctx)?.ToString() ?? "unknown";
}
