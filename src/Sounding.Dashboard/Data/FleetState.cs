namespace Sounding.Dashboard.Data;

public sealed record FleetState(
    DateTimeOffset Timestamp,
    IReadOnlyList<HostState> Hosts);

public sealed record HostState(
    string HostId,
    string DisplayName,
    bool Online,
    DateTimeOffset LastSeen,
    string? LeaderContainerId,
    HostSample? Latest,
    IReadOnlyList<ContainerState> Containers);

public sealed record HostSample(
    DateTimeOffset Timestamp,
    string ReportedBy,
    HostSnapshot Snapshot);

public static class ContainerStatus
{
    public const string Online = "online";
    public const string Offline = "offline";
    public const string Stopped = "stopped";
}

public sealed record ContainerState(
    string ContainerId,
    string Status,
    bool IsLeader,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeen,
    DateTimeOffset? SampledAt,
    ContainerUsage? Usage,
    string? Name = null,
    int Restarts = 0,
    int? ExitCode = null,
    int? ExitSignal = null,
    double? StopSeconds = null);
