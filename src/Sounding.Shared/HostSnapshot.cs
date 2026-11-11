namespace Sounding.Shared;

public sealed record HostSnapshot(
    SystemStats System,
    CpuStats? Cpu,
    MemoryStats? Memory,
    GpuStats Gpu,
    DiskStats Disk,
    NetworkStats Network,
    ContainerTotals Containers,
    Pressure? Pressure = null,
    IReadOnlyList<TemperatureSensor>? Temperatures = null,
    FileHandles? Files = null);

public sealed record TemperatureSensor(string Source, string Label, double Celsius, double? HighCelsius, double? CriticalCelsius);

public sealed record FileHandles(long Open, long Max);

public sealed record ContainerTotals(
    int Count,
    double CpuPercentOfHost,
    double CpuCores,
    long MemoryBytes)
{
    public static readonly ContainerTotals Empty = new(0, 0, 0, 0);
}

public sealed record SystemStats(
    string Hostname,
    string? Kernel,
    double? UptimeSeconds,
    double[]? LoadAverage);

public sealed record CpuStats(
    string? Model,
    int LogicalCores,
    double UsagePercent,
    double[] CoreUsagePercent,
    CpuTimeSplit? Split = null,
    CpuActivity? Activity = null);

public sealed record CpuActivity(int Running, int Blocked, double? ContextSwitchesPerSecond, double? ProcessesStartedPerSecond);

public sealed record CpuTimeSplit(double User, double Nice, double System, double IoWait, double Irq, double SoftIrq, double Steal);

public sealed record MemoryStats(
    long TotalBytes,
    long UsedBytes,
    long AvailableBytes,
    long CachedBytes,
    long SwapTotalBytes,
    long SwapUsedBytes,
    double UsagePercent,
    double? MajorFaultsPerSecond = null,
    double? SwapInBytesPerSecond = null,
    double? SwapOutBytesPerSecond = null,
    long? DirtyBytes = null,
    long? WritebackBytes = null,
    long? CommittedBytes = null,
    long? CommitLimitBytes = null,
    long? SlabReclaimableBytes = null,
    long? SlabUnreclaimableBytes = null,
    double? ReclaimScannedBytesPerSecond = null,
    double? ReclaimedBytesPerSecond = null);

public sealed record GpuStats(
    bool Available,
    string? Error,
    IReadOnlyList<GpuDevice> Devices);

public sealed record GpuDevice(
    int Index,
    string Name,
    string? DriverVersion,
    double? UtilizationPercent,
    double? MemoryUtilizationPercent,
    long? MemoryTotalBytes,
    long? MemoryUsedBytes,
    double? MemoryUsagePercent,
    double? TemperatureCelsius,
    double? PowerDrawWatts,
    double? PowerLimitWatts,
    double? FanPercent,
    double? ClockSmMhz,
    double? ClockSmMaxMhz,
    string? PciBusId = null,
    string? PerformanceState = null,
    int? PcieGeneration = null,
    int? PcieWidth = null,
    IReadOnlyList<string>? ClockEventReasons = null,
    double? EncoderPercent = null,
    double? DecoderPercent = null,
    double? ClockMemoryMhz = null,
    double? ClockMemoryMaxMhz = null,
    double? TemperatureHeadroomCelsius = null,
    long? EccCorrected = null,
    long? EccUncorrected = null);

public sealed record DiskStats(
    IReadOnlyList<DiskVolume> Volumes,
    IReadOnlyList<DiskDevice> Devices,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond);

public sealed record DiskVolume(
    string Name,
    string Path,
    string? Format,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    double UsagePercent,
    string? Error,
    long? InodesTotal = null,
    long? InodesFree = null);

public sealed record DiskDevice(
    string Name,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    double BusyPercent,
    double ReadOpsPerSecond = 0,
    double WriteOpsPerSecond = 0,
    double? ReadLatencyMs = null,
    double? WriteLatencyMs = null,
    double? QueueDepth = null);

public sealed record NetworkStats(
    bool Available,
    string? Error,
    IReadOnlyList<NetworkInterfaceStats> Interfaces,
    double ReceiveBytesPerSecond,
    double TransmitBytesPerSecond,
    TcpStats? Tcp = null,
    UdpStats? Udp = null,
    Conntrack? Conntrack = null);

public sealed record NetworkInterfaceStats(
    string Name,
    double ReceiveBytesPerSecond,
    double TransmitBytesPerSecond,
    double ReceivePacketsPerSecond,
    double TransmitPacketsPerSecond,
    long ReceiveBytesTotal,
    long TransmitBytesTotal,
    long ErrorsTotal,
    long DropsTotal);
