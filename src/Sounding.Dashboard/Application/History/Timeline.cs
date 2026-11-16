using System.Globalization;
using Sounding.Dashboard.Application.Common;

namespace Sounding.Dashboard.Application.History;

// What happened to a host or container over a window, in time order: its events, and the episodes where any of its
// series left its usual range. Answers "what changed around the time it failed" without reading every series.
// With SignificantOnly, routine information events are left out (processes that start and end, new ports and
// destinations): a database starts a process per connection, and a reader that sees each one end takes it for a crash.
public sealed record GetTimelineQuery(string? HostId, string? ContainerId, int? Seconds = null, int? Limit = null, bool SignificantOnly = false);

public sealed record TimelineEntry(
    DateTimeOffset Time,
    DateTimeOffset? End,
    string Kind,
    string Level,
    string? ContainerId,
    string? ContainerName,
    string? Metric,
    double? Peak,
    double? Typical,
    string Message);

// Note says when the window held more events than were read.
public sealed record Timeline(string Scope, int WindowSeconds, int BucketSeconds, int Anomalies, int Events, IReadOnlyList<TimelineEntry> Entries, string? Note = null);

public static class TimelineKind
{
    public const string Event = "event";
    public const string Spike = "spike";
    public const string Drop = "drop";
}

public static class GetTimelineHandler
{
    public const int DefaultSeconds = 1800;
    public const int MaxLimit = 300;
    public const int DefaultLimit = 100;
    private const int RecurringAfter = 3;
    private const int MaxEventsRead = 1000;

    public static async Task<Result<Timeline>> Handle(GetTimelineQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (Validate.Limit(query.Limit, MaxLimit, out var limit, DefaultLimit) is { } badLimit) return badLimit;
        if (Validate.Window(query.Seconds, out var window, DefaultSeconds) is { } badWindow) return badWindow;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, query.HostId, query.ContainerId, true, cancellationToken);
        if (failure is not null) return failure;

        var host = scope.Host!;
        var names = host.Containers.ToDictionary(c => c.ContainerId, c => c.Name);
        if (scope.Container is { } scoped) names.TryAdd(scoped.ContainerId, scoped.Name);
        var series = new List<(string? ContainerId, string Metric, string Unit, long[] T, double?[] Values)>();
        int bucketSeconds;

        // The three reads of a scope are independent: started together, awaited in turn.
        // Filtered in the database, most severe first: a busy neighbour's routine events cannot push this scope's out.
        var eventsRead = repository.GetEventsAsync(new EventFilter([host.HostId], window, MaxEventsRead, scope.Container?.ContainerId,
            Significant: query.SignificantOnly, SevereFirst: true), cancellationToken);
        if (scope.Container is { } container)
        {
            var detailRead = repository.GetContainerDetailHistoryAsync(container.ContainerId, window, cancellationToken);
            var basic = await repository.GetContainerHistoryAsync(container.ContainerId, window, cancellationToken);
            var detail = await detailRead;
            bucketSeconds = basic.BucketSeconds;
            var id = container.ContainerId;
            var own = new List<(string Name, string Unit, double?[] Values)>();
            MetricSeries.Container(basic, own);
            foreach (var (name, unit, values) in own) series.Add((id, name, unit, basic.T, values));
            foreach (var (name, values) in MetricSeries.Own(ResourceTarget.Container, detail))
            {
                series.Add((id, name, DetailUnits.Of(name), detail.T, values));
            }
        }
        else
        {
            var detailRead = repository.GetHostDetailHistoryAsync(host.HostId, window, cancellationToken);
            var basic = await repository.GetHostHistoryAsync(host.HostId, window, cancellationToken);
            var detail = await detailRead;
            bucketSeconds = basic.BucketSeconds;
            series.Add((null, "cpuPercent", "%", basic.T, basic.Cpu));
            series.Add((null, "memoryUsedBytes", "bytes", basic.T, basic.MemoryUsed));
            foreach (var gpu in basic.Gpus) series.Add((null, $"gpu{gpu.Index}UtilizationPercent", "%", basic.T, gpu.Utilization));
            foreach (var c in basic.Containers)
            {
                series.Add((c.ContainerId, "cpuPercentOfHost", "%", basic.T, c.CpuPercentOfHost));
                series.Add((c.ContainerId, "memoryBytes", "bytes", basic.T, c.MemoryBytes));
            }

            foreach (var (name, values) in detail.Columns)
            {
                // Host series, and per-container series keyed "name:containerId".
                var colon = name.IndexOf(':');
                if (colon < 0 || DetailUnits.IsHostPart(name)) series.Add((null, name, DetailUnits.Of(name), detail.T, values));
                else series.Add((name[(colon + 1)..], name[..colon], DetailUnits.Of(name[..colon]), detail.T, values));
            }
        }

