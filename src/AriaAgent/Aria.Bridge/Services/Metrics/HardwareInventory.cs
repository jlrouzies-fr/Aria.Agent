using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Aria.Bridge.Infrastructure;

namespace Aria.Bridge.Services.Metrics;

/// <summary>
/// Static hardware inventory for the fleet view: what this machine IS, as opposed to /metrics'
/// live load. Computed lazily on first request and cached for the process lifetime — hardware
/// doesn't change mid-run. GPU name/VRAM piggyback on the metrics collector's probes so no
/// duplicate subprocesses are spawned. Every field is best-effort and stays null when its
/// probe is unavailable.
/// </summary>
public sealed class HardwareInventory(BridgeMetricsCollector metrics)
{
    public record Snapshot(
        string Hostname,
        string Os,
        string Arch,
        string? CpuModel,
        int CpuCores,
        double? TotalRamMb,
        string? GpuName,
        double? GpuVramTotalMb,
        string FormFactor);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Snapshot? _cached;

    public async Task<Snapshot> GetAsync(CancellationToken ct = default)
    {
        if (_cached is null)
        {
            await _lock.WaitAsync(ct);
            try { _cached ??= await BuildAsync(ct); }
            finally { _lock.Release(); }
        }
        var s = _cached;

        // GPU identity piggybacks on the metrics collector's probes, and the earliest metrics
        // snapshots can be partial (probe still in flight) — so a null GPU field is refilled from
        // the LATEST metrics at request time rather than cached as null for the process lifetime.
        if (s.GpuName == null || s.GpuVramTotalMb == null)
        {
            var m = metrics.GetLatest();
            if (m != null)
                s = s with { GpuName = s.GpuName ?? m.GpuName, GpuVramTotalMb = s.GpuVramTotalMb ?? m.GpuMemoryTotalMb };
        }
        return ApplyOverrides(s, DebugBridgeProfileLoader.Current);
    }

    private async Task<Snapshot> BuildAsync(CancellationToken ct)
    {
        var m = metrics.GetLatest() ?? await metrics.GetMetricsAsync(ct);

        return new Snapshot(
            Hostname: Environment.MachineName,
            Os: RuntimeInformation.OSDescription,
            Arch: RuntimeInformation.OSArchitecture.ToString(),
            CpuModel: await GetCpuModelAsync(ct),
            CpuCores: Environment.ProcessorCount,
            TotalRamMb: await GetTotalRamMbAsync(ct),
            GpuName: m.GpuName,
            GpuVramTotalMb: m.GpuMemoryTotalMb,
            FormFactor: await GetFormFactorAsync(ct));
    }

    /// <summary>
    /// Applies the debug profile to a hardware snapshot, replacing only the fields the profile
    /// explicitly provides. Pure function — safe to unit test.
    /// </summary>
    internal static Snapshot ApplyOverrides(Snapshot snapshot, DebugBridgeProfile? profile)
    {
        if (profile is null) return snapshot;

        // "none" fakes a GPU-less machine: suppress both name and VRAM instead of letting the
        // real probe's values leak through (a null profile field means "keep the real value").
        var noGpu = DebugBridgeProfileLoader.IsNoGpu(profile);

        return snapshot with
        {
            Hostname    = profile.Hostname    ?? snapshot.Hostname,
            Os          = profile.Platform    ?? snapshot.Os,
            CpuModel    = profile.CpuModel    ?? snapshot.CpuModel,
            CpuCores    = profile.CpuCores    ?? snapshot.CpuCores,
            TotalRamMb  = profile.TotalRamMb  ?? snapshot.TotalRamMb,
            GpuName     = noGpu ? null : profile.GpuName     ?? snapshot.GpuName,
            GpuVramTotalMb = noGpu ? null : profile.GpuVramTotalMb ?? snapshot.GpuVramTotalMb,
            FormFactor  = profile.FormFactor  ?? snapshot.FormFactor,
        };
    }

