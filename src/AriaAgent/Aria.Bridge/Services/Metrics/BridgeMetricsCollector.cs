using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aria.Bridge.Services.Logging;
using Aria.Shared;

namespace Aria.Bridge.Services.Metrics;

/// <summary>
/// Collects live performance metrics for the local bridge process.
/// CPU and RAM are measured directly; macOS GPU/utilization is attempted via
/// system_profiler/ioreg/powermetrics and gracefully falls back if root or
/// hardware support is unavailable.
/// </summary>
public sealed class BridgeMetricsCollector
{
    private readonly Process _process;
    private readonly PowermetricsTelemetrySource? _powermetrics;
    private readonly object _lock = new();
    private DateTimeOffset _lastCpuAt;
    private TimeSpan _lastCpuTime;

    // Cache expensive macOS subprocess probes.
    private DateTimeOffset _macGpuCacheAt;
    private string? _macGpuName;
    private double? _macGpuUtil;
    private DateTimeOffset _macSysCacheAt;
    private double? _macSystemCpu;
    private (double UsedMb, double TotalMb)? _macSystemMemory;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private BridgeMetrics? _latest;

    public BridgeMetrics? GetLatest() => _latest;
    public void SetLatest(BridgeMetrics metrics) => _latest = metrics;

    public BridgeMetricsCollector(PowermetricsTelemetrySource? powermetrics = null)
    {
        _powermetrics = powermetrics;
        _process = Process.GetCurrentProcess();
        _lastCpuAt = DateTimeOffset.UtcNow;
        _lastCpuTime = _process.TotalProcessorTime;
    }

    public async Task<BridgeMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        string? error = null;
        double? cpuPercent = null;
        double? systemCpuPercent = null;
        string? gpuName = null;
        double? gpuUtilization = null;
        double? gpuPowerMw = null;
        double? memoryBandwidthGbps = null;
        string? bandwidthSource = null;

        try
        {
            cpuPercent = GetCpuPercent();
        }
        catch (Exception ex)
        {
            error = $"CPU: {ex.Message}";
        }

        _process.Refresh();
        var processMemoryMb = _process.WorkingSet64 / (1024.0 * 1024.0);
        var managedHeapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

        double? sysUsedMb = null;
        double? sysTotalMb = null;

