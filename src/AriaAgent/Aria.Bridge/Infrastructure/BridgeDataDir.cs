namespace Aria.Bridge.Infrastructure;

/// <summary>
/// Tiny, logging-free helper for the bridge's per-instance app-data directory.
/// Extracted so both <see cref="BridgeDatabaseInitializer"/> and <see cref="BridgeLogger"/>
/// can resolve the data dir without creating a static-init cycle.
/// </summary>
public static class BridgeDataDir
{
    /// <summary>
    /// Environment override that makes every instance use its own vault, logs, and migration markers.
    /// </summary>
    public static string? Override => Environment.GetEnvironmentVariable("ARIA_BRIDGE_DATA_DIR");

    /// <summary>
    /// Default per-user app-data directory used when <see cref="Override"/> is not set.
    /// </summary>
    public static string Default =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "aria-bridge");

    /// <summary>
    /// Returns the effective data directory for this bridge process.
    /// </summary>
    public static string Resolve()
    {
        var dir = Override;
        if (!string.IsNullOrWhiteSpace(dir))
            return dir!;

        return Default;
    }
}
