using Aria.Bridge.Data;
using Aria.Bridge.Services.Auth;
using Aria.Bridge.Services.Diagnostics;
using Aria.Bridge.Services.Logging;
using Aria.Bridge.Services.Metrics;
using Aria.Bridge.Services.Noosphere;
using Aria.Bridge.Services.Security;
using Aria.Bridge.Services.Speech;
using Aria.Bridge.Services.Trust;
using Aria.Bridge.Services.Vault;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Infrastructure;

public static class BridgeServiceRegistration
{
    public static WebApplicationBuilder AddBridgeServices(this WebApplicationBuilder builder)
    {
        // Loopback-only by default — not exposed to the network. Use localhost so OAuth redirect URI
        // validation matches the hostname users register in their app credentials.
        // In container/headless deployments set ASPNETCORE_URLS (e.g. http://+:5741) to accept
        // external traffic; direct builds keep the loopback default.
        if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
        {
            builder.WebHost.UseUrls("http://localhost:5741");
        }

        // CORS must be open: the WASM bridge calls this from the browser (origin = Aria.Web host).
        builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

        // Single SessionStore holds all live stdio MCP child processes for the lifetime of the bridge.
        builder.Services.AddSingleton<SessionStore>();

        // Persistent pseudo-terminal sessions for the shared terminal panel's PTY mode.
        builder.Services.AddSingleton<PtySessionStore>();

        // On-device speech-to-text (whisper.cpp). Models + audio stay on this machine.
        builder.Services.AddSingleton<LocalWhisperService>();

        // Live performance metrics for the bridge process (RAM, CPU, GPU best-effort).
        builder.Services.AddSingleton<BridgeMetricsCollector>();
        builder.Services.AddHostedService<BridgeMetricsHostedService>();

        // Static hardware inventory (CPU model/cores, total RAM, GPU, form factor) for the fleet view.
        builder.Services.AddSingleton<HardwareInventory>();

        // Privileged macOS telemetry source (sudo powermetrics) — started on demand.
        builder.Services.AddSingleton<PowermetricsTelemetrySource>();

        // Direct tunnel: outbound SignalR connection to the linked Aria.Web server.
        builder.Services.AddSingleton<Action<string, string>>(BridgeLogger.Log);
        builder.Services.AddSingleton<SiblingRoster>();
        builder.Services.AddSingleton<DirectTunnel>();
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DirectTunnel>());

        // Local SQLite vault — cogitations and soul identity stored on the user's machine.
        builder.Services.AddDbContext<BridgeDbContext>(opts =>
            opts.UseSqlite($"Data Source={BridgeDatabaseInitializer.DbPath}"));

        // OAuth app credentials live locally on the bridge; the server never sees them.
        builder.Services.AddSingleton(BridgeOAuthConfig.FromConfiguration(builder.Configuration));

        // F-8: node-side security audit trail for sensitive capability invocations.
        builder.Services.AddSingleton<SecurityAuditLog>();

        // F-7: encrypt sensitive values in the local SQLite vault at rest via OS keychain/DPAPI.
        builder.Services.Configure<VaultEncryptionOptions>(_ => { });
        builder.Services.AddSingleton<VaultEncryption>();

        // Noosphere: native agent memory (Engrams + entity graph). Channel selection lives in the
        // local SQLite vault via NoosphereConfigService; appsettings is no longer the primary UI.
        builder.Services.Configure<NoosphereOptions>(builder.Configuration.GetSection(NoosphereOptions.SectionName));
        builder.Services.AddSingleton<NoosphereConfigService>();
        builder.Services.AddSingleton<NoosphereBuiltinRuntime>();
        builder.Services.AddSingleton<NoosphereEmbedder>();
        builder.Services.AddSingleton<NoosphereExtractor>();
        builder.Services.AddSingleton<NoosphereService>();
        builder.Services.AddHostedService<NoosphereIngestWorker>();

        return builder;
    }
}
