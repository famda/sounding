using Sounding.Dashboard.Application.Common;

namespace Sounding.Dashboard.Application.Processes;

// Every process of a host's containers, grouped by container, now or at a past moment: what the dashboard draws.
public sealed record GetProcessesByContainerQuery(string HostId, DateTimeOffset? At = null);

// The same over a window, with averages and peaks.
public sealed record GetProcessWindowByContainerQuery(string HostId, int? Seconds = null);

public sealed record GetTopProcessesQuery(string? HostId, string? ContainerId, int? Seconds = null, string? Sort = null, int? Limit = null);

// The processes now, or at a past moment, with what only a live sample carries: state, waits, open files against
// their limit, OOM score, swap, hot threads.
public sealed record GetLiveProcessesQuery(
    string? HostId,
    string? ContainerId,
    DateTimeOffset? At = null,
    string? Sort = null,
    string? State = null,
    int? Limit = null);

public sealed record TopProcess(string ContainerId, string? ContainerName, ProcessWindow Process);

public sealed record TopProcesses(string HostId, int WindowSeconds, string Sort, int TotalCount, IReadOnlyList<TopProcess> Processes);

public sealed record LiveProcess(string ContainerId, string? ContainerName, ProcessUsage Process);

public sealed record LiveProcesses(string HostId, DateTimeOffset? At, string Sort, int TotalCount, IReadOnlyList<LiveProcess> Processes);

public static class ProcessSort
{
    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string MemoryGrowth = "memoryGrowth";
    public const string Gpu = "gpu";
    public const string Io = "io";
    public const string Wait = "wait";
    public const string OpenFiles = "openFiles";
    public const string Threads = "threads";
    public static readonly string[] Window = [Cpu, Memory, MemoryGrowth, Gpu, Io];
    public static readonly string[] Live = [Cpu, Memory, Gpu, Io, Wait, OpenFiles, Threads];
}

public sealed class WindowProcessSortsAttribute() : ChoicesAttribute(ProcessSort.Window);

public sealed class LiveProcessSortsAttribute() : ChoicesAttribute(ProcessSort.Live);

public static class GetProcessesByContainerHandler
{
    public static async Task<Result<IReadOnlyList<ContainerProcesses>>> Handle(GetProcessesByContainerQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, query.HostId, null, required: true, cancellationToken);
        if (failure is not null) return failure;

        return Result<IReadOnlyList<ContainerProcesses>>.Ok(query.At is { } at
            ? await repository.GetHostProcessesAtAsync(scope.Host!.HostId, at, cancellationToken)
            : await repository.GetHostProcessesAsync(scope.Host!.HostId, cancellationToken));
    }
}

public static class GetProcessWindowByContainerHandler
{
    public static async Task<Result<IReadOnlyList<ContainerProcessWindow>>> Handle(GetProcessWindowByContainerQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (Validate.Window(query.Seconds, out var window) is { } invalid) return invalid;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, query.HostId, null, required: true, cancellationToken);
        if (failure is not null) return failure;

        return Result<IReadOnlyList<ContainerProcessWindow>>.Ok(await repository.GetHostProcessWindowAsync(scope.Host!.HostId, window, cancellationToken));
    }
}

public static class GetTopProcessesHandler
{
    public const int MaxLimit = 100;

    public static async Task<Result<TopProcesses>> Handle(GetTopProcessesQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (Validate.OneOf(query.Sort, "sort", ProcessSort.Window) is { } badSort) return badSort;
        if (Validate.Limit(query.Limit, MaxLimit, out var limit) is { } badLimit) return badLimit;
        if (Validate.Window(query.Seconds, out var window) is { } badWindow) return badWindow;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, query.HostId, query.ContainerId, required: true, cancellationToken);
        if (failure is not null) return failure;

        // Process samples are read per host, so a container is read through its host.
        var host = scope.Host!;
        var processes = new List<TopProcess>();
        foreach (var container in await repository.GetHostProcessWindowAsync(host.HostId, window, cancellationToken))
        {
            if (!scope.Includes(container.ContainerId)) continue;
            var name = ProcessNames.Of(scope, container.ContainerId);
            foreach (var process in container.Processes) processes.Add(new TopProcess(container.ContainerId, name, process));
        }

