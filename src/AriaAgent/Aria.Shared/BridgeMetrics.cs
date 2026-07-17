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
    double? MemoryBandwidthGbps,
    string Platform,
    string? BandwidthSource,
    string? Error
);
