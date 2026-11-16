namespace Sounding.Dashboard.Api;

public sealed record FleetHistory(
    int BucketSeconds,
    long[] T,
    IReadOnlyList<HostSeries> Hosts);

public sealed record HostSeries(
    string HostId,
    double?[] CpuCores,
    double?[] MemoryUsed,
    double?[] GpuUtilization,
    double?[] VramUsed,
    double?[] DiskRead,
    double?[] DiskWrite,
    double?[] NetReceive,
    double?[] NetTransmit,
    double?[] ContainersCpuCores);

public sealed record HostHistory(
    int BucketSeconds,
    long[] T,
    double?[] Cpu,
    double?[] MemoryUsed,
    double?[] DiskRead,
    double?[] DiskWrite,
    double?[] NetReceive,
    double?[] NetTransmit,
    double?[]?[] Cores,
    IReadOnlyList<GpuSeries> Gpus,
    IReadOnlyList<ContainerSeries> Containers);

public sealed record GpuSeries(int Index, double?[] Utilization, double?[] MemoryUsage);

public sealed record ContainerSeries(string ContainerId, double?[] CpuPercentOfHost, double?[] MemoryBytes);

public sealed record ContainerHistory(
    int BucketSeconds,
    long[] T,
    double[] CpuCores,
    double[] MemoryBytes,
    double[] IoRead,
    double[] IoWrite,
    double[] NetReceive,
    double[] NetTransmit,
    double?[] GpuPercent,
    double?[] GpuMemoryBytes);

public sealed record SeriesHistory(int BucketSeconds, long[] T, IReadOnlyDictionary<string, double?[]> Columns);

public sealed record ContainerProcesses(string ContainerId, IReadOnlyList<ProcessUsage> Processes);

public sealed record ContainerProcessWindow(string ContainerId, IReadOnlyList<ProcessWindow> Processes);

public sealed record ContainerNetwork(string ContainerId, NetworkActivity Network, string? HostId = null);

public sealed record CaptureInfo(long Id, DateTimeOffset RequestedAt, int Seconds, string Status, DateTimeOffset? FinishedAt, int? Packets, long? Bytes);

// A process over a window of its history samples.
public sealed record ProcessWindow(
    int Pid,
    string Name,
    string? CommandLine,
    string? User,
    DateTimeOffset? StartedAt,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    bool Running,
    double? AverageCores,
    double PeakCores,
    long PeakMemoryBytes,
    long MemoryBytes,
    double? AverageGpuPercent,
    long? PeakGpuMemoryBytes,
    long? ReadBytes,
    long? WriteBytes,
    int Threads,
    int? ParentPid = null,
    double? MemoryGrowthBytesPerHour = null);

// Something that happened, as opposed to a sample: a restart, an OOM kill, a new port or destination.
public sealed record HostEvent(DateTimeOffset Time, string? ContainerId, string Kind, string Level, string Message, string? HostId = null, string? ContainerName = null);

// Which events to read. Every predicate applies before Limit, so routine events elsewhere on a host cannot push out
// the ones asked for. Significant leaves out information events other than lifecycle ones; SkipRoutineProcesses
// leaves out only processes starting and ending; SevereFirst fills the limit with errors, then warnings, then the rest.
public sealed record EventFilter(
    IReadOnlyList<string> HostIds,
    TimeSpan Window,
    int Limit,
    string? ContainerId = null,
    IReadOnlyList<string>? Levels = null,
    IReadOnlyList<string>? Kinds = null,
    bool Significant = false,
    bool SkipRoutineProcesses = false,
    bool SevereFirst = false);

// Truncated: the window held more matching events than the limit, so the oldest (or, with SevereFirst, the mildest)
// were not read.
public sealed record EventPage(IReadOnlyList<HostEvent> Events, bool Truncated);
