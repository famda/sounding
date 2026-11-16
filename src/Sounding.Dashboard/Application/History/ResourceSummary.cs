using Sounding.Dashboard.Application.Common;

namespace Sounding.Dashboard.Application.History;

// T and DetailT are the bucket times (Unix milliseconds) of the basic and the detail metrics' Values, when asked for.
public sealed record ResourceSummary(
    string Target,
    string Id,
    string? Name,
    int WindowSeconds,
    int BucketSeconds,
    int Buckets,
    IReadOnlyList<MetricSummary> Metrics,
    IReadOnlyList<string>? OtherMetrics = null,
    long[]? T = null,
    long[]? DetailT = null);

// ChangePercent compares the average of the last quarter of the window with the first quarter: a trend without
// the noise of single samples.
public sealed record MetricSummary(
    string Name,
    string Unit,
    double? Min,
    double? Average,
    double? Max,
    double? Last,
    double? ChangePercent,
    double?[]? Values = null);

public static class ResourceTarget
{
    public const string Host = "host";
    public const string Container = "container";
}

// One summary over every metric of a host or container. Without Metrics, the basic ones (CPU, memory, disk, network,
// GPU) and the names of the rest; with names, those; with "all", every one.
public sealed record GetMetricsSummaryQuery(string? HostId, string? ContainerId, int? Seconds = null, string? Metrics = null, bool IncludeSeries = false);

public static class GetMetricsSummaryHandler
{
    public const string All = "all";

    public static async Task<Result<ResourceSummary>> Handle(GetMetricsSummaryQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (Validate.Window(query.Seconds, out var window) is { } invalid) return invalid;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, query.HostId, query.ContainerId, required: true, cancellationToken);
        if (failure is not null) return failure;

        var wanted = query.Metrics?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var all = wanted?.Contains(All) == true;
        if (wanted is { Count: 0 } || all) wanted = null;
        var withDetail = all || wanted is not null;

        var basic = new List<(string Name, string Unit, double?[] Values)>();
        SeriesHistory? detail;
        string target, id;
        string? name;
        int bucketSeconds;
        long[] t;
        if (scope.Container is { } container)
        {
            (target, id, name) = (ResourceTarget.Container, container.ContainerId, container.Name);
            var basicRead = repository.GetContainerHistoryAsync(id, window, cancellationToken);
            detail = withDetail ? await repository.GetContainerDetailHistoryAsync(id, window, cancellationToken) : null;
            var c = await basicRead;
            (bucketSeconds, t) = (c.BucketSeconds, c.T);
            MetricSeries.Container(c, basic);
        }
        else
        {
            var host = scope.Host!;
            (target, id, name) = (ResourceTarget.Host, host.HostId, host.DisplayName);
            var basicRead = repository.GetHostHistoryAsync(id, window, cancellationToken);
            detail = withDetail ? await repository.GetHostDetailHistoryAsync(id, window, cancellationToken) : null;
            var h = await basicRead;
            (bucketSeconds, t) = (h.BucketSeconds, h.T);
            MetricSeries.Host(h, basic);
        }

        var metrics = new List<MetricSummary>();
        var anyBasic = false;
        foreach (var (metric, unit, values) in basic)
        {
            if (wanted is not null && !wanted.Remove(metric)) continue;
            metrics.Add(Summarize(metric, unit, values, query.IncludeSeries));
            anyBasic = true;
        }

        var details = new List<MetricSummary>();
        if (detail is not null)
        {
            foreach (var (metric, values) in MetricSeries.Own(target, detail))
            {
                if (wanted is not null && !wanted.Remove(metric)) continue;
                details.Add(Summarize(metric, DetailUnits.Of(metric), values, query.IncludeSeries));
            }

            details.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        if (wanted is { Count: > 0 })
        {
            var available = basic.Select(m => m.Name).Concat(MetricSeries.Own(target, detail!).Select(m => m.Name).Order(StringComparer.Ordinal));
            return Result<ResourceSummary>.Invalid("metrics",
                $"No such metric for this {target}: {string.Join(", ", wanted)}. Available: {string.Join(", ", available)}.");
        }

        metrics.AddRange(details);
        return new ResourceSummary(target, id, name, (int)window.TotalSeconds, bucketSeconds, t.Length, metrics,
            withDetail ? null : MetricSeries.DetailNames(target),
            query.IncludeSeries && anyBasic ? t : null,
            query.IncludeSeries && details.Count > 0 ? detail!.T : null);
    }

