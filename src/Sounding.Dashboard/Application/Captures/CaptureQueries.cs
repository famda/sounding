using Sounding.Dashboard.Application.Common;

namespace Sounding.Dashboard.Application.Captures;

public sealed record RequestCaptureCommand(string ContainerId, int? Seconds = null);

public sealed record CaptureRequested(long Id, string ContainerId, int Seconds);

public sealed record GetCapturesQuery(string ContainerId);

public sealed record GetCaptureFileQuery(long Id);

public sealed record CaptureFile(string ContainerId, DateTimeOffset At, byte[] Data)
{
    public const string ContentType = "application/vnd.tcpdump.pcap";
    public string FileName => $"{ContainerId}-{At:yyyyMMdd-HHmmss}.pcap";
}

public static class RequestCaptureHandler
{
    public const int DefaultSeconds = 10;
    public const int MaxSeconds = 60;

    public static async Task<Result<CaptureRequested>> Handle(RequestCaptureCommand command, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        var seconds = command.Seconds ?? DefaultSeconds;
        if (seconds is < 1 or > MaxSeconds) return Failure.Invalid("seconds", $"'seconds' must be between 1 and {MaxSeconds}.");
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        if (FleetSnapshot.ResolveScope(fleet, null, command.ContainerId, required: true, out var scope) is { } failure) return failure;

        var container = scope.Container!;
        var label = container.Name ?? container.ContainerId;
        if (container.Status != ContainerStatus.Online) return Result<CaptureRequested>.Conflict($"Container {label} is not running.");

        if (await repository.RequestCaptureAsync(container.ContainerId, seconds, cancellationToken) is { } id) return new CaptureRequested(id, container.ContainerId, seconds);
        return Result<CaptureRequested>.Conflict($"A capture of container {label} is already in progress.");
    }
}

public static class GetCapturesHandler
{
    public static async Task<Result<IReadOnlyList<CaptureInfo>>> Handle(GetCapturesQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, null, query.ContainerId, required: true, cancellationToken);
        if (failure is not null) return failure;
        return Result<IReadOnlyList<CaptureInfo>>.Ok(await repository.GetCapturesAsync(scope.Container!.ContainerId, cancellationToken));
    }
}

public static class GetCaptureFileHandler
{
    public static async Task<Result<CaptureFile>> Handle(GetCaptureFileQuery query, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (await repository.GetCaptureFileAsync(query.Id, cancellationToken) is { } capture) return new CaptureFile(capture.ContainerId, capture.At, capture.Data);
        return Failure.NotFound($"Capture {query.Id} does not exist or has no data yet.");
    }
}
