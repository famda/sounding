using System.Globalization;
using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.Events;
using Sounding.Dashboard.Application.History;
using Sounding.Dashboard.Application.Network;

namespace Sounding.Dashboard.Application.Diagnostics;

// Answers that join several readings on the server, for readers (small models above all) that call tools well but
// join their results poorly: what differs from before, what changed anywhere, who competes for a host, and whether
// one stated thing is true.

// The last Seconds against the same length of time before them.
public sealed record ComparePeriodsQuery(string? HostId, string? ContainerId, int? Seconds = null);

public sealed record MetricChange(string Name, string Unit, double Before, double Now, double? ChangePercent, string Summary);

public sealed record PeriodComparison(
    string Target,
    string? Name,
    int PeriodSeconds,
    DateTimeOffset Boundary,
    int MetricsCompared,
    IReadOnlyList<MetricChange> Changed,
    string Summary);

public static class ComparePeriodsHandler
{
    public const int DefaultSeconds = 900;
    public const int MaxSeconds = Validate.MaxWindowSeconds / 2;
    private const int MaxChanges = 15;
    private const double MinimumRelativeChange = 0.25;

    public static async Task<Result<PeriodComparison>> Handle(ComparePeriodsQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        var seconds = query.Seconds ?? DefaultSeconds;
        if (seconds is < Validate.MinWindowSeconds or > MaxSeconds) return Failure.Invalid("seconds", $"'seconds' must be between {Validate.MinWindowSeconds} and {MaxSeconds}.");
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, query.HostId, query.ContainerId, required: true, cancellationToken);
        if (failure is not null) return failure;

        var window = TimeSpan.FromSeconds(seconds * 2);
        var series = new List<(string Name, string Unit, long[] T, double?[] Values)>();
        string target;
        string? name;
        if (scope.Container is { } container)
        {
            (target, name) = (ResourceTarget.Container, container.Name ?? container.ContainerId);
            var detailRead = repository.GetContainerDetailHistoryAsync(container.ContainerId, window, cancellationToken);
            var basic = await repository.GetContainerHistoryAsync(container.ContainerId, window, cancellationToken);
            var detail = await detailRead;
            var own = new List<(string Name, string Unit, double?[] Values)>();
            MetricSeries.Container(basic, own);
            foreach (var (metric, unit, values) in own) series.Add((metric, unit, basic.T, values));
            foreach (var (metric, values) in MetricSeries.Own(target, detail)) series.Add((metric, DetailUnits.Of(metric), detail.T, values));
        }
        else
        {
            var host = scope.Host!;
            (target, name) = (ResourceTarget.Host, host.DisplayName);
            var detailRead = repository.GetHostDetailHistoryAsync(host.HostId, window, cancellationToken);
            var basic = await repository.GetHostHistoryAsync(host.HostId, window, cancellationToken);
            var detail = await detailRead;
            var own = new List<(string Name, string Unit, double?[] Values)>();
            MetricSeries.Host(basic, own);
            foreach (var (metric, unit, values) in own) series.Add((metric, unit, basic.T, values));
            foreach (var (metric, values) in MetricSeries.Own(target, detail)) series.Add((metric, DetailUnits.Of(metric), detail.T, values));
        }

        // The fleet's clock is the database's, the same one the histories are cut by.
        var boundary = fleet.Timestamp.AddSeconds(-seconds);
        var cut = boundary.ToUnixTimeMilliseconds();
        var changes = new List<(double Weight, MetricChange Change)>();
        var compared = 0;
        foreach (var (metric, unit, t, values) in series)
        {
            if (Average(t, values, long.MinValue, cut) is not { } before || Average(t, values, cut, long.MaxValue) is not { } now) continue;
            compared++;
            var delta = now - before;
            var relative = Math.Abs(delta) / Math.Max(Math.Abs(before), Anomalies.MinimumChange(unit));
            if (Math.Abs(delta) < Anomalies.MinimumChange(unit) || relative < MinimumRelativeChange) continue;

            double? percent = Math.Abs(before) > 1e-9 ? Math.Round(delta * 100 / Math.Abs(before), 0) : null;
            changes.Add((relative, new MetricChange(metric, unit, Round(before), Round(now), percent,
                $"{metric} {(delta > 0 ? "rose" : "fell")} from {Format(before, unit)} to {Format(now, unit)}"
                + (percent is { } p ? string.Create(CultureInfo.InvariantCulture, $" ({(p > 0 ? "+" : "")}{p:0} %)") : ""))));
        }

