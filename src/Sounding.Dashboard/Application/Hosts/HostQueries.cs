using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.Containers;

namespace Sounding.Dashboard.Application.Hosts;

public sealed record HostOverview(
    string HostId,
    string DisplayName,
    bool Online,
    DateTimeOffset LastSeen,
    DateTimeOffset? SampledAt,
    string? Kernel,
    double? UptimeSeconds,
    double[]? LoadAverage,
    double? CpuPercent,
    int? LogicalCores,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? MemoryPercent,
    IReadOnlyList<GpuSummary> Gpus,
    double? DiskReadBytesPerSecond,
    double? DiskWriteBytesPerSecond,
    double? NetReceiveBytesPerSecond,
    double? NetTransmitBytesPerSecond,
    IReadOnlyList<VolumeSummary> Volumes,
    int ContainersOnline,
    int ContainersTotal)
{
    public static HostOverview From(HostState host)
    {
        var summary = HostSummary.From(host);
        var online = 0;
        foreach (var container in host.Containers)
        {
            if (container.Status == ContainerStatus.Online) online++;
        }

        return new HostOverview(
            summary.HostId, summary.DisplayName, summary.Online, summary.LastSeen, summary.SampledAt, summary.Kernel,
            summary.UptimeSeconds, host.Latest?.Snapshot.System.LoadAverage, summary.CpuPercent, summary.LogicalCores,
            summary.MemoryUsedBytes, summary.MemoryTotalBytes, summary.MemoryPercent, summary.Gpus,
            summary.DiskReadBytesPerSecond, summary.DiskWriteBytesPerSecond, summary.NetReceiveBytesPerSecond,
            summary.NetTransmitBytesPerSecond, summary.Volumes, online, host.Containers.Count);
    }
}

public sealed record HostDetails(HostOverview Host, IReadOnlyList<ContainerOverview> Containers, HostSnapshot? Snapshot);

public static class HostStatus
{
    public const string Online = "online";
    public const string Offline = "offline";
    public static readonly string[] All = [Online, Offline];
}

public sealed class HostStatusesAttribute() : ChoicesAttribute(HostStatus.All);

public sealed record GetHostsQuery(string? Status = null, int? Page = null, int? PageSize = null);

public sealed record GetHostQuery(string HostId);

public static class GetHostsHandler
{
    public static Result<PaginatedList<HostOverview>> Handle(GetHostsQuery query, FleetSnapshot snapshot)
    {
        if (Validate.OneOf(query.Status, "status", HostStatus.All) is { } invalid) return invalid;
        if (Validate.Page(query.Page, query.PageSize, out var page) is { } badPage) return badPage;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;

        var hosts = new List<HostOverview>(fleet.Hosts.Count);
        foreach (var host in fleet.Hosts)
        {
            if (query.Status is null || host.Online == (query.Status == HostStatus.Online))
            {
                hosts.Add(HostOverview.From(host));
            }
        }

        return PaginatedList<HostOverview>.From(hosts, page);
    }
}

public static class GetHostHandler
{
    public static Result<HostDetails> Handle(GetHostQuery query, FleetSnapshot snapshot)
    {
        if (Validate.Id(query.HostId, "hostId") is { } invalid) return invalid;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        if (FleetSnapshot.ResolveHost(fleet, query.HostId, out var host) is { } failure) return failure;

        return new HostDetails(
            HostOverview.From(host),
            [.. host.Containers.Select(container => ContainerOverview.From(host, container))],
            host.Latest?.Snapshot);
    }
}