    private static async Task<string?> GetCpuModelAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
        {
            var output = await BridgeMetricsCollector.RunCommandAsync("sysctl", ["machdep.cpu.brand_string"], ct);
            var match = output is null ? null : Regex.Match(output, @"brand_string:\s*(.+)");
            return match is { Success: true } ? match.Groups[1].Value.Trim() : null;
        }
        if (OperatingSystem.IsWindows())
        {
            var output = await BridgeMetricsCollector.RunCommandAsync("powershell",
                ["-NoProfile", "-Command", "(Get-CimInstance Win32_Processor | Select-Object -First 1).Name"], ct);
            return output?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        }
        // Linux: first "model name" line of /proc/cpuinfo.
        try
        {
            foreach (var line in await File.ReadAllLinesAsync("/proc/cpuinfo", ct))
                if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    return line.Split(':', 2)[1].Trim();
        }
        catch { }
        return null;
    }

    private static async Task<double?> GetTotalRamMbAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
        {
            var output = await BridgeMetricsCollector.RunCommandAsync("sysctl", ["hw.memsize"], ct);
            var match = output is null ? null : Regex.Match(output, @"hw\.memsize:\s*(\d+)");
            if (match is { Success: true } && long.TryParse(match.Groups[1].Value, out var v))
                return v / (1024.0 * 1024.0);
        }
        else if (OperatingSystem.IsWindows())
        {
            var output = await BridgeMetricsCollector.RunCommandAsync("powershell",
                ["-NoProfile", "-Command", "(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory"], ct);
            var line = output?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line != null && long.TryParse(line, out var v) && v > 0)
                return v / (1024.0 * 1024.0);
        }
        else
        {
            try
            {
                foreach (var line in await File.ReadAllLinesAsync("/proc/meminfo", ct))
                    if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase) &&
                        long.TryParse(Regex.Match(line, @"\d+").Value, out var kb))
                        return kb / 1024.0;
            }
            catch { }
        }

        var gc = GC.GetGCMemoryInfo();
        return gc.TotalAvailableMemoryBytes > 0 ? gc.TotalAvailableMemoryBytes / (1024.0 * 1024.0) : null;
    }

    // Best-effort laptop/desktop guess — drives the fleet map icon. "unknown" when no probe answers.
    private static async Task<string> GetFormFactorAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
        {
            // hw.model is the model IDENTIFIER: older Macs embed the family ("MacBookPro18,3"),
            // but recent generations are bare ("Mac17,9") with no family substring at all — so
            // fall back to system_profiler's marketing name ("Model Name: MacBook Pro"), which
            // always carries the family, before deciding.
            var output = await BridgeMetricsCollector.RunCommandAsync("sysctl", ["hw.model"], ct);
            var match = output is null ? null : Regex.Match(output, @"hw\.model:\s*(\S+)");
            if (match is { Success: true }
                && match.Groups[1].Value.Contains("MacBook", StringComparison.OrdinalIgnoreCase))
                return "laptop";

            var profiler = await BridgeMetricsCollector.RunCommandAsync("system_profiler", ["SPHardwareDataType"], ct);
            var name = profiler is null ? null : Regex.Match(profiler, @"Model Name:\s*(.+)");
            if (name is { Success: true })
                return name.Groups[1].Value.Contains("MacBook", StringComparison.OrdinalIgnoreCase)
                    ? "laptop" : "desktop";

            return match is { Success: true } ? "desktop" : "unknown";
        }
        if (OperatingSystem.IsWindows())
        {
            // Win32_SystemEnclosure chassis types: 8-14, 18, 21, 30-32 are portable form factors.
            var output = await BridgeMetricsCollector.RunCommandAsync("powershell",
                ["-NoProfile", "-Command", "(Get-CimInstance Win32_SystemEnclosure | Select-Object -First 1).ChassisTypes[0]"], ct);
            var line = output?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line == null || !int.TryParse(line, out var chassis)) return "unknown";
            int[] portable = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];
            return portable.Contains(chassis) ? "laptop" : "desktop";
        }
        try
        {
            return Directory.EnumerateDirectories("/sys/class/power_supply").Any(d =>
                Path.GetFileName(d).StartsWith("BAT", StringComparison.OrdinalIgnoreCase))
                ? "laptop" : "desktop";
        }
        catch { return "unknown"; }
    }
}
