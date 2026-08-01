using Aria.Bridge.Infrastructure;
using Aria.Bridge.Services.Noosphere;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// Opt-in built-in Noosphere models (download / enable). Local-origin mutate only — deliberately
/// excluded from <see cref="Aria.Shared.TunnelAllowlist"/> so a compromised server cannot trigger
/// multi-hundred-MB downloads or flip the enable bit.
/// </summary>
public static class MemoryBuiltinEndpoints
{
    public static void MapMemoryBuiltinEndpoints(this WebApplication app)
    {
        app.MapGet("/memory/builtin/status", async (NoosphereConfigService cfg, NoosphereBuiltinRuntime runtime, CancellationToken ct) =>
        {
            var config = await cfg.GetConfigAsync(ct);
            return Results.Ok(runtime.Status(config.BuiltinEnabled, config.BuiltinLicenseAcceptedAt));
        });

        app.MapPut("/memory/builtin/config", async (HttpRequest req, SaveBuiltinConfigRequest dto, NoosphereConfigService cfg, CancellationToken ct) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(req))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            await cfg.SaveBuiltinConfigAsync(new NoosphereConfigService.SaveBuiltinRequest(dto.Enabled, dto.AcceptLicense), ct);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/memory/builtin/download", async (HttpRequest req, string role, NoosphereConfigService cfg, NoosphereBuiltinRuntime runtime, CancellationToken ct) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(req))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (NoosphereBuiltinCatalog.Lookup(role) is null)
                return Results.BadRequest(new { error = $"Unknown role '{role}'" });

            var config = await cfg.GetConfigAsync(ct);
            var err = runtime.StartDownload(role, config.BuiltinLicenseAcceptedAt != null);
            if (err != null) return Results.BadRequest(new { error = err });
            return Results.Ok(new { started = true });
        });

        app.MapDelete("/memory/builtin/model", (HttpRequest req, string role, NoosphereBuiltinRuntime runtime) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(req))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (NoosphereBuiltinCatalog.Lookup(role) is null)
                return Results.BadRequest(new { error = $"Unknown role '{role}'" });

            return Results.Ok(new { deleted = runtime.DeleteModel(role) });
        });

        // Free RAM only — files stay on disk. role omitted → unload both.
        app.MapPost("/memory/builtin/unload", (HttpRequest req, string? role, NoosphereBuiltinRuntime runtime) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(req))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (string.IsNullOrWhiteSpace(role))
            {
                runtime.UnloadAll();
                return Results.Ok(new { unloaded = true, role = (string?)null });
            }

            if (NoosphereBuiltinCatalog.Lookup(role) is null)
                return Results.BadRequest(new { error = $"Unknown role '{role}'" });

            return Results.Ok(new { unloaded = runtime.UnloadModel(role), role });
        });
    }
}

public record SaveBuiltinConfigRequest(bool Enabled, bool AcceptLicense = false);