        var anomalies = new List<(double Score, TimelineEntry Entry)>();
        foreach (var s in series)
        {
            var containerName = s.ContainerId is null ? null : names.GetValueOrDefault(s.ContainerId) ?? s.ContainerId;
            var who = containerName is null ? "" : $"{containerName}: ";
            var episodes = Anomalies.Find(s.Values, Anomalies.MinimumChange(s.Unit));
            foreach (var group in episodes.GroupBy(e => e.Drop))
            {
                var list = group.ToList();
                var drop = group.Key;
                var kind = drop ? TimelineKind.Drop : TimelineKind.Spike;
                var verb = drop ? "fell to" : "rose to";
                // A pattern that keeps repeating (a periodic job, a retry loop) is one entry, not one per occurrence.
                if (list.Count >= RecurringAfter)
                {
                    var first = DateTimeOffset.FromUnixTimeMilliseconds(s.T[list[0].Start]);
                    var last = DateTimeOffset.FromUnixTimeMilliseconds(s.T[list[^1].End]).AddSeconds(bucketSeconds);
                    var peak = drop ? list.Min(e => e.Peak) : list.Max(e => e.Peak);
                    var message = $"{who}{s.Metric} repeatedly {verb} up to {Format(peak, s.Unit)} (typical {Format(list[0].Typical, s.Unit)}): "
                        + $"{list.Count} times between {first:HH:mm:ss} and {last:HH:mm:ss}";
                    anomalies.Add((list.Max(e => e.Score) * Math.Log2(list.Count + 1), new TimelineEntry(first, last, kind,
                        list.Any(e => e.Strong) ? "warning" : "info", s.ContainerId, containerName, s.Metric, Round(peak), Round(list[0].Typical), message)));
                    continue;
                }

                foreach (var episode in list)
                {
                    var start = DateTimeOffset.FromUnixTimeMilliseconds(s.T[episode.Start]);
                    var end = DateTimeOffset.FromUnixTimeMilliseconds(s.T[episode.End]).AddSeconds(bucketSeconds);
                    var message = $"{who}{s.Metric} {verb} {Format(episode.Peak, s.Unit)} "
                        + $"(typical {Format(episode.Typical, s.Unit)}) for {(end - start).TotalSeconds:0} s";
                    anomalies.Add((episode.Score, new TimelineEntry(start, end, kind, episode.Strong ? "warning" : "info",
                        s.ContainerId, containerName, s.Metric, Round(episode.Peak), Round(episode.Typical), message)));
                }
            }
        }

        var (events, truncated) = await eventsRead;
        var labels = await ContainerLabels.ResolveAsync(fleet, repository, events.Select(e => e.ContainerId).OfType<string>(), cancellationToken);
        var entries = new List<TimelineEntry>();
        var eventCount = 0;
        foreach (var e in events)
        {
            eventCount++;
            entries.Add(new TimelineEntry(e.Time, null, TimelineKind.Event, e.Level, e.ContainerId,
                e.ContainerId is null ? null : labels.GetValueOrDefault(e.ContainerId, (e.ContainerId, false)).Item1, e.Kind, null, null, e.Message));
        }

        // Events first (they are what happened), then the strongest anomalies up to the limit.
        if (entries.Count > limit)
        {
            entries.Sort(static (a, b) => LevelRank(b.Level).CompareTo(LevelRank(a.Level)) is var c and not 0 ? c : b.Time.CompareTo(a.Time));
            entries.RemoveRange(limit, entries.Count - limit);
        }

