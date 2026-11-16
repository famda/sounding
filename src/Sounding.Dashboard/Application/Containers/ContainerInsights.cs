using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.Events;

namespace Sounding.Dashboard.Application.Containers;

// The output lines the agent keeps of the latest sample (not a full history), filtered.
public sealed record GetLogsQuery(string ContainerId, string? MinimumLevel = null, string? Contains = null, int? Limit = null);

public sealed record ContainerLogs(
    string ContainerId,
    string? ContainerName,
    string HostId,
    LogStats? Rates,
    int Kept,
    int Matched,
    IReadOnlyList<LogLine> Lines,
    string Note);

public sealed record GetStorageQuery(string ContainerId);

public sealed record ContainerStorage(
    string ContainerId,
    string? ContainerName,
    string HostId,
    long? DiskUsedBytes,
    long? DiskFreeBytes,
    double? GrowthBytesPerHour,
    double? HoursUntilFull,
    long? FileCount,
    long? OpenFiles,
    long DeletedButOpenBytes,
    IReadOnlyList<PathSize> LargestPaths,
    IReadOnlyList<PathSize> GrowingPaths,
    IReadOnlyList<FileSize> LargestFiles,
    IReadOnlyList<DeletedFile> DeletedFiles,
    IReadOnlyList<MountUsage> Mounts,
    IReadOnlyList<IoDevice> IoDevices);

public static class GetLogsHandler
{
    public const int MaxLimit = 500;

    public static async Task<Result<ContainerLogs>> Handle(GetLogsQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (Validate.OneOf(query.MinimumLevel, "level", EventLevel.All) is { } badLevel) return badLevel;
        if (Validate.Limit(query.Limit ?? 100, MaxLimit, out var limit) is { } badLimit) return badLimit;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, null, query.ContainerId, true, cancellationToken);
        if (failure is not null) return failure;

        var container = scope.Container!;
        var detail = container.Usage?.Detail;
        var recent = detail?.RecentLogs ?? [];
        var lines = new List<LogLine>();
        for (var i = recent.Count - 1; i >= 0 && lines.Count < limit; i--)
        {
            var line = recent[i];
            if (query.MinimumLevel is { } level && !EventLevel.AtLeast(line.Level, level)) continue;
            if (query.Contains is { } text && !line.Text.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add(line);
        }

        lines.Reverse();
        var note = container.Status != ContainerStatus.Online
            ? "The container is not running: no recent output is kept. Its events may include its last output."
            : "Only the most recent lines of the latest sample are kept, not a history. The logErrors and logWarnings metrics give error rates over time.";
        return new ContainerLogs(container.ContainerId, container.Name, scope.Host!.HostId, detail?.Logs, recent.Count, lines.Count, lines, note);
    }
}

public static class GetStorageHandler
{
    // Beyond a year, "until full" says nothing useful.
    private const double MaxHoursUntilFull = 24 * 365;

    public static Result<ContainerStorage> Handle(GetStorageQuery query, FleetSnapshot snapshot)
    {
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        if (FleetSnapshot.ResolveScope(fleet, null, query.ContainerId, required: true, out var scope) is { } failure) return failure;

        var container = scope.Container!;
        var detail = container.Usage?.Detail;
        double? hoursUntilFull = detail is { DiskGrowthBytesPerHour: > 0 and var growth, DiskFreeBytes: { } free }
            && free / growth < MaxHoursUntilFull ? Math.Round(free / growth, 1) : null;
        long deleted = 0;
        foreach (var file in detail?.DeletedFiles ?? []) deleted += file.Bytes;

        return new ContainerStorage(
            container.ContainerId, container.Name, scope.Host!.HostId,
            container.Usage?.DiskUsedBytes, detail?.DiskFreeBytes, detail?.DiskGrowthBytesPerHour, hoursUntilFull,
            detail?.FileCount, detail?.OpenFiles, deleted,
            detail?.LargestPaths ?? [], detail?.GrowingPaths ?? [], detail?.LargestFiles ?? [], detail?.DeletedFiles ?? [],
            detail?.Mounts ?? [], detail?.IoDevices ?? []);
    }
}
