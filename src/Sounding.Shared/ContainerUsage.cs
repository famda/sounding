namespace Sounding.Shared;

public sealed record ContainerUsage(
    double CpuPercentOfHost,
    double CpuCores,
    double? CpuLimitCores,
    long MemoryBytes,
    long? MemoryLimitBytes,
    double IoReadBytesPerSecond,
    double IoWriteBytesPerSecond,
    double NetReceiveBytesPerSecond,
    double NetTransmitBytesPerSecond,
    double? GpuPercent = null,
    long? GpuMemoryBytes = null,
    long? DiskUsedBytes = null,
    ContainerDetail? Detail = null);

public sealed record ContainerDetail(
    Pressure? Pressure,
    CpuThrottling? Throttling,
    string? Cpuset,
    long? Tasks,
    long? TasksLimit,
    MemoryBreakdown? Memory,
    MemoryEvents? MemoryEvents,
    IReadOnlyList<IoDevice> IoDevices,
    IReadOnlyList<GpuShare> Gpus,
    long? GpuMemoryPeakBytes,
    IReadOnlyList<NetworkInterfaceStats>? Interfaces = null,
    TrafficSplit? Traffic = null,
    TcpStats? Tcp = null,
    IReadOnlyList<PathSize>? LargestPaths = null,
    long? FileCount = null,
    double? TasksStartedPerSecond = null,
    IReadOnlyList<GpuKernel>? GpuKernels = null,
    long? GpuAllocationFailures = null,
    IReadOnlyList<MountUsage>? Mounts = null,
    double? DiskGrowthBytesPerHour = null,
    long? DiskFreeBytes = null,
    long? OpenFiles = null,
    int? OpenFilesUncounted = null,
    UdpStats? Udp = null,
    Conntrack? Conntrack = null,
    DnsStats? Dns = null,
    IReadOnlyList<DeletedFile>? DeletedFiles = null,
    IReadOnlyList<PathSize>? GrowingPaths = null,
    IReadOnlyList<FileSize>? LargestFiles = null,
    LogStats? Logs = null,
    IReadOnlyList<LogLine>? RecentLogs = null);

public sealed record DeletedFile(int Pid, string Process, string Path, long Bytes);

public sealed record FileSize(string Path, long Bytes);

public sealed record LogStats(double LinesPerSecond, double ErrorsPerSecond, double WarningsPerSecond, double BytesPerSecond);

public sealed record LogLine(DateTimeOffset Time, string Stream, string Level, string Text);

public sealed record UdpStats(double ReceiveErrorsPerSecond, double BufferErrorsPerSecond, double NoPortPerSecond);

public sealed record Conntrack(long Entries, long? Max);

public sealed record DnsStats(double LookupsPerSecond, double FailuresPerSecond, double? AverageReplyMs);

public sealed record GpuKernel(string Name, double BusyPercent, double LaunchesPerSecond, double AverageMicroseconds);

public sealed record MountUsage(
    string MountPoint,
    string Source,
    string FileSystem,
    bool ReadOnly,
    long? TotalBytes,
    long? FreeBytes,
    long? InodesTotal,
    long? InodesFree,
    long? UsedBytes);

public sealed record TcpStats(
    double RetransmitsPerSecond,
    double ResetsSentPerSecond,
    double FailedConnectsPerSecond,
    double ListenDropsPerSecond,
    double ActiveOpensPerSecond,
    double PassiveOpensPerSecond,
    long Established);

public sealed record PathSize(string Path, long Bytes, long Files);

public sealed record Pressure(
    double CpuSome,
    double? CpuFull,
    double MemorySome,
    double MemoryFull,
    double IoSome,
    double IoFull);

public sealed record CpuThrottling(
    double ThrottledPercent,
    double ThrottledMillisecondsPerSecond,
    long ThrottledPeriodsTotal);

public sealed record MemoryBreakdown(
    long Anon,
    long File,
    long Kernel,
    long Shmem,
    long Sock,
    long? PeakBytes,
    double? MajorFaultsPerSecond = null,
    double? RefaultsPerSecond = null,
    long? SwapBytes = null,
    long? CurrentBytes = null,
    long? InactiveFileBytes = null,
    long? DirtyBytes = null,
    long? WritebackBytes = null,
    double? ReclaimScannedBytesPerSecond = null,
    double? ReclaimedBytesPerSecond = null);

public sealed record MemoryEvents(long High, long Max, long Oom, long OomKill);

public sealed record IoDevice(
    string Device,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    double ReadOpsPerSecond,
    double WriteOpsPerSecond);

public sealed record GpuShare(
    string PciBusId,
    double Percent,
    long MemoryBytes,
    double HostToDeviceBytesPerSecond,
    double DeviceToHostBytesPerSecond,
    double? LaunchesPerSecond = null,
    double? AverageKernelMicroseconds = null);