        var platform = OperatingSystem.IsWindows() ? "Windows"
                     : OperatingSystem.IsMacOS() ? "macOS"
                     : OperatingSystem.IsLinux() ? "Linux" : "Unknown";

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                await RefreshMacSystemAsync(ct);
                systemCpuPercent = _macSystemCpu;
                if (_macSystemMemory is { } mem)
                {
                    sysUsedMb = mem.UsedMb;
                    sysTotalMb = mem.TotalMb;
                }
            }
            catch (Exception ex)
            {
                error = string.IsNullOrEmpty(error) ? $"SYS: {ex.Message}" : $"{error}; SYS: {ex.Message}";
            }

            try
            {
                // GPU utilization comes from ioreg (matches Stats / Activity Monitor).
                // GPU power comes from privileged powermetrics when sudo is granted.
                await RefreshMacGpuAsync(ct);
                gpuName = _macGpuName;
                gpuUtilization = _macGpuUtil;

                if (_powermetrics?.IsRunning == true)
                {
                    gpuPowerMw = _powermetrics.LatestGpuPowerMw;
                    bandwidthSource = "sudo powermetrics active; memory bandwidth not exposed";
                    if (gpuUtilization == null)
                        gpuUtilization = _powermetrics.LatestGpuUtilizationPercent;
                    if (!string.IsNullOrEmpty(_powermetrics.LastError))
                        error = string.IsNullOrEmpty(error) ? _powermetrics.LastError : $"{error}; {_powermetrics.LastError}";
                }
                else
                {
                    bandwidthSource = "sudo not granted — GPU power unavailable";
                }
            }
            catch (Exception ex)
            {
                error = string.IsNullOrEmpty(error) ? $"GPU: {ex.Message}" : $"{error}; GPU: {ex.Message}";
            }
        }
        else
        {
            bandwidthSource = "unavailable on this platform";
        }

        // Fall back to GC-reported totals if vm_stat failed.
        if (sysTotalMb == null)
        {
            var gcInfo = GC.GetGCMemoryInfo();
            sysUsedMb = gcInfo.MemoryLoadBytes > 0 ? gcInfo.MemoryLoadBytes / (1024.0 * 1024.0) : null;
            sysTotalMb = gcInfo.TotalAvailableMemoryBytes > 0 ? gcInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0) : null;
        }

        return new BridgeMetrics(
            Timestamp: DateTimeOffset.UtcNow,
            Uptime: DateTimeOffset.UtcNow - BridgeLogger.StartedAt,
            ProcessMemoryMb: processMemoryMb,
            ManagedHeapMb: managedHeapMb,
            SystemMemoryUsedMb: sysUsedMb,
            SystemMemoryTotalMb: sysTotalMb,
            CpuPercent: cpuPercent,
            SystemCpuPercent: systemCpuPercent,
            GpuName: gpuName,
            GpuUtilizationPercent: gpuUtilization,
            GpuPowerMw: gpuPowerMw,
            MemoryBandwidthGbps: memoryBandwidthGbps,
            Platform: platform,
            BandwidthSource: bandwidthSource,
            Error: error
        );
    }

    private double? GetCpuPercent()
    {
        var now = DateTimeOffset.UtcNow;
        _process.Refresh();
        var currentCpu = _process.TotalProcessorTime;

        lock (_lock)
        {
            var elapsed = (now - _lastCpuAt).TotalSeconds;
            if (elapsed <= 0) return null;

            var cpuDelta = (currentCpu - _lastCpuTime).TotalSeconds;
            var percent = (cpuDelta / elapsed / Environment.ProcessorCount) * 100.0;

            _lastCpuAt = now;
            _lastCpuTime = currentCpu;

            return Math.Clamp(percent, 0, 100);
        }
    }

    private async Task RefreshMacSystemAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _macSysCacheAt < CacheTtl)
            return;

        _macSysCacheAt = DateTimeOffset.UtcNow;

        _macSystemCpu = await GetMacSystemCpuAsync(ct);
        _macSystemMemory = await GetMacSystemMemoryAsync(ct);
    }

    private static async Task<double?> GetMacSystemCpuAsync(CancellationToken ct)
    {
        // top -l 1 -n 0 prints one sample without process list.
        var output = await RunCommandAsync("top", ["-l", "1", "-n", "0"], ct);
        if (string.IsNullOrWhiteSpace(output))
            return null;

        // "CPU usage: 7.41% user, 5.55% sys, 87.3% idle"
        foreach (var line in output.Split('\n'))
        {
            if (!line.StartsWith("CPU usage:", StringComparison.OrdinalIgnoreCase))
                continue;

            double? user = null, sys = null;
            var userMatch = Regex.Match(line, @"([0-9]+(?:\.[0-9]+)?)\s*%\s*user", RegexOptions.IgnoreCase);
            var sysMatch = Regex.Match(line, @"([0-9]+(?:\.[0-9]+)?)\s*%\s*sys", RegexOptions.IgnoreCase);

            if (userMatch.Success && double.TryParse(userMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var u))
                user = u;
            if (sysMatch.Success && double.TryParse(sysMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                sys = s;

            if (user.HasValue || sys.HasValue)
                return (user ?? 0) + (sys ?? 0);
        }
        return null;
    }

    private static async Task<(double UsedMb, double TotalMb)?> GetMacSystemMemoryAsync(CancellationToken ct)
    {
        var totalBytes = await GetMacTotalMemoryBytesAsync(ct);
        if (totalBytes == null || totalBytes.Value <= 0)
            return null;

        var freePages = await GetMacFreePagesAsync(ct);
        if (freePages == null)
            return null;

        const long PageSize = 16_384; // Apple Silicon page size; falls back if vm_stat reports bytes.
        var freeBytes = freePages.Value * PageSize;
        var usedBytes = totalBytes.Value - freeBytes;
        return (usedBytes / (1024.0 * 1024.0), totalBytes.Value / (1024.0 * 1024.0));
    }

    private static async Task<long?> GetMacTotalMemoryBytesAsync(CancellationToken ct)
    {
        var output = await RunCommandAsync("sysctl", ["hw.memsize"], ct);
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var match = Regex.Match(output, @"hw\.memsize:\s*(\d+)");
        return match.Success && long.TryParse(match.Groups[1].Value, out var v) ? v : null;
    }

    private static async Task<long?> GetMacFreePagesAsync(CancellationToken ct)
    {
        var output = await RunCommandAsync("vm_stat", [], ct);
        if (string.IsNullOrWhiteSpace(output))
            return null;

        long free = 0, inactive = 0, speculative = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var match = Regex.Match(line, @"^(Pages\s+\w+):\s*([0-9]+)");
            if (!match.Success) continue;

            var key = match.Groups[1].Value;
            if (!long.TryParse(match.Groups[2].Value, out var value))
                continue;

            if (key.Equals("Pages free", StringComparison.OrdinalIgnoreCase)) free = value;
            else if (key.Equals("Pages inactive", StringComparison.OrdinalIgnoreCase)) inactive = value;
            else if (key.Equals("Pages speculative", StringComparison.OrdinalIgnoreCase)) speculative = value;
        }

        return free + inactive + speculative;
    }

    private async Task RefreshMacGpuAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _macGpuCacheAt < CacheTtl)
            return;

        _macGpuCacheAt = DateTimeOffset.UtcNow;
        _macGpuName = await GetMacGpuNameAsync(ct);
        _macGpuUtil = await GetMacGpuUtilizationAsync(ct);
    }

    private static async Task<string?> GetMacGpuNameAsync(CancellationToken ct)
    {
        var output = await RunCommandAsync("system_profiler", ["SPDisplaysDataType", "-json"], ct);
        if (string.IsNullOrWhiteSpace(output))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("SPDisplaysDataType", out var displays) && displays.GetArrayLength() > 0)
            {
                var first = displays[0];
                if (first.TryGetProperty("_name", out var name))
                    return name.GetString();
                if (first.TryGetProperty("sppci_model", out var model))
                    return model.GetString();
            }
        }
        catch { }
        return null;
    }

    private static async Task<double?> GetMacGpuUtilizationAsync(CancellationToken ct)
    {
        var output = await RunCommandAsync("ioreg", ["-r", "-c", "IOAccelerator", "-a"], ct);
        if (string.IsNullOrWhiteSpace(output))
            return null;

        try
        {
            var doc = XDocument.Parse(output);
            var keys = doc.Descendants("key");

            // First try the same key Stats / Activity Monitor use.
            foreach (var key in keys)
            {
                if (key.Value.Equals("Device Utilization %", StringComparison.OrdinalIgnoreCase))
                {
                    var next = key.ElementsAfterSelf().FirstOrDefault();
                    if (next != null && double.TryParse(next.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        if (value > 1000) value /= 100.0;
                        return Math.Clamp(value, 0, 100);
                    }
                }
            }

            // Fallback to any utilization key.
            foreach (var key in keys)
            {
                var text = key.Value;
                if (text.Contains("Utilization", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("GPU %", StringComparison.OrdinalIgnoreCase))
                {
                    var next = key.ElementsAfterSelf().FirstOrDefault();
                    if (next != null && double.TryParse(next.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        if (value > 1000) value /= 100.0;
                        return Math.Clamp(value, 0, 100);
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static async Task<string?> RunCommandAsync(string fileName, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

        using (ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }))
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(3), ct);
            var completedTask = await Task.WhenAny(process.WaitForExitAsync(ct), timeoutTask);

            if (completedTask == timeoutTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            // Give redirected streams a moment to finish flushing.
            await Task.Delay(50, ct);
        }

        var combined = output.ToString();
        if (!string.IsNullOrWhiteSpace(error.ToString()))
            combined += Environment.NewLine + error.ToString();

        return string.IsNullOrWhiteSpace(combined) ? null : combined;
    }
}
