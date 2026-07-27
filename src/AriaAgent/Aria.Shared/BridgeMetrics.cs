namespace Aria.Shared;

public record BridgeMetrics(
    DateTimeOffset Timestamp,
    TimeSpan Uptime,
    double ProcessMemoryMb,
    double ManagedHeapMb,
    double? SystemMemoryUsedMb,
    double? SystemMemoryTotalMb,
    double? CpuPercent,
    double? SystemCpuPercent,
    string? GpuName,
    double? GpuUtilizationPercent,
    double? GpuPowerMw,
    // VRAM. On Apple silicon this mirrors system RAM (unified memory — documented semantic);
    // on Windows it comes from nvidia-smi memory.total/memory.free (MiB), or the
    // Win32_VideoController.AdapterRAM fallback (total only) when nvidia-smi is absent.
    double? GpuMemoryTotalMb,
    double? GpuMemoryFreeMb,
    double? MemoryBandwidthGbps,
    string Platform,
    string? BandwidthSource,
    string? Error
);
