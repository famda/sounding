using Sounding.Dashboard.Application.Common;

namespace Sounding.Dashboard.Application.History;

// Histories of hosts and containers that are no longer shown still answer: the key is resolved against the fleet and
// then the database, and one that names nothing is NotFound.
public sealed record GetFleetHistoryQuery(int? Seconds = null);

public sealed record GetHostHistoryQuery(string HostId, int? Seconds = null);

public sealed record GetHostDetailHistoryQuery(string HostId, int? Seconds = null);

public sealed record GetContainerHistoryQuery(string ContainerId, int? Seconds = null);

public sealed record GetContainerDetailHistoryQuery(string ContainerId, int? Seconds = null);

public static class GetFleetHistoryHandler
{
    public static async Task<Result<FleetHistory>> Handle(GetFleetHistoryQuery query, FleetRepository repository, CancellationToken cancellationToken) =>
        Validate.Window(query.Seconds, out var window) is { } invalid ? invalid
            : await repository.GetFleetHistoryAsync(window, cancellationToken);
}

public static class GetHostHistoryHandler
{
    public static async Task<Result<HostHistory>> Handle(GetHostHistoryQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (Validate.Window(query.Seconds, out var window) is { } invalid) return invalid;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, query.HostId, null, required: true, cancellationToken);
        return failure is not null ? failure : await repository.GetHostHistoryAsync(scope.Host!.HostId, window, cancellationToken);
    }
}

public static class GetHostDetailHistoryHandler
{
    public static async Task<Result<SeriesHistory>> Handle(GetHostDetailHistoryQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (Validate.Window(query.Seconds, out var window) is { } invalid) return invalid;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, query.HostId, null, required: true, cancellationToken);
        return failure is not null ? failure : await repository.GetHostDetailHistoryAsync(scope.Host!.HostId, window, cancellationToken);
    }
}

public static class GetContainerHistoryHandler
{
    public static async Task<Result<ContainerHistory>> Handle(GetContainerHistoryQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (Validate.Window(query.Seconds, out var window) is { } invalid) return invalid;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, null, query.ContainerId, required: true, cancellationToken);
        return failure is not null ? failure : await repository.GetContainerHistoryAsync(scope.Container!.ContainerId, window, cancellationToken);
    }
}

public static class GetContainerDetailHistoryHandler
{
    public static async Task<Result<SeriesHistory>> Handle(GetContainerDetailHistoryQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (Validate.Window(query.Seconds, out var window) is { } invalid) return invalid;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, null, query.ContainerId, required: true, cancellationToken);
        return failure is not null ? failure : await repository.GetContainerDetailHistoryAsync(scope.Container!.ContainerId, window, cancellationToken);
    }
}