        var sort = query.Sort ?? ProcessSort.Cpu;
        processes.Sort(Comparer(sort));
        return new TopProcesses(host.HostId, (int)window.TotalSeconds, sort, processes.Count, [.. processes.Take(limit)]);
    }

    private static Comparison<TopProcess> Comparer(string sort) => sort switch
    {
        ProcessSort.Memory => static (a, b) => b.Process.PeakMemoryBytes.CompareTo(a.Process.PeakMemoryBytes),
        ProcessSort.MemoryGrowth => static (a, b) => Nullable.Compare(b.Process.MemoryGrowthBytesPerHour, a.Process.MemoryGrowthBytesPerHour),
        ProcessSort.Gpu => static (a, b) => Nullable.Compare(b.Process.AverageGpuPercent, a.Process.AverageGpuPercent),
        ProcessSort.Io => static (a, b) => Nullable.Compare(b.Process.ReadBytes + b.Process.WriteBytes, a.Process.ReadBytes + a.Process.WriteBytes),
        _ => static (a, b) => (b.Process.AverageCores ?? b.Process.PeakCores).CompareTo(a.Process.AverageCores ?? a.Process.PeakCores),
    };
}

public static class GetLiveProcessesHandler
{
    public const int MaxLimit = 200;

    public static async Task<Result<LiveProcesses>> Handle(GetLiveProcessesQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (Validate.OneOf(query.Sort, "sort", ProcessSort.Live) is { } badSort) return badSort;
        if (Validate.Limit(query.Limit, MaxLimit, out var limit) is { } badLimit) return badLimit;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, query.HostId, query.ContainerId, required: true, cancellationToken);
        if (failure is not null) return failure;

        var host = scope.Host!;
        var containers = query.At is { } at
            ? await repository.GetHostProcessesAtAsync(host.HostId, at, cancellationToken)
            : await repository.GetHostProcessesAsync(host.HostId, cancellationToken);

        var processes = new List<LiveProcess>();
        foreach (var container in containers)
        {
            if (!scope.Includes(container.ContainerId)) continue;
            var name = ProcessNames.Of(scope, container.ContainerId);
            foreach (var process in container.Processes)
            {
                if (query.State is { } state && process.State?.StartsWith(state, StringComparison.OrdinalIgnoreCase) != true) continue;
                processes.Add(new LiveProcess(container.ContainerId, name, process));
            }
        }

        var sort = query.Sort ?? ProcessSort.Cpu;
        processes.Sort(Comparer(sort));
        return new LiveProcesses(host.HostId, query.At, sort, processes.Count, [.. processes.Take(limit)]);
    }

    private static Comparison<LiveProcess> Comparer(string sort) => sort switch
    {
        ProcessSort.Memory => static (a, b) => b.Process.MemoryBytes.CompareTo(a.Process.MemoryBytes),
        ProcessSort.Gpu => static (a, b) => Nullable.Compare(b.Process.GpuPercent, a.Process.GpuPercent),
        ProcessSort.Io => static (a, b) => Nullable.Compare(b.Process.ReadBytesPerSecond + b.Process.WriteBytesPerSecond, a.Process.ReadBytesPerSecond + a.Process.WriteBytesPerSecond),
        ProcessSort.Wait => static (a, b) => Nullable.Compare(b.Process.WaitMillisecondsPerSecond, a.Process.WaitMillisecondsPerSecond),
        ProcessSort.OpenFiles => static (a, b) => Nullable.Compare(FileShare(b.Process), FileShare(a.Process)),
        ProcessSort.Threads => static (a, b) => b.Process.Threads.CompareTo(a.Process.Threads),
        _ => static (a, b) => b.Process.CpuCores.CompareTo(a.Process.CpuCores),
    };

    // Open files as a share of the process's limit when known, so a leak near its limit sorts first.
    private static double? FileShare(ProcessUsage p) =>
        p.OpenFiles is { } open ? p.OpenFilesLimit is > 0 and var max ? open / (double)max : open / 1e9 : null;
}

internal static class ProcessNames
{
    public static string? Of(Scope scope, string containerId)
    {
        if (scope.Container is { } scoped && scoped.ContainerId == containerId) return scoped.Name;
        foreach (var container in scope.Host!.Containers)
        {
            if (container.ContainerId == containerId) return container.Name;
        }

        return null;
    }
}
