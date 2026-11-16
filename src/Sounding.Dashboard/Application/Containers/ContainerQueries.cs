using Sounding.Dashboard.Application.Common;

namespace Sounding.Dashboard.Application.Containers;

public sealed record ContainerOverview(
    string ContainerId,
    string? Name,
    string HostId,
    string HostName,
    string Status,
    bool IsLeader,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeen,
    DateTimeOffset? SampledAt,
    int Restarts,
    int? ExitCode,
    int? ExitSignal,
    double? CpuCores,
    double? CpuLimitCores,
    double? CpuPercentOfHost,
    long? MemoryBytes,
    long? MemoryLimitBytes,
    double? MemoryPercentOfLimit,
    double? GpuPercent,
    long? GpuMemoryBytes,
    double? IoReadBytesPerSecond,
    double? IoWriteBytesPerSecond,
    double? NetReceiveBytesPerSecond,
    double? NetTransmitBytesPerSecond,
    long? DiskUsedBytes)
{
    public static ContainerOverview From(HostState host, ContainerState container)
    {
        var usage = container.Usage;
        return new ContainerOverview(
            container.ContainerId, container.Name, host.HostId, host.DisplayName, container.Status, container.IsLeader,
            container.StartedAt, container.LastSeen, container.SampledAt, container.Restarts, container.ExitCode, container.ExitSignal,
            usage?.CpuCores, usage?.CpuLimitCores, usage?.CpuPercentOfHost, usage?.MemoryBytes, usage?.MemoryLimitBytes,
            usage is { MemoryLimitBytes: > 0 and var limit } ? Math.Round(usage.MemoryBytes * 100d / limit, 1) : null,
            usage?.GpuPercent, usage?.GpuMemoryBytes, usage?.IoReadBytesPerSecond, usage?.IoWriteBytesPerSecond,
            usage?.NetReceiveBytesPerSecond, usage?.NetTransmitBytesPerSecond, usage?.DiskUsedBytes);
    }
}

public sealed record ContainerDetails(string HostId, string HostName, ContainerState Container, string? Note = null);

public static class ContainerSort
{
    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string Gpu = "gpu";
    public const string Io = "io";
    public const string Network = "network";
    public const string Name = "name";
    public static readonly string[] All = [Cpu, Memory, Gpu, Io, Network, Name];
}

public sealed class ContainerSortsAttribute() : ChoicesAttribute(ContainerSort.All);

public sealed class ContainerStatusesAttribute() : ChoicesAttribute(ContainerStatus.Online, ContainerStatus.Offline, ContainerStatus.Stopped);

public sealed record GetContainersQuery(
    string? HostId = null,
    string? Status = null,
    string? Sort = null,
    int? Page = null,
    int? PageSize = null,
    string? Search = null);

public sealed record GetContainerQuery(string ContainerId);

public static class GetContainersHandler
{
    private static readonly string[] Statuses = [ContainerStatus.Online, ContainerStatus.Offline, ContainerStatus.Stopped];

    public static Result<PaginatedList<ContainerOverview>> Handle(GetContainersQuery query, FleetSnapshot snapshot)
    {
        if (Validate.OptionalId(query.HostId, "hostId") is { } badHost) return badHost;
        if (Validate.OneOf(query.Status, "status", Statuses) is { } badStatus) return badStatus;
        if (Validate.OneOf(query.Sort, "sort", ContainerSort.All) is { } badSort) return badSort;
        if (Validate.Page(query.Page, query.PageSize, out var page) is { } badPage) return badPage;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        if (FleetSnapshot.ResolveScope(fleet, query.HostId, null, required: false, out var scope) is { } failure) return failure;

        var containers = new List<ContainerOverview>();
        foreach (var host in fleet.Hosts)
        {
            if (!scope.IncludesHost(host.HostId)) continue;
            foreach (var container in host.Containers)
            {
                if ((query.Status is null || container.Status == query.Status)
                    && (query.Search is not { } search
                        || container.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
                        || container.ContainerId.StartsWith(search, StringComparison.Ordinal)))
                {
                    containers.Add(ContainerOverview.From(host, container));
                }
            }
        }

        containers.Sort(Comparer(query.Sort ?? ContainerSort.Cpu));
        return PaginatedList<ContainerOverview>.From(containers, page);
    }

    private static Comparison<ContainerOverview> Comparer(string sort) => sort switch
    {
        ContainerSort.Name => static (a, b) => string.Compare(a.Name ?? a.ContainerId, b.Name ?? b.ContainerId, StringComparison.Ordinal),
        ContainerSort.Memory => static (a, b) => Nullable.Compare(b.MemoryBytes, a.MemoryBytes),
        ContainerSort.Gpu => static (a, b) => Nullable.Compare(b.GpuPercent, a.GpuPercent),
        ContainerSort.Io => static (a, b) => Nullable.Compare(b.IoReadBytesPerSecond + b.IoWriteBytesPerSecond, a.IoReadBytesPerSecond + a.IoWriteBytesPerSecond),
        ContainerSort.Network => static (a, b) => Nullable.Compare(b.NetReceiveBytesPerSecond + b.NetTransmitBytesPerSecond, a.NetReceiveBytesPerSecond + a.NetTransmitBytesPerSecond),
        _ => static (a, b) => Nullable.Compare(b.CpuCores, a.CpuCores),
    };
}

public static class GetContainerHandler
{
    public static async Task<Result<ContainerDetails>> Handle(GetContainerQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (Validate.Id(query.ContainerId, "containerId") is { } invalid) return invalid;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;

        // Stopped or silent containers leave the fleet view after a few minutes but keep their rows: a container that
        // stopped is not one that never existed.
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, null, query.ContainerId, required: true, cancellationToken);
        if (failure is not null) return failure;

        var (host, container) = (scope.Host!, scope.Container!);
        if (container.Usage is not null || host.Containers.Contains(container)) return new ContainerDetails(host.HostId, host.DisplayName, container);

        var ended = container.Status == ContainerStatus.Stopped
            ? $"stopped at {container.LastSeen:u}" + (container.ExitSignal is { } signal ? $" by signal {signal}" : container.ExitCode is { } code ? $" with exit code {code}" : "")
            : $"has not reported since {container.LastSeen:u}";
        return new ContainerDetails(host.HostId, host.DisplayName, container,
            $"Not shown on the dashboard any more: it {ended}. No current sample; its events, timeline and history remain readable.");
    }
}
