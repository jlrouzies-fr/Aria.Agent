using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aria.Bridge.Services.Logging;
using Aria.Shared;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Aria.Bridge.Services.Metrics;

/// <summary>
/// Collects live performance metrics for the local bridge process.
/// CPU and RAM are measured directly; macOS GPU/utilization is attempted via
/// system_profiler/ioreg/powermetrics and gracefully falls back if root or
/// hardware support is unavailable. On Windows, system CPU comes from
/// GetSystemTimes and GPU name/utilization/power from nvidia-smi (with a
/// Win32_VideoController name-only fallback); anything unavailable stays null.
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

    // Cache the expensive Windows nvidia-smi probe.
    private DateTimeOffset _winGpuCacheAt;
    private string? _winGpuName;
    private double? _winGpuUtil;
    private double? _winGpuPowerMw;
    private double? _winGpuMemTotalMb;
    private double? _winGpuMemFreeMb;
    private bool _nvidiaSmiUnavailable;
    private bool _winGpuNameProbed;

    // Previous GetSystemTimes sample for Windows system CPU %.
    private bool _winSystemTimesValid;
    private ulong _winPrevIdleTicks;
    private ulong _winPrevKernelTicks;
    private ulong _winPrevUserTicks;

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
        double? gpuMemoryTotalMb = null;
        double? gpuMemoryFreeMb = null;
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
        else if (OperatingSystem.IsWindows())
        {
            try
            {
                // System CPU from GetSystemTimes deltas (no subprocess).
                systemCpuPercent = GetWindowsSystemCpuPercent();
            }
            catch (Exception ex)
            {
                error = string.IsNullOrEmpty(error) ? $"SYS: {ex.Message}" : $"{error}; SYS: {ex.Message}";
            }

            try
            {
                // GPU name/utilization/power/VRAM come from nvidia-smi; memory
                // bandwidth has no OS-level API on Windows and stays null.
                await RefreshWindowsGpuAsync(ct);
                gpuName = _winGpuName;
                gpuUtilization = _winGpuUtil;
                gpuPowerMw = _winGpuPowerMw;
                gpuMemoryTotalMb = _winGpuMemTotalMb;
                gpuMemoryFreeMb = _winGpuMemFreeMb;
                bandwidthSource = !_nvidiaSmiUnavailable
                    ? "nvidia-smi active; memory bandwidth not exposed"
                    : "unavailable on this platform";
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

        // Apple silicon: the GPU shares unified memory with the CPU, so VRAM total/free mirror
        // the system RAM figures. Discrete-GPU Macs, Windows and Linux report their own (or null).
        if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            gpuMemoryTotalMb = sysTotalMb;
            gpuMemoryFreeMb  = sysTotalMb is { } t && sysUsedMb is { } u ? t - u : null;
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
            GpuMemoryTotalMb: gpuMemoryTotalMb,
            GpuMemoryFreeMb: gpuMemoryFreeMb,
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

    private async Task RefreshWindowsGpuAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _winGpuCacheAt < CacheTtl)
            return;

        _winGpuCacheAt = DateTimeOffset.UtcNow;

        if (!_nvidiaSmiUnavailable)
        {
            var gpu = await GetWindowsNvidiaSmiAsync(ct);
            if (gpu is { } smi)
            {
                _winGpuName = smi.Name;
                _winGpuUtil = smi.UtilizationPercent;
                _winGpuPowerMw = smi.PowerMw;
                _winGpuMemTotalMb = smi.MemTotalMb;
                _winGpuMemFreeMb = smi.MemFreeMb;
                return;
            }

            // Don't spawn a failing process every tick.
            _nvidiaSmiUnavailable = true;
            _winGpuUtil = null;
            _winGpuPowerMw = null;
            _winGpuMemFreeMb = null;
        }

        // Name + total-VRAM fallback (no live free figure without nvidia-smi), probed once.
        if (!_winGpuNameProbed)
        {
            _winGpuNameProbed = true;
            var fallback = await GetWindowsGpuFallbackAsync(ct);
            _winGpuName = fallback.Name;
            _winGpuMemTotalMb = fallback.VramTotalMb;
        }
    }

    private static async Task<(string Name, double? UtilizationPercent, double? PowerMw, double? MemTotalMb, double? MemFreeMb)?> GetWindowsNvidiaSmiAsync(CancellationToken ct)
    {
        string? output;
        try
        {
            output = await RunCommandAsync("nvidia-smi",
                ["--query-gpu=name,utilization.gpu,power.draw,memory.total,memory.free", "--format=csv,noheader,nounits"], ct);
        }
        catch
        {
            return null; // nvidia-smi not on PATH.
        }

        var line = output?.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (line == null)
            return null;

        // e.g. "NVIDIA GeForce RTX 4090, 12, 85.43, 24564, 23100" (power.draw is watts, memory is MiB).
        var parts = line.Split(',');
        if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0]))
            return null;

        double? util = double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var u)
            ? Math.Clamp(u, 0, 100)
            : null;
        double? powerMw = double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            ? w * 1000.0
            : null;
        double? memTotalMb = parts.Length > 3 && double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mt)
            ? mt
            : null;
        double? memFreeMb = parts.Length > 4 && double.TryParse(parts[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mf)
            ? mf
            : null;

        return (parts[0].Trim(), util, powerMw, memTotalMb, memFreeMb);
    }

    private static async Task<(string? Name, double? VramTotalMb)> GetWindowsGpuFallbackAsync(CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("powershell",
                ["-NoProfile", "-Command", "$g = Get-CimInstance Win32_VideoController | Select-Object -First 1; \"$($g.Name)|$($g.AdapterRAM)\""], ct);
            var line = output?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line == null)
                return (null, null);

            var parts = line.Split('|');
            var name = parts[0].Length > 0 ? parts[0] : null;
            double? vramMb = parts.Length > 1 && long.TryParse(parts[1], out var ram) && ram > 0
                ? ram / (1024.0 * 1024.0)
                : null;
            return (name, vramMb);
        }
        catch
        {
            return (null, null);
        }
    }

    private double? GetWindowsSystemCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return null;

        var idleTicks = ToTicks(idle);
        var kernelTicks = ToTicks(kernel);
        var userTicks = ToTicks(user);

        lock (_lock)
        {
            if (!_winSystemTimesValid)
            {
                _winSystemTimesValid = true;
                (_winPrevIdleTicks, _winPrevKernelTicks, _winPrevUserTicks) = (idleTicks, kernelTicks, userTicks);
                return null; // Need two samples to compute a delta.
            }

            var idleDelta = idleTicks - _winPrevIdleTicks;
            var kernelDelta = kernelTicks - _winPrevKernelTicks;
            var userDelta = userTicks - _winPrevUserTicks;

            (_winPrevIdleTicks, _winPrevKernelTicks, _winPrevUserTicks) = (idleTicks, kernelTicks, userTicks);

            // Kernel time already includes idle time.
            var total = kernelDelta + userDelta;
            if (total == 0)
                return null;

            return Math.Clamp(100.0 * (1.0 - (double)idleDelta / total), 0, 100);
        }
    }

    private static ulong ToTicks(FILETIME ft) =>
        ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    internal static async Task<string?> RunCommandAsync(string fileName, string[] args, CancellationToken ct)
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
