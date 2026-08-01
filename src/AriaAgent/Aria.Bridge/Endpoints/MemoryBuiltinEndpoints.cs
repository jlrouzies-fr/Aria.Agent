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
            return Results.Ok(runtime.Status(
                config.BuiltinEnabled,
                config.BuiltinLicenseAcceptedAt,
                cfg.ResolveBuiltinExtractModelId(config)));
        });

        app.MapPut("/memory/builtin/config", async (
            HttpRequest req, SaveBuiltinConfigRequest dto, NoosphereConfigService cfg,
            NoosphereBuiltinRuntime runtime, CancellationToken ct) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(req))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                var before = await cfg.GetConfigAsync(ct);
                var beforeId = cfg.ResolveBuiltinExtractModelId(before);
                await cfg.SaveBuiltinConfigAsync(
                    new NoosphereConfigService.SaveBuiltinRequest(dto.Enabled, dto.AcceptLicense, dto.ExtractModelId), ct);
                var after = await cfg.GetConfigAsync(ct);
                var afterId = cfg.ResolveBuiltinExtractModelId(after);
                // Switching active extract while another variant is in RAM — drop it so the next
                // Inscribe loads the newly selected weights.
                if (!string.Equals(beforeId, afterId, StringComparison.OrdinalIgnoreCase)
                    && runtime.LoadedExtractModelId != null
                    && !string.Equals(runtime.LoadedExtractModelId, afterId, StringComparison.OrdinalIgnoreCase))
                    runtime.UnloadModel(NoosphereBuiltinCatalog.RoleExtract);
                return Results.Ok(new { ok = true });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/memory/builtin/download", async (
            HttpRequest req, string role, string? model, NoosphereConfigService cfg,
            NoosphereBuiltinRuntime runtime, CancellationToken ct) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(req))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (!NoosphereBuiltinCatalog.IsKnownRole(role) && !NoosphereBuiltinCatalog.IsKnownExtractId(role))
                return Results.BadRequest(new { error = $"Unknown role '{role}'" });

            var config = await cfg.GetConfigAsync(ct);
            var extractId = model ?? cfg.ResolveBuiltinExtractModelId(config);
            if (string.Equals(role, NoosphereBuiltinCatalog.RoleExtract, StringComparison.OrdinalIgnoreCase)
                && !NoosphereBuiltinCatalog.IsKnownExtractId(extractId))
                return Results.BadRequest(new { error = $"Unknown extract model '{extractId}'" });

            var err = runtime.StartDownload(role, config.BuiltinLicenseAcceptedAt != null, extractId);
            if (err != null) return Results.BadRequest(new { error = err });
            return Results.Ok(new { started = true });
        });

        app.MapDelete("/memory/builtin/model", (
            HttpRequest req, string role, string? model, NoosphereBuiltinRuntime runtime) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(req))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (!NoosphereBuiltinCatalog.IsKnownRole(role) && !NoosphereBuiltinCatalog.IsKnownExtractId(role))
                return Results.BadRequest(new { error = $"Unknown role '{role}'" });

            return Results.Ok(new { deleted = runtime.DeleteModel(role, model) });
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

            if (!NoosphereBuiltinCatalog.IsKnownRole(role) && !NoosphereBuiltinCatalog.IsKnownExtractId(role))
                return Results.BadRequest(new { error = $"Unknown role '{role}'" });

            return Results.Ok(new { unloaded = runtime.UnloadModel(role), role });
        });
    }
}

public record SaveBuiltinConfigRequest(bool Enabled, bool AcceptLicense = false, string? ExtractModelId = null);