        anomalies.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        foreach (var (_, entry) in anomalies)
        {
            if (entries.Count >= limit) break;
            entries.Add(entry);
        }

        entries.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        return new Timeline(scope.Describe(), (int)window.TotalSeconds, bucketSeconds, anomalies.Count, eventCount, entries,
            truncated ? $"More than {MaxEventsRead} events in this window: only the {MaxEventsRead} most severe, newest first, were read." : null);
    }

    private static int LevelRank(string level) => level switch { "error" => 2, "warning" => 1, _ => 0 };

    private static double Round(double value) => Math.Round(value, Math.Abs(value) >= 100 ? 0 : 3);

    private static string Format(double value, string unit) => unit switch
    {
        "bytes" => Bytes(value),
        "bytes/s" => Bytes(value) + "/s",
        _ => value.ToString(Math.Abs(value) >= 100 ? "0" : "0.###", CultureInfo.InvariantCulture) + " " + unit,
    };

    private static string Bytes(double value) => Math.Abs(value) switch
    {
        >= 1 << 30 => (value / (1 << 30)).ToString("0.##", CultureInfo.InvariantCulture) + " GiB",
        >= 1 << 20 => (value / (1 << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MiB",
        >= 1 << 10 => (value / (1 << 10)).ToString("0", CultureInfo.InvariantCulture) + " KiB",
        _ => value.ToString("0", CultureInfo.InvariantCulture) + " B",
    };
}

// Robust outlier episodes in a bucketed series: buckets far from the median, measured in median absolute
// deviations, merged when consecutive. A change must also be large enough to matter in its unit, so a series that
// is flat at zero does not report every blip.
public static class Anomalies
{
    private const int MinimumPoints = 8;
    private const double Threshold = 6;
    private const double MaxZ = 50;

    public readonly record struct Episode(int Start, int End, double Peak, double Typical, double Score, bool Drop, bool Strong);

    public static double MinimumChange(string unit) => unit switch
    {
        "%" => 2,
        "cores" => 0.1,
        "bytes" => 16 << 20,
        "bytes/s" => 64 << 10,
        "ops/s" => 10,
        "packets/s" => 50,
        "ms/s" or "ms" => 20,
        "count" => 5,
        "W" => 10,
        "MHz" => 100,
        "°C" => 5,
        _ => 0.2,
    };

    public static List<Episode> Find(double?[] values, double minimumChange)
    {
        var episodes = new List<Episode>();
        var present = new List<double>(values.Length);
        foreach (var value in values)
        {
            if (value is { } v) present.Add(v);
        }

        if (present.Count < MinimumPoints) return episodes;

        var median = Median(present);
        for (var i = 0; i < present.Count; i++) present[i] = Math.Abs(present[i] - median);
        var spread = Math.Max(Math.Max(1.4826 * Median(present), Math.Abs(median) * 0.1), minimumChange / Threshold);

        var start = -1;
        double peak = 0, z = 0;
        var drop = false;
        for (var i = 0; i <= values.Length; i++)
        {
            var value = i < values.Length ? values[i] : null;
            var deviation = value is { } v ? v - median : 0;
            var outlier = value is not null && Math.Abs(deviation) >= Math.Max(Threshold * spread, minimumChange)
                // A drop only counts for a series that is usually well above zero (a throughput that stopped).
                && (deviation > 0 || median > 0 && value < median * 0.2);
            if (outlier && (start < 0 || (deviation < 0) == drop))
            {
                if (start < 0)
                {
                    (start, drop, peak, z) = (i, deviation < 0, value!.Value, 0);
                }

                if (drop ? value < peak : value > peak) peak = value!.Value;
                z = Math.Max(z, Math.Min(MaxZ, Math.Abs(deviation) / spread));
                continue;
            }

            if (start >= 0)
            {
                // Ranked by how unusual and how large the change is, so a long, large spike outranks a brief blip.
                var size = Math.Abs(peak - median) / minimumChange;
                var score = Math.Round(z * Math.Log2(1 + size) * Math.Sqrt(i - start), 1);
                episodes.Add(new Episode(start, i - 1, peak, median, score, drop, z >= 20 && size >= 5));
                start = -1;
                if (outlier) i--;
            }
        }

        return episodes;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
