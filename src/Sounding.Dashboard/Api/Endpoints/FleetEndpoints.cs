using Sounding.Dashboard.Application.Captures;
using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.Diagnostics;
using Sounding.Dashboard.Application.Events;
using Sounding.Dashboard.Application.History;
using Sounding.Dashboard.Application.Network;
using Wolverine;

namespace Sounding.Dashboard.Api.Endpoints;

// Everything Sounding monitors. The same resources exist under /hosts/{hostId} and /containers/{containerId} for one
// host or container: the scope is always in the path.
public static class FleetEndpoints
{
    public static RouteGroupBuilder MapFleetEndpoints(this RouteGroupBuilder api)
    {
        var fleet = api.MapGroup("/fleet").WithTags("Fleet");

        // A live subscription rather than a query: it streams the snapshot FleetMonitor publishes.
        fleet.MapGet("/stream", static (string? hosts, string? containers, Broadcaster<FleetView> broadcaster, CancellationToken cancellationToken) =>
                TypedResults.ServerSentEvents(ForHosts(broadcaster.Subscribe(cancellationToken), Ids(hosts), Ids(containers)), eventType: "fleet"))
            .WithName("StreamFleet")
            .WithSummary("Streams the state of everything about once a second, as Server-Sent Events; the hosts and containers listed (ids, comma-separated, up to 32) carry full detail.");

        fleet.MapGet("/history", static async (int? seconds, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<FleetHistory>>(new GetFleetHistoryQuery(seconds), cancellationToken)).ToHttpResult())
            .Returns<FleetHistory>("GetFleetHistory", "Chart series of every host over the last `seconds` (10 to 86400, default 60).");

        api.MapGet("/diagnostics", static async (int? eventSeconds, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<DiagnosisReport>>(new DiagnoseQuery(null, null, eventSeconds, limit), cancellationToken)).ToDiagnosisResult())
            .WithTags("Diagnostics")
            .Returns<DiagnosisResponse>("DiagnoseFleet", "The problems Sounding sees anywhere, most severe first, each with links to the requests that show more; events are checked over the last `eventSeconds` (default 900), and up to `limit` findings (default 30, up to 500) are returned.");

        api.MapGet("/changes", static async (int? seconds, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<Changes>>(new GetChangesQuery(null, seconds, limit), cancellationToken)).ToHttpResult())
            .WithTags("Diagnostics")
            .Returns<Changes>("ListChanges", "What changed on every host, in time order, over the last `seconds` (default 1800): starts, stops, kills, limits reached, and CPU or memory leaving its usual range; up to `limit` (default 60, up to 300).");

        api.MapGet("/events", static async (int? seconds, [EventLevels] string? level, [EventKinds] string? kind, int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<IReadOnlyList<HostEvent>>>(new GetEventsQuery(null, null, seconds, level, limit, kind), cancellationToken)).ToHttpResult())
            .WithTags("Events")
            .Returns<IReadOnlyList<HostEvent>>("ListEvents", "Events of every host, newest first, over the last `seconds` (default 3600); `level` is the minimum level; up to `limit` (default 100, up to 1000).");

        api.MapGet("/dependencies", static async (int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<Dependencies>>(new GetDependenciesQuery(null, null, limit), cancellationToken)).ToHttpResult())
            .WithTags("Network")
            .Returns<Dependencies>("ListDependencies", "Who calls whom, with traffic and the rate, errors and latency of decoded requests; failing edges first; up to `limit` (default 100, up to 500).");

        var network = api.MapGroup("/network").WithTags("Network");

        network.MapGet("/", static async (IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<IReadOnlyList<ContainerNetwork>>>(new GetNetworkQuery(), cancellationToken)).ToHttpResult())
            .Returns<IReadOnlyList<ContainerNetwork>>("GetNetwork", "Everything known about the network of every running container: each connection, listener, lookup and peer. Large.");

        network.MapGet("/overview", static async (int? limit, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<IReadOnlyList<ContainerNetworkOverview>>>(new GetNetworkOverviewQuery(null, null, limit), cancellationToken)).ToHttpResult())
            .Returns<IReadOnlyList<ContainerNetworkOverview>>("GetNetworkOverview", "A bounded network summary per running container: top peers, requests, connections with problems and failed lookups, up to `limit` each (default 20, up to 50).");

        api.MapGet("/captures/{id:long}", static async (long id, IMessageBus bus, CancellationToken cancellationToken) =>
                (await bus.InvokeAsync<Result<CaptureFile>>(new GetCaptureFileQuery(id), cancellationToken)).ToFileResult())
            .WithTags("Captures")
            .WithName("DownloadCapture")
            .WithSummary("Downloads a finished packet capture as a pcap file; 404 until it has data.")
            .Produces(StatusCodes.Status200OK, contentType: CaptureFile.ContentType)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static IReadOnlySet<string> Ids(string? ids) =>
        (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(32).ToHashSet();

    private static async IAsyncEnumerable<FleetUpdate> ForHosts(IAsyncEnumerable<FleetView> views, IReadOnlySet<string> hostIds, IReadOnlySet<string> containerIds)
    {
        await foreach (var view in views)
        {
            yield return view.ForHosts(hostIds, containerIds);
        }
    }
}