    public static MetricSummary Summarize(string name, string unit, double?[] values, bool includeSeries)
    {
        double min = double.MaxValue, max = double.MinValue, sum = 0;
        double? last = null;
        var count = 0;
        foreach (var value in values)
        {
            if (value is not { } v) continue;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
            sum += v;
            last = v;
            count++;
        }

        if (count == 0)
        {
            return new MetricSummary(name, unit, null, null, null, null, null, includeSeries ? values : null);
        }

        var quarter = Math.Max(1, values.Length / 4);
        var head = Average(values.AsSpan(0, quarter));
        var tail = Average(values.AsSpan(values.Length - quarter));
        double? change = head is > 0 && tail is { } end && values.Length >= 4 ? Round((end - head.Value) * 100 / head.Value) : null;

        return new MetricSummary(name, unit, Round(min), Round(sum / count), Round(max), Round(last!.Value), change,
            includeSeries ? values : null);
    }

    private static double? Average(ReadOnlySpan<double?> values)
    {
        double sum = 0;
        var count = 0;
        foreach (var value in values)
        {
            if (value is { } v)
            {
                sum += v;
                count++;
            }
        }

        return count == 0 ? null : sum / count;
    }

    private static double Round(double value) => Math.Round(value, Math.Abs(value) >= 100 ? 0 : 2);
}

// The series of a host or a container by metric name, shared by the summary and the timeline.
public static class MetricSeries
{
    public static void Container(ContainerHistory c, List<(string Name, string Unit, double?[] Values)> series)
    {
        series.Add(("cpuCores", "cores", Nullable(c.CpuCores)));
        series.Add(("memoryBytes", "bytes", Nullable(c.MemoryBytes)));
        series.Add(("ioReadBytesPerSecond", "bytes/s", Nullable(c.IoRead)));
        series.Add(("ioWriteBytesPerSecond", "bytes/s", Nullable(c.IoWrite)));
        series.Add(("netReceiveBytesPerSecond", "bytes/s", Nullable(c.NetReceive)));
        series.Add(("netTransmitBytesPerSecond", "bytes/s", Nullable(c.NetTransmit)));
        series.Add(("gpuPercent", "%", c.GpuPercent));
        series.Add(("gpuMemoryBytes", "bytes", c.GpuMemoryBytes));
    }

    public static void Host(HostHistory h, List<(string Name, string Unit, double?[] Values)> series)
    {
        series.Add(("cpuPercent", "%", h.Cpu));
        series.Add(("memoryUsedBytes", "bytes", h.MemoryUsed));
        series.Add(("diskReadBytesPerSecond", "bytes/s", h.DiskRead));
        series.Add(("diskWriteBytesPerSecond", "bytes/s", h.DiskWrite));
        series.Add(("netReceiveBytesPerSecond", "bytes/s", h.NetReceive));
        series.Add(("netTransmitBytesPerSecond", "bytes/s", h.NetTransmit));
        foreach (var gpu in h.Gpus)
        {
            series.Add(($"gpu{gpu.Index}UtilizationPercent", "%", gpu.Utilization));
            series.Add(($"gpu{gpu.Index}MemoryPercent", "%", gpu.MemoryUsage));
        }
    }

    // A host's detail history also carries its containers' series ("name:containerId"), which are left out here.
    public static IEnumerable<(string Name, double?[] Values)> Own(string target, SeriesHistory history)
    {
        foreach (var (name, values) in history.Columns)
        {
            if (target == ResourceTarget.Host && name.Contains(':') && !DetailUnits.IsHostPart(name)) continue;
            yield return (name, values);
        }
    }

    // The detail metrics worth naming to a reader who has not asked for them yet; "all" returns every one recorded.
    public static IReadOnlyList<string> DetailNames(string target) => target == ResourceTarget.Host ? HostDetail : ContainerDetail;

    private static readonly string[] ContainerDetail =
    [
        "cpuPressure", "memoryPressure", "ioPressure", "throttled", "anon", "file", "kernel", "majorFaults", "refaults",
        "tasks", "readOps", "writeOps", "retransmits", "resets", "failedConnects", "dnsLookups", "dnsFailures", "dnsReplyMs",
        "openFiles", "logLines", "logErrors", "logWarnings", "internetIn", "internetOut",
    ];

    private static readonly string[] HostDetail =
    [
        "cpuPressure", "memoryPressure", "ioPressure", "cpuUser", "cpuSystem", "cpuIoWait", "cpuSteal", "swapIn", "swapOut",
        "running", "blocked", "retransmits", "failedConnects", "conntrack", "fileHandles",
    ];

    private static double?[] Nullable(double[] values)
    {
        var result = new double?[values.Length];
        for (var i = 0; i < values.Length; i++) result[i] = values[i];
        return result;
    }
}
