using System.Text.Json;
using Aria.Bridge.Services.Logging;

namespace Aria.Bridge.Infrastructure;

/// <summary>
/// Optional per-bridge fake identity and hardware, honored only in Development and only when the
/// launcher sets <c>ARIA_BRIDGE_DEBUG_PROFILE</c>. Null fields fall through to real probes.
/// </summary>
public sealed record DebugBridgeProfile(
    string? Label,
    string? Platform,
    string? Hostname,
    string? FormFactor,
    string? CpuModel,
    int? CpuCores,
    double? TotalRamMb,
    string? GpuName,
    double? GpuVramTotalMb,
    double? GpuVramFreeMb
);

/// <summary>
/// Loads <see cref="DebugBridgeProfile"/> from environment. Gated on
/// <c>ASPNETCORE_ENVIRONMENT=Development</c> so production bridges can never be spoofed.
/// </summary>
public static class DebugBridgeProfileLoader
{
    private static readonly Lazy<DebugBridgeProfile?> CurrentLazy = new(() =>
        TryParse(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                 Environment.GetEnvironmentVariable("ARIA_BRIDGE_DEBUG_PROFILE")));

    public static DebugBridgeProfile? Current => CurrentLazy.Value;

    /// <summary>
    /// Testable parser: returns null outside Development or when JSON is missing/malformed.
    /// Malformed JSON is logged at WARN level.
    /// </summary>
    internal static DebugBridgeProfile? TryParse(string? environmentName, string? profileJson)
    {
        if (!string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.IsNullOrWhiteSpace(profileJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<DebugBridgeProfile>(profileJson!.Trim(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex)
        {
            BridgeLogger.Log("WARN", $"ARIA_BRIDGE_DEBUG_PROFILE parse failed: {ex.Message}");
            return null;
        }
    }
}