        changes.Sort(static (a, b) => b.Weight.CompareTo(a.Weight));
        var changed = changes.Take(MaxChanges).Select(c => c.Change).ToList();
        var summary = compared == 0 ? "No samples in one of the two periods, so nothing can be compared."
            : changed.Count == 0 ? $"None of the {compared} metrics differs noticeably between the two periods."
            : $"{changes.Count} of {compared} metrics differ noticeably; the largest changes are listed first.";
        return new PeriodComparison(target, name, seconds, boundary, compared, changed, summary);
    }

    private static double? Average(long[] t, double?[] values, long from, long to)
    {
        double sum = 0;
        var count = 0;
        for (var i = 0; i < values.Length && i < t.Length; i++)
        {
            if (t[i] < from || t[i] >= to || values[i] is not { } value) continue;
            sum += value;
            count++;
        }

        return count == 0 ? null : sum / count;
    }

    private static double Round(double value) => Math.Round(value, Math.Abs(value) >= 100 ? 0 : 3);

    public static string Format(double value, string unit) => unit switch
    {
        "bytes" => Bytes(value),
        "bytes/s" => Bytes(value) + "/s",
        _ => value.ToString(Math.Abs(value) >= 100 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + unit,
    };

    private static string Bytes(double value) => Math.Abs(value) switch
    {
        >= 1 << 30 => (value / (1 << 30)).ToString("0.##", CultureInfo.InvariantCulture) + " GiB",
        >= 1 << 20 => (value / (1 << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MiB",
        >= 1 << 10 => (value / (1 << 10)).ToString("0", CultureInfo.InvariantCulture) + " KiB",
        _ => value.ToString("0", CultureInfo.InvariantCulture) + " B",
    };
}

// What happened anywhere, in time order, for when it is not yet known where to look: starts, stops, kills and limits
// reached, and the moments a host's or container's CPU or memory left its usual range.
public sealed record GetChangesQuery(string? HostId = null, int? Seconds = null, int? Limit = null);

// An event that repeats (a restart loop, a limit reached again and again) is one entry: Time is the first time, End the
// last, Count how often.
public sealed record Change(DateTimeOffset Time, DateTimeOffset? End, int Count, string Host, string? Container, string Kind, string Level, string Message);

// HostsTotal is how many hosts the scope covers and HostsRead how many were read: when they differ, Coverage says what
// was left out, and an empty list proves nothing about the hosts not read. Total and Omitted count only the entries of
// the hosts read.
public sealed record Changes(int WindowSeconds, int HostsTotal, int HostsRead, int Total, int Omitted, IReadOnlyList<Change> Entries, string? Coverage = null);

public static class GetChangesHandler
{
    public const int DefaultSeconds = 1800;
    public const int DefaultLimit = 60;
    public const int MaxLimit = 300;
    private const int MaxHosts = 10;
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(2);

    public static async Task<Result<Changes>> Handle(GetChangesQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (Validate.Limit(query.Limit, MaxLimit, out var limit, DefaultLimit) is { } badLimit) return badLimit;
        if (Validate.Window(query.Seconds, out var window, DefaultSeconds) is { } badWindow) return badWindow;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, query.HostId, null, required: false, cancellationToken);
        if (failure is not null) return failure;

        var hosts = scope.Hosts(fleet);
        var entries = new List<Change>();
        var repeats = new Dictionary<(string Host, string? Container, string Kind, string Message), int>();
        var read = 0;
        var unread = new List<string>();
        var partial = new List<string>();
        foreach (var host in hosts)
        {
            if (read == MaxHosts)
            {
                unread.Add(host.DisplayName);
                continue;
            }

            var timeline = await GetTimelineHandler.Handle(new GetTimelineQuery(host.HostId, null, (int)window.TotalSeconds, GetTimelineHandler.MaxLimit, SignificantOnly: true), snapshot, repository, cancellationToken);
            if (!timeline.IsSuccess)
            {
                unread.Add(host.DisplayName);
                continue;
            }

            read++;
            if (timeline.Value!.Note is not null) partial.Add(host.DisplayName);
            var started = host.Containers.ToDictionary(c => c.ContainerId, c => c.StartedAt);
            foreach (var entry in timeline.Value!.Entries)
            {
                if (entry.Kind != TimelineKind.Event)
                {
                    // A container's first minutes are all spikes (it loads, connects, warms up): what a start looks
                    // like, already said by its Started event.
                    if (entry.ContainerId is { } id && started.TryGetValue(id, out var since) && entry.Time < since + StartupGrace) continue;
                    entries.Add(new Change(entry.Time, entry.End, 1, host.DisplayName, entry.ContainerName, entry.Kind, entry.Level, entry.Message));
                    continue;
                }

                // Entries come in time order, so the first of a repeat keeps its place and the later ones extend it.
                var kind = entry.Metric ?? entry.Kind;
                var key = (host.DisplayName, entry.ContainerName, kind, kind == EventKind.Cpu ? "" : entry.Message);
                if (repeats.TryGetValue(key, out var index))
                {
                    entries[index] = entries[index] with { End = entry.Time, Count = entries[index].Count + 1, Message = entry.Message };
                }
                else
                {
                    repeats[key] = entries.Count;
                    entries.Add(new Change(entry.Time, null, 1, host.DisplayName, entry.ContainerName, kind, entry.Level, entry.Message));
                }
            }
        }

        var total = entries.Count;
        if (total > limit)
        {
            entries.Sort(static (a, b) => Rank(b.Level).CompareTo(Rank(a.Level)) is var byLevel and not 0 ? byLevel : b.Time.CompareTo(a.Time));
            entries.RemoveRange(limit, total - limit);
        }

        entries.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        var coverage = new List<string>(2);
        if (unread.Count > 0)
        {
            coverage.Add($"Only {read} of {hosts.Count} hosts were read (at most {MaxHosts} at once); not read: {string.Join(", ", unread.Take(10))}"
                + (unread.Count > 10 ? $" and {unread.Count - 10} more" : "") + ". Ask for each of them with its hostId.");
        }

        if (partial.Count > 0)
        {
            coverage.Add($"More events than could be read on {string.Join(", ", partial)}: the mildest and oldest there were left out.");
        }

        return new Changes((int)window.TotalSeconds, hosts.Count, read, total, total - entries.Count, entries,
            coverage.Count == 0 ? null : string.Join(" ", coverage));
    }

    private static int Rank(string level) => level switch { EventLevel.Error => 2, EventLevel.Warning => 1, _ => 0 };
}

// Who uses a host's CPU, memory and disk, and whether the host itself is short of them: what a container that is
// slow for no reason of its own is competing with.
public sealed record GetContentionQuery(string? HostId, string? ContainerId);

public sealed record ResourceUse(string Container, double Amount, double PercentOfHost, string Summary);

public sealed record HostContention(
    string Host,
    string? Container,
    string? ContainerState,
    bool HostSaturated,
    IReadOnlyList<string> HostPressure,
    string HostSummary,
    IReadOnlyList<ResourceUse> TopCpu,
    IReadOnlyList<ResourceUse> TopMemory,
    IReadOnlyList<ResourceUse> TopDisk,
    string Summary);

public static class GetContentionHandler
{
    private const int Top = 5;

    public static async Task<Result<HostContention>> Handle(GetContentionQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, query.HostId, query.ContainerId, required: true, cancellationToken);
        if (failure is not null) return failure;

        var host = scope.Host!;
        if (host.Latest?.Snapshot is not { } sample)
        {
            return Failure.NotFound($"Host {host.DisplayName} has no current sample: it is offline since {host.LastSeen:u}.");
        }

        var cores = sample.Cpu?.LogicalCores ?? 0;
        var memoryTotal = sample.Memory?.TotalBytes ?? 0;
        var pressure = new List<string>();
        if (sample.Cpu is { UsagePercent: >= 85 } cpu) pressure.Add(Text($"CPU is {cpu.UsagePercent:0} % busy across {cpu.LogicalCores} cores"));
        if (sample.Memory is { UsagePercent: >= 90 } memory) pressure.Add(Text($"memory is {memory.UsagePercent:0} % used"));
        if (sample.Pressure is { CpuSome: >= 25 } p) pressure.Add(Text($"tasks waited for CPU {p.CpuSome:0} % of the time"));
        if (sample.Pressure is { MemoryFull: >= 5 } m) pressure.Add(Text($"all tasks stalled on memory {m.MemoryFull:0} % of the time"));
        if (sample.Pressure is { IoFull: >= 10 } io) pressure.Add(Text($"all tasks stalled on disk I/O {io.IoFull:0} % of the time"));
        if (sample.Cpu?.Split is { IoWait: >= 20 } split) pressure.Add(Text($"{split.IoWait:0} % of CPU time waits on disk I/O"));
        if (sample.Cpu?.Split is { Steal: >= 10 } steal) pressure.Add(Text($"{steal.Steal:0} % of CPU time is taken by the hypervisor"));

        var running = host.Containers.Where(c => c.Usage is not null).ToList();
        var topCpu = running.OrderByDescending(c => c.Usage!.CpuCores).Take(Top)
            .Select(c => new ResourceUse(Label(c), Math.Round(c.Usage!.CpuCores, 2), Percent(c.Usage.CpuCores, cores),
                Text($"{Label(c)} uses {c.Usage.CpuCores:0.##} of the host's {cores} cores ({Percent(c.Usage.CpuCores, cores):0.#} %)")
                + (c.Usage.CpuLimitCores is > 0 and var limit ? Text($", limit {limit:0.##}") : ", no CPU limit"))).ToList();
        var topMemory = running.OrderByDescending(c => c.Usage!.MemoryBytes).Take(Top)
            .Select(c => new ResourceUse(Label(c), c.Usage!.MemoryBytes, Percent(c.Usage.MemoryBytes, memoryTotal),
                Text($"{Label(c)} uses {ComparePeriodsHandler.Format(c.Usage.MemoryBytes, "bytes")} ({Percent(c.Usage.MemoryBytes, memoryTotal):0.#} % of the host)")
                + (c.Usage.MemoryLimitBytes is > 0 and var limit ? $", limit {ComparePeriodsHandler.Format(limit, "bytes")}" : ", no memory limit"))).ToList();
        var topDisk = running.Where(c => c.Usage!.IoReadBytesPerSecond + c.Usage.IoWriteBytesPerSecond > 0)
            .OrderByDescending(c => c.Usage!.IoReadBytesPerSecond + c.Usage.IoWriteBytesPerSecond).Take(Top)
            .Select(c => new ResourceUse(Label(c), Math.Round(c.Usage!.IoReadBytesPerSecond + c.Usage.IoWriteBytesPerSecond), 0,
                $"{Label(c)} reads and writes {ComparePeriodsHandler.Format(c.Usage.IoReadBytesPerSecond + c.Usage.IoWriteBytesPerSecond, "bytes/s")}")).ToList();

        var hostSummary = Text($"CPU {sample.Cpu?.UsagePercent ?? 0:0} % of {cores} cores; memory {sample.Memory?.UsagePercent ?? 0:0} % used");
        var saturated = pressure.Count > 0;
        var biggest = topCpu.Count > 0 ? topCpu[0] : null;
        var summary = !saturated
            ? "The host is not short of CPU, memory or disk: a slow container here is not slowed by its neighbours. Look at its own limits and at what it calls."
            : biggest is { PercentOfHost: >= 40 }
                ? $"The host is under pressure, and {biggest.Container} alone uses {biggest.PercentOfHost:0} % of its CPU: the other containers on it compete with that."
                : "The host is under pressure, with no single container using most of it: the load is spread over the containers listed.";
        return new HostContention(host.DisplayName, scope.Container is { } c ? Label(c) : null,
            scope.Container is { } state ? TraceFailureHandler.State(state) : null, saturated, pressure, hostSummary, topCpu, topMemory, topDisk, summary);
    }

    private static string Label(ContainerState container) => container.Name ?? container.ContainerId;

    private static double Percent(double part, double whole) => whole > 0 ? Math.Round(part * 100 / whole, 1) : 0;

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

// One stated thing about one container, answered yes or no with the figures behind it: a cheap way to confirm or
// reject a guess before concluding.
public sealed record CheckQuery(string ContainerId, string Check);

public sealed record CheckResult(string Container, string Check, string Answer, string Evidence);

public static class CheckKind
{
    public const string Running = "running";
    public const string Restarting = "restarting";
    public const string OomKilled = "oomKilled";
    public const string MemoryNearLimit = "memoryNearLimit";
    public const string MemoryGrowing = "memoryGrowing";
    public const string CpuAtLimit = "cpuAtLimit";
    public const string DiskFilling = "diskFilling";
    public const string RequestsFailing = "requestsFailing";
    public const string RequestsSlow = "requestsSlow";
    public const string HostSaturated = "hostSaturated";
    public static readonly string[] All =
        [Running, Restarting, OomKilled, MemoryNearLimit, MemoryGrowing, CpuAtLimit, DiskFilling, RequestsFailing, RequestsSlow, HostSaturated];
}

public sealed class CheckKindsAttribute() : ChoicesAttribute(CheckKind.All);

public static class CheckHandler
{
    public const string Yes = "yes";
    public const string No = "no";
    public const string Unknown = "unknown";
    private const int MaxEventsRead = 1000;
    // Memory this close to its limit before a SIGKILL is read as the kernel stopping it there: samples come every few
    // seconds, so the last one before a kill can fall short of the limit itself.
    private const double OomPeakShare = 0.85;
    private static readonly TimeSpan EventWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan GrowthWindow = TimeSpan.FromMinutes(30);

    public static async Task<Result<CheckResult>> Handle(CheckQuery query, FleetSnapshot snapshot, NetworkReader networkReader, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (Validate.Id(query.Check, "check") is { } missing) return missing;
        if (Validate.OneOf(query.Check, "check", CheckKind.All) is { } invalid) return invalid;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, null, query.ContainerId, required: true, cancellationToken);
        if (failure is not null) return failure;

        var (host, container) = (scope.Host!, scope.Container!);
        var usage = container.Usage;
        var (answer, evidence) = query.Check switch
        {
            // Silence is not a stop: a container that stopped reporting without a recorded stop may still run.
            CheckKind.Running => (container.Status switch { ContainerStatus.Online => Yes, ContainerStatus.Offline => Unknown, _ => No },
                TraceFailureHandler.State(container)),
            CheckKind.Restarting => await RestartingAsync(),
            CheckKind.OomKilled => await OomKilledAsync(),
            CheckKind.MemoryGrowing => await MemoryGrowingAsync(),
            CheckKind.RequestsFailing or CheckKind.RequestsSlow => await RequestsAsync(query.Check == CheckKind.RequestsSlow),
            CheckKind.HostSaturated => await HostSaturatedAsync(),
            _ when usage is null => (Unknown, $"No current sample: the container is {container.Status} since {container.LastSeen:u}."),
            CheckKind.MemoryNearLimit => usage.MemoryLimitBytes is > 0 and var limit
                ? (usage.MemoryBytes >= limit * 0.9 ? Yes : No, Text($"Memory is {ComparePeriodsHandler.Format(usage.MemoryBytes, "bytes")} of its {ComparePeriodsHandler.Format(limit, "bytes")} limit ({usage.MemoryBytes * 100d / limit:0} %)."))
                : (No, $"It has no memory limit; it uses {ComparePeriodsHandler.Format(usage.MemoryBytes, "bytes")}."),
            CheckKind.CpuAtLimit => usage.CpuLimitCores is > 0 and var cpuLimit
                ? (usage.CpuCores >= cpuLimit * 0.9 || usage.Detail?.Throttling is { ThrottledPercent: >= 25 } ? Yes : No,
                    Text($"CPU is {usage.CpuCores:0.##} of its {cpuLimit:0.##}-core limit; held back by that limit {usage.Detail?.Throttling?.ThrottledPercent ?? 0:0} % of the time. The limit is the container's own setting: other containers do not cause this."))
                : (No, Text($"It has no CPU limit; it uses {usage.CpuCores:0.##} cores.")),
            _ => DiskFilling(usage),
        };

        return new CheckResult(container.Name ?? container.ContainerId, query.Check, answer, evidence);

        // Only this container's events of the kinds asked about are read, so a busy neighbour cannot push them out.
        Task<EventPage> EventsAsync(TimeSpan window, string[] kinds) =>
            repository.GetEventsAsync(new EventFilter([host.HostId], window, MaxEventsRead, container.ContainerId, Kinds: kinds), cancellationToken);

        async Task<(string, string)> RestartingAsync()
        {
            var (starts, ends, last) = (0, 0, (string?)null);
            var (events, truncated) = await EventsAsync(EventWindow, [EventKind.Lifecycle]);
            foreach (var e in events)
            {
                if (e.Message.StartsWith("Started", StringComparison.Ordinal)) starts++;
                else
                {
                    ends++;
                    last ??= $"{e.Message} at {e.Time:u}";
                }
            }

            return (starts >= 2 ? Yes : No, $"Started {starts} time(s) and ended {ends} time(s) in the last {EventWindow.TotalMinutes:0} min"
                + (last is null ? "." : $"; last end: {last}.") + $" Restarts since first seen: {container.Restarts}." + Truncated(truncated));
        }

        async Task<(string, string)> OomKilledAsync()
        {
            var (kills, limits, last) = (0, 0, (DateTimeOffset?)null);
            var (events, truncated) = await EventsAsync(TimeSpan.FromHours(1), [EventKind.Oom, EventKind.Memory]);
            foreach (var e in events)
            {
                if (e.Kind == EventKind.Oom) { kills++; last ??= e.Time; }
                else limits++;
            }

            var counter = usage?.Detail?.MemoryEvents?.OomKill ?? 0;
            var figures = $"In the last hour: {kills} out-of-memory kill event(s){(last is { } at ? $" (last at {at:u})" : "")} and {limits} time(s) at its memory limit. Kills counted since it started: {counter}.";
            if (kills > 0 || counter > 0) return (Yes, figures + Truncated(truncated));
            if (container.ExitSignal != 9) return (No, figures + Truncated(truncated));

            // A container killed at its limit as a whole often leaves no event: the kernel's SIGKILL is the stop, and
            // the agent inside dies with it. Memory that had climbed to the limit just before is the evidence then.
            if (limits > 0) return (Yes, figures + " Its last stop was by signal 9 (SIGKILL), after it reached its memory limit.");
            var peak = usage?.MemoryLimitBytes is > 0 and var limit && await PeakMemoryAsync() is { } highest ? (Bytes: highest, Limit: limit) : default;
            if (peak.Limit > 0 && peak.Bytes >= peak.Limit * OomPeakShare)
            {
                return (Yes, Text($"Most likely: its last stop was by signal 9 (SIGKILL), and in the last hour its memory reached {ComparePeriodsHandler.Format(peak.Bytes, "bytes")} of its {ComparePeriodsHandler.Format(peak.Limit, "bytes")} limit ({peak.Bytes * 100 / peak.Limit:0} %): that is how the kernel ends a container at its limit. No out-of-memory event was recorded for it."));
            }

            return (Unknown, figures + " Its last stop was by signal 9 (SIGKILL): the kernel's out-of-memory killer stops a container this way, but so does a forced stop, and its memory was not near its limit in the last hour's figures.");
        }

        async Task<double?> PeakMemoryAsync() =>
            await repository.GetPeakMemoryAsync(container.ContainerId, TimeSpan.FromHours(1), cancellationToken);

        async Task<(string, string)> MemoryGrowingAsync()
        {
            var history = await repository.GetContainerHistoryAsync(container.ContainerId, GrowthWindow, cancellationToken);
            var values = history.MemoryBytes;
            if (values.Length < 8) return (Unknown, "Too few samples in the last 30 min to tell.");
            var quarter = values.Length / 4;
            var first = values.AsSpan(0, quarter).ToArray().Average();
            var lastQuarter = values.AsSpan(values.Length - quarter).ToArray().Average();
            var growing = lastQuarter - first >= 16 << 20 && lastQuarter >= first * 1.2;
            return (growing ? Yes : No, Text($"Memory averaged {ComparePeriodsHandler.Format(first, "bytes")} at the start of the last {GrowthWindow.TotalMinutes:0} min and {ComparePeriodsHandler.Format(lastQuarter, "bytes")} at the end (highest {ComparePeriodsHandler.Format(values.Max(), "bytes")}).")
                + (container.Restarts > 0 ? " A container that restarts drops back each time, so growth before each restart shows as a high peak rather than a steady rise." : ""));
        }

        async Task<(string, string)> RequestsAsync(bool slow)
        {
            ContainerNetwork? network = null;
            foreach (var candidate in await networkReader.GetAsync(host.HostId, cancellationToken))
            {
                if (candidate.ContainerId == container.ContainerId) network = candidate;
            }

            if (network is null) return (Unknown, "No network activity is recorded for it: it is not running, or its agent cannot read its network.");
            var matches = new List<string>();
            var total = 0;
            foreach (var r in network.Network.Requests ?? [])
            {
                if (SoundingTraffic.IsOwn(r) || r.RequestsPerSecond < 0.02) continue;
                total++;
                var errorPercent = r.ErrorsPerSecond * 100 / r.RequestsPerSecond;
                if (slow ? r.P95Ms >= 1000 : errorPercent >= 5)
                {
                    matches.Add(Text($"{r.Protocol} {r.Operation} {(r.Role == "client" ? "calling" : "serving")} {r.PeerName ?? r.Peer}: {errorPercent:0.#} % errors, p95 {r.P95Ms ?? 0:0} ms at {r.RequestsPerSecond:0.##}/s"));
                }
            }

            return (matches.Count > 0 ? Yes : No, matches.Count > 0 ? string.Join("; ", matches.Take(5)) + "."
                : $"None of its {total} kinds of request in the last minute {(slow ? "has a p95 of 1 s or more" : "fails 5 % or more of the time")}.");
        }

        async Task<(string, string)> HostSaturatedAsync()
        {
            var contention = await GetContentionHandler.Handle(new GetContentionQuery(host.HostId, null), snapshot, repository, cancellationToken);
            if (!contention.IsSuccess) return (Unknown, contention.Error ?? "The host has no current sample.");
            var value = contention.Value!;
            return (value.HostSaturated ? Yes : No, $"Host {value.Host}: {value.HostSummary}." + (value.HostPressure.Count > 0 ? " " + string.Join("; ", value.HostPressure) + "." : "")
                + (value.TopCpu.Count > 0 ? $" Biggest CPU user: {value.TopCpu[0].Summary}." : ""));
        }
    }

    // A yes or no needs both the growth and the free space measured: without either, the agent could not read the
    // container's disk, which says nothing about whether it fills.
    private static (string, string) DiskFilling(ContainerUsage usage) => usage.Detail switch
    {
        null => (Unknown, "No disk detail is recorded for it: its agent could not read its filesystem, so whether its disk fills is not known."),
        { DiskGrowthBytesPerHour: null } or { DiskFreeBytes: null } =>
            (Unknown, "Its disk growth or free space is not measured (too few samples yet, or its filesystem could not be read), so whether its disk fills is not known."),
        { DiskGrowthBytesPerHour: <= 0 and var flat } => (No, Text($"Its disk use is not growing ({ComparePeriodsHandler.Format(flat, "bytes")}/h).")),
        { DiskGrowthBytesPerHour: { } growth, DiskFreeBytes: { } free } =>
            (free / growth < 48 ? Yes : No, Text($"Its disk grows {ComparePeriodsHandler.Format(growth, "bytes")}/h with {ComparePeriodsHandler.Format(free, "bytes")} free: full in about {free / growth:0} h.")),
    };

    private static string Truncated(bool truncated) => truncated
        ? $" More than {MaxEventsRead} such events in this window: only the newest {MaxEventsRead} were counted."
        : "";

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
