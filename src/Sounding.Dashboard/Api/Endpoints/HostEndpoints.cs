using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.Diagnostics;
using Sounding.Dashboard.Application.Events;
using Sounding.Dashboard.Application.History;
using Sounding.Dashboard.Application.Hosts;
using Sounding.Dashboard.Application.Network;
using Sounding.Dashboard.Application.Processes;
using Wolverine;

namespace Sounding.Dashboard.Api.Endpoints;

public static class HostEndpoints
{
    public static RouteGroupBuilder MapHostEndpoints(this RouteGroupBuilder api)
    {
        var hosts = api.MapGroup("/hosts").WithTags("Hosts");

        hosts.MapGet("/", static async ([HostStatuses] string? status, int? page, int? pageSize, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<PaginatedList<HostOverview>>>(new GetHostsQuery(status, page, pageSize), cancellationToken)).ToHttpResult())
            .Returns<PaginatedList<HostOverview>>("ListHosts", "The hosts the dashboard shows, with their latest figures, in pages of `pageSize` (default 50, up to 200).");

        hosts.MapGet("/{hostId}", static async (string hostId, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<HostDetails>>(new GetHostQuery(hostId), cancellationToken)).ToHttpResult())
            .Returns<HostDetails>("GetHost", "One host's latest sample and its containers. `hostId` is an id, a name or a part of a name, here and in every route below.");

        hosts.MapGet("/{hostId}/history", static async (string hostId, int? seconds, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<HostHistory>>(new GetHostHistoryQuery(hostId, seconds), cancellationToken)).ToHttpResult())
            .Returns<HostHistory>("GetHostHistory", "Chart series of the host's CPU, memory, disk, network and GPU over the last `seconds` (10 to 86400, default 60).");

        hosts.MapGet("/{hostId}/history/detail", static async (string hostId, int? seconds, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<SeriesHistory>>(new GetHostDetailHistoryQuery(hostId, seconds), cancellationToken)).ToHttpResult())
            .Returns<SeriesHistory>("GetHostDetailHistory", "Chart series of every detail metric of the host and its containers, keyed by metric name, over the last `seconds` (default 60).");

        hosts.MapGet("/{hostId}/history/summary", static async (string hostId, int? seconds, string? metrics, bool? includeSeries, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<ResourceSummary>>(new GetMetricsSummaryQuery(hostId, null, seconds, metrics, includeSeries ?? false), cancellationToken)).ToHttpResult())
            .Returns<ResourceSummary>("GetHostHistorySummary", "Lowest, average, highest, last and trend of each metric over the last `seconds` (default 60). Without `metrics`: the basic ones, and the names of the others; with a comma-separated list: those; with `all`: every one.");

        hosts.MapGet("/{hostId}/history/comparison", static async (string hostId, int? seconds, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<PeriodComparison>>(new ComparePeriodsQuery(hostId, null, seconds), cancellationToken)).ToHttpResult())
            .Returns<PeriodComparison>("CompareHostPeriods", "The metrics that differ between the last `seconds` (default 900, up to 43200) and the same length of time before, largest change first.");

        hosts.MapGet("/{hostId}/changes", static async (string hostId, int? seconds, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<Changes>>(new GetChangesQuery(hostId, seconds, limit), cancellationToken)).ToHttpResult())
            .WithTags("Diagnostics")
            .Returns<Changes>("ListHostChanges", "What changed on the host and its containers, in time order, over the last `seconds` (default 1800).");

        hosts.MapGet("/{hostId}/contention", static async (string hostId, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<HostContention>>(new GetContentionQuery(hostId, null), cancellationToken)).ToHttpResult())
            .WithTags("Diagnostics")
            .Returns<HostContention>("GetHostContention", "Whether the host is short of CPU, memory or disk, and which containers use the most of each.");

        hosts.MapGet("/{hostId}/timeline", static async (string hostId, int? seconds, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<Timeline>>(new GetTimelineQuery(hostId, null, seconds, limit), cancellationToken)).ToHttpResult())
            .Returns<Timeline>("GetHostTimeline", "The host's and its containers' events and the moments their metrics left their usual range, in time order, over the last `seconds` (default 1800); up to `limit` entries (default 100, up to 300).");

        hosts.MapGet("/{hostId}/diagnostics", static async (string hostId, int? eventSeconds, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<DiagnosisReport>>(new DiagnoseQuery(hostId, null, eventSeconds, limit), cancellationToken)).ToDiagnosisResult())
            .WithTags("Diagnostics")
            .Returns<DiagnosisResponse>("DiagnoseHost", "The problems Sounding sees on the host and its containers, most severe first.");

        hosts.MapGet("/{hostId}/events", static async (string hostId, int? seconds, [EventLevels] string? level, [EventKinds] string? kind, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<IReadOnlyList<HostEvent>>>(new GetEventsQuery(hostId, null, seconds, level, limit, kind), cancellationToken)).ToHttpResult())
            .WithTags("Events")
            .Returns<IReadOnlyList<HostEvent>>("ListHostEvents", "The host's events, newest first, over the last `seconds` (default 3600); `level` is the minimum level; up to `limit` (default 100, up to 1000).");

        hosts.MapGet("/{hostId}/dependencies", static async (string hostId, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<Dependencies>>(new GetDependenciesQuery(hostId, null, limit), cancellationToken)).ToHttpResult())
            .WithTags("Network")
            .Returns<Dependencies>("ListHostDependencies", "Who calls whom among the host's containers and the services they reach; failing edges first.");

        hosts.MapGet("/{hostId}/network", static async (string hostId, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<IReadOnlyList<ContainerNetwork>>>(new GetNetworkQuery(hostId), cancellationToken)).ToHttpResult())
            .WithTags("Network")
            .Returns<IReadOnlyList<ContainerNetwork>>("GetHostNetwork", "Everything known about the network of the host's running containers. Large.");

        hosts.MapGet("/{hostId}/network/overview", static async (string hostId, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<IReadOnlyList<ContainerNetworkOverview>>>(new GetNetworkOverviewQuery(hostId, null, limit), cancellationToken)).ToHttpResult())
            .WithTags("Network")
            .Returns<IReadOnlyList<ContainerNetworkOverview>>("GetHostNetworkOverview", "A bounded network summary of each of the host's running containers.");

        hosts.MapGet("/{hostId}/processes", static async (string hostId, DateTimeOffset? at, [LiveProcessSorts] string? sort, string? state, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<LiveProcesses>>(new GetLiveProcessesQuery(hostId, null, at, sort, state, limit), cancellationToken)).ToHttpResult())
            .WithTags("Processes")
            .Returns<LiveProcesses>("ListHostProcesses", "The host's processes now, or as sampled closest to `at` (ISO 8601), sorted (default cpu); `state` keeps one state such as D or Z; up to `limit` (default 20, up to 200).");

        hosts.MapGet("/{hostId}/processes/top", static async (string hostId, int? seconds, [WindowProcessSorts] string? sort, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<TopProcesses>>(new GetTopProcessesQuery(hostId, null, seconds, sort, limit), cancellationToken)).ToHttpResult())
            .WithTags("Processes")
            .Returns<TopProcesses>("ListHostTopProcesses", "The host's top processes over the last `seconds` (default 60), with averages, peaks and memory growth; up to `limit` (default 20, up to 100).");

        hosts.MapGet("/{hostId}/processes/by-container", static async (string hostId, DateTimeOffset? at, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<IReadOnlyList<ContainerProcesses>>>(new GetProcessesByContainerQuery(hostId, at), cancellationToken)).ToHttpResult())
            .WithTags("Processes")
            .Returns<IReadOnlyList<ContainerProcesses>>("ListHostProcessesByContainer", "Every process of the host's containers, grouped by container, now or as sampled closest to `at` (ISO 8601). Unbounded: what the dashboard draws.");

        hosts.MapGet("/{hostId}/processes/by-container/top", static async (string hostId, int? seconds, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<IReadOnlyList<ContainerProcessWindow>>>(new GetProcessWindowByContainerQuery(hostId, seconds), cancellationToken)).ToHttpResult())
            .WithTags("Processes")
            .Returns<IReadOnlyList<ContainerProcessWindow>>("ListHostTopProcessesByContainer", "Every process seen on the host over the last `seconds` (default 60), grouped by container, with averages and peaks. Unbounded.");

        return api;
    }
}
