namespace Aria.Bridge;

/// <summary>
/// Well-known bridge-owned filesystem locations.
/// </summary>
public static class BridgePaths
{
    /// <summary>
    /// Directory holding background-job logs (bash_exec background / run_background output spools
    /// and their .exit sidecars). Lives under the system temp dir — NOT under the project — so
    /// project trees stay clean of .aria-bg clutter. SecurityPolicy.EnforcePath exempts this
    /// directory so the model's follow-up read_file on a log path doesn't trip the allowed-paths
    /// gate. The dir is bridge-owned scratch space: nothing lands here but output the agent's own
    /// commands printed.
    /// </summary>
    public static readonly string BackgroundLogDir =
        Path.Combine(Path.GetTempPath(), "aria-bg");
}
