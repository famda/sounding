using Sounding.Dashboard.Application.Captures;
using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.Containers;
using Sounding.Dashboard.Application.Diagnostics;
using Sounding.Dashboard.Application.Events;
using Sounding.Dashboard.Application.History;
using Sounding.Dashboard.Application.Network;
using Sounding.Dashboard.Application.Processes;
using Wolverine;

namespace Sounding.Dashboard.Api.Endpoints;

public sealed record CaptureRequest(int? Seconds);

public static class ContainerEndpoints
{
    public static RouteGroupBuilder MapContainerEndpoints(this RouteGroupBuilder api)
    {
        var containers = api.MapGroup("/containers").WithTags("Containers");

        containers.MapGet("/", static async (string? hostId, [ContainerStatuses] string? status, [ContainerSorts] string? sort, string? search, int? page, int? pageSize, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<PaginatedList<ContainerOverview>>>(new GetContainersQuery(hostId, status, sort, page, pageSize, search), cancellationToken)).ToHttpResult())
            .Returns<PaginatedList<ContainerOverview>>("ListContainers", "The containers the dashboard shows, with their latest use against their limits, sorted (default cpu), in pages of `pageSize` (default 50, up to 200). `hostId` keeps one host's; `search` keeps names containing it.");

        containers.MapGet("/{containerId}", static async (string containerId, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<ContainerDetails>>(new GetContainerQuery(containerId), cancellationToken)).ToHttpResult())
            .Returns<ContainerDetails>("GetContainer", "One container's state and latest sample. `containerId` is an id, a name or a part of a name, here and in every route below; a container no longer shown is still found.");

        containers.MapGet("/{containerId}/history", static async (string containerId, int? seconds, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<ContainerHistory>>(new GetContainerHistoryQuery(containerId, seconds), cancellationToken)).ToHttpResult())
            .Returns<ContainerHistory>("GetContainerHistory", "Chart series of the container's CPU, memory, disk, network and GPU over the last `seconds` (10 to 86400, default 60).");

        containers.MapGet("/{containerId}/history/detail", static async (string containerId, int? seconds, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<SeriesHistory>>(new GetContainerDetailHistoryQuery(containerId, seconds), cancellationToken)).ToHttpResult())
            .Returns<SeriesHistory>("GetContainerDetailHistory", "Chart series of every detail metric of the container, keyed by metric name, over the last `seconds` (default 60).");

        containers.MapGet("/{containerId}/history/summary", static async (string containerId, int? seconds, string? metrics, bool? includeSeries, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<ResourceSummary>>(new GetMetricsSummaryQuery(null, containerId, seconds, metrics, includeSeries ?? false), cancellationToken)).ToHttpResult())
            .Returns<ResourceSummary>("GetContainerHistorySummary", "Lowest, average, highest, last and trend of each metric over the last `seconds` (default 60). Without `metrics`: the basic ones, and the names of the others; with a comma-separated list: those; with `all`: every one.");

        containers.MapGet("/{containerId}/history/comparison", static async (string containerId, int? seconds, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<PeriodComparison>>(new ComparePeriodsQuery(null, containerId, seconds), cancellationToken)).ToHttpResult())
            .Returns<PeriodComparison>("CompareContainerPeriods", "The metrics that differ between the last `seconds` (default 900, up to 43200) and the same length of time before, largest change first.");

        containers.MapGet("/{containerId}/contention", static async (string containerId, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<HostContention>>(new GetContentionQuery(null, containerId), cancellationToken)).ToHttpResult())
            .WithTags("Diagnostics")
            .Returns<HostContention>("GetContainerContention", "Whether the container's host is short of CPU, memory or disk, and which containers on it use the most of each.");

        containers.MapGet("/{containerId}/checks/{check}", static async (string containerId, [CheckKinds] string check, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<CheckResult>>(new CheckQuery(containerId, check), cancellationToken)).ToHttpResult())
            .WithTags("Diagnostics")
            .Returns<CheckResult>("CheckContainer", "One yes-or-no check of the container (running, restarting, oomKilled, memoryNearLimit, memoryGrowing, cpuAtLimit, diskFilling, requestsFailing, requestsSlow, hostSaturated), with the figures behind the answer.");

        containers.MapGet("/{containerId}/dependencies/trace", static async (string containerId, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<FailureTrace>>(new TraceFailureQuery(containerId), cancellationToken)).ToHttpResult())
            .WithTags("Network")
            .Returns<FailureTrace>("TraceContainerDependencies", "What the container calls, hop by hop up to four calls deep, with each called service's state and own findings, the services it cannot reach, and who calls it.");

        containers.MapGet("/{containerId}/timeline", static async (string containerId, int? seconds, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<Timeline>>(new GetTimelineQuery(null, containerId, seconds, limit), cancellationToken)).ToHttpResult())
            .Returns<Timeline>("GetContainerTimeline", "The container's events and the moments its metrics left their usual range, in time order, over the last `seconds` (default 1800); up to `limit` entries (default 100, up to 300).");

        containers.MapGet("/{containerId}/diagnostics", static async (string containerId, int? eventSeconds, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<DiagnosisReport>>(new DiagnoseQuery(null, containerId, eventSeconds, limit), cancellationToken)).ToDiagnosisResult())
            .WithTags("Diagnostics")
            .Returns<DiagnosisResponse>("DiagnoseContainer", "The problems Sounding sees in the container, most severe first.");

        containers.MapGet("/{containerId}/events", static async (string containerId, int? seconds, [EventLevels] string? level, [EventKinds] string? kind, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<IReadOnlyList<HostEvent>>>(new GetEventsQuery(null, containerId, seconds, level, limit, kind), cancellationToken)).ToHttpResult())
            .WithTags("Events")
            .Returns<IReadOnlyList<HostEvent>>("ListContainerEvents", "The container's events, newest first, over the last `seconds` (default 3600); `level` is the minimum level; up to `limit` (default 100, up to 1000).");

        containers.MapGet("/{containerId}/dependencies", static async (string containerId, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<Dependencies>>(new GetDependenciesQuery(null, containerId, limit), cancellationToken)).ToHttpResult())
            .WithTags("Network")
            .Returns<Dependencies>("ListContainerDependencies", "Who calls the container and whom it calls, with the rate, errors and latency of decoded requests; failing edges first.");

        containers.MapGet("/{containerId}/network", static async (string containerId, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<ContainerNetwork>>(new GetContainerNetworkQuery(containerId), cancellationToken)).ToHttpResult())
            .WithTags("Network")
            .Returns<ContainerNetwork>("GetContainerNetwork", "Everything known about the container's network: each connection, listener, lookup and peer.");

        containers.MapGet("/{containerId}/network/overview", static async (string containerId, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<ContainerNetworkOverview>>(new GetContainerNetworkOverviewQuery(containerId, limit), cancellationToken)).ToHttpResult())
            .WithTags("Network")
            .Returns<ContainerNetworkOverview>("GetContainerNetworkOverview", "A bounded summary of the container's network: top peers, requests, connections with problems and failed lookups.");

        containers.MapGet("/{containerId}/connections", static async (string containerId, string? peer, int? port, string? process, bool? problemsOnly, [ConnectionSorts] string? sort, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<ContainerConnections>>(new GetConnectionsQuery(containerId, peer, port, process, problemsOnly ?? false, sort, limit), cancellationToken)).ToHttpResult())
            .WithTags("Network")
            .Returns<ContainerConnections>("ListContainerConnections", "The container's connections, filtered by `peer` (address or name), `port`, `process` or `problemsOnly`, sorted (default traffic); up to `limit` (default 50, up to 200).");

        containers.MapGet("/{containerId}/processes", static async (string containerId, DateTimeOffset? at, [LiveProcessSorts] string? sort, string? state, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<LiveProcesses>>(new GetLiveProcessesQuery(null, containerId, at, sort, state, limit), cancellationToken)).ToHttpResult())
            .WithTags("Processes")
            .Returns<LiveProcesses>("ListContainerProcesses", "The container's processes now, or as sampled closest to `at` (ISO 8601), sorted (default cpu); `state` keeps one state such as D or Z; up to `limit` (default 20, up to 200).");

        containers.MapGet("/{containerId}/processes/top", static async (string containerId, int? seconds, [WindowProcessSorts] string? sort, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<TopProcesses>>(new GetTopProcessesQuery(null, containerId, seconds, sort, limit), cancellationToken)).ToHttpResult())
            .WithTags("Processes")
            .Returns<TopProcesses>("ListContainerTopProcesses", "The container's top processes over the last `seconds` (default 60), with averages, peaks and memory growth; up to `limit` (default 20, up to 100).");

        containers.MapGet("/{containerId}/logs", static async (string containerId, [EventLevels] string? level, string? contains, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<ContainerLogs>>(new GetLogsQuery(containerId, level, contains, limit), cancellationToken)).ToHttpResult())
            .Returns<ContainerLogs>("GetContainerLogs", "The container's most recent output lines and its output rates; `level` is the minimum level; up to `limit` lines (default 100, up to 500).");

        containers.MapGet("/{containerId}/storage", static async (string containerId, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<ContainerStorage>>(new GetStorageQuery(containerId), cancellationToken)).ToHttpResult())
            .Returns<ContainerStorage>("GetContainerStorage", "Where the container's disk goes: growth and time until full, largest and growing paths, deleted files held open, mounts.");

        containers.MapGet("/{containerId}/captures", static async (string containerId, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<IReadOnlyList<CaptureInfo>>>(new GetCapturesQuery(containerId), cancellationToken)).ToHttpResult())
            .WithTags("Captures")
            .Returns<IReadOnlyList<CaptureInfo>>("ListContainerCaptures", "The container's last ten packet captures and their status.");

        containers.MapPost("/{containerId}/captures", static async (string containerId, CaptureRequest? request, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<CaptureRequested>>(new RequestCaptureCommand(containerId, request?.Seconds), cancellationToken))
                    .ToAcceptedResult(static capture => $"/api/v1/captures/{capture.Id}"))
            .WithTags("Captures")
            .WithName("StartContainerCapture")
            .WithSummary("Starts a packet capture. The body is JSON: `{}` for 10 seconds, or `{\"seconds\": n}` with 1 to 60. Answers 202 with the capture and, in Location, where to download it once ListContainerCaptures shows it done.")
            .Accepts<CaptureRequest>("application/json")
            .Produces<CaptureRequested>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return api;
    }
}
