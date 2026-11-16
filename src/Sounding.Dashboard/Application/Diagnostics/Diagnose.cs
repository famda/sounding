using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.Diagnostics.Rules;
using Sounding.Dashboard.Application.Network;

namespace Sounding.Dashboard.Application.Diagnostics;

public static class Severity
{
    public const string Critical = "critical";
    public const string Warning = "warning";
    public const string Healthy = "healthy";

    public static int Rank(string severity) => severity switch
    {
        Critical => 2,
        Warning => 1,
        _ => 0,
    };
}

// The codes that rules other than the one producing them refer to. A code is part of the contract; a message is not.
public static class FindingCode
{
    public const string RequestsFailing = "requests-failing";
    public const string RequestsSlow = "requests-slow";
    public const string DnsFailed = "dns-failed";
    public const string ContainerStopped = "container-stopped";
    public const string ContainerKilled = "container-killed";
    public const string ContainerFailed = "container-failed";
    public const string ContainerNotReporting = "container-not-reporting";
    public const string ContainerOomKill = "container-oom-kill";
    public const string ContainerCrashLoop = "container-crash-loop";
    public const string ContainerCpuThrottled = "container-cpu-throttled";
    public const string EventLifecycle = "event-" + Events.EventKind.Lifecycle;
    public const string EventOom = "event-" + Events.EventKind.Oom;
    public const string EventCpu = "event-" + Events.EventKind.Cpu;

    public static string Event(string kind) => "event-" + kind;

    // About calls between services, as opposed to a problem of the container itself.
    public static bool IsAboutCalls(string code) => code is RequestsFailing or RequestsSlow;

    public static bool IsStop(string code) => code is ContainerStopped or ContainerKilled or ContainerFailed;
}

// What to look at next for a finding, as data: which kind of reading, of the finding's own host or container, with
// which options. Each transport says it its own way: the REST API as a link, the MCP server as a tool call.
public sealed record Lookup(string Kind, string? Sort = null, string? Metrics = null, string? Level = null, bool ProblemsOnly = false, int? Seconds = null);

public static class Look
{
    public const string EventsKind = "events";
    public const string TimelineKind = "timeline";
    public const string TrendsKind = "processTrends";
    public const string ProcessesKind = "processes";
    public const string MetricsKind = "metrics";
    public const string StorageKind = "storage";
    public const string LogsKind = "logs";
    public const string ConnectionsKind = "connections";
    public const string NetworkKind = "network";
    public const string TraceKind = "dependencyTrace";
    public const string ContainersKind = "hostContainers";
    public const string HostKind = "host";

    public static Lookup Events { get; } = new(EventsKind);
    public static Lookup Timeline { get; } = new(TimelineKind);
    public static Lookup Storage { get; } = new(StorageKind);
    public static Lookup Network { get; } = new(NetworkKind);
    public static Lookup Trace { get; } = new(TraceKind);
    public static Lookup Host { get; } = new(HostKind);

    public static Lookup Trends(string sort, int? seconds = null) => new(TrendsKind, Sort: sort, Seconds: seconds);

    public static Lookup Processes(string? sort = null) => new(ProcessesKind, Sort: sort);

    public static Lookup Metrics(string? metrics = null) => new(MetricsKind, Metrics: metrics);

    public static Lookup Logs(string level) => new(LogsKind, Level: level);

    public static Lookup Connections(bool problemsOnly = false) => new(ConnectionsKind, ProblemsOnly: problemsOnly);

    // The containers of the finding's host, biggest first by this measure.
    public static Lookup Containers(string sort) => new(ContainersKind, Sort: sort);
}

// What a finding is about, as data, for the rules that combine findings: they read these and never the message, so a
// message can be reworded without breaking them. CalledId: the monitored container a request finding is about.
// UnresolvedNames: the names a failed lookup could not resolve. Since: when a silent or stopped container was last
// heard from.
public sealed record FindingFacts(string? CalledId = null, IReadOnlyList<string>? UnresolvedNames = null, DateTimeOffset? Since = null);

public sealed record Finding(
    string Severity,
    string Code,
    string Target,
    string TargetId,
    string? TargetName,
    string HostId,
    string Message,
    IReadOnlyList<Lookup>? Next = null,
    string? Advice = null,
    string? Related = null,
    [property: System.Text.Json.Serialization.JsonIgnore] FindingFacts? Facts = null);

// Findings holds the most severe ones up to the limit; Critical and Warnings count them all, and Omitted says how many
// were left out. Gaps says what could not be checked in full, so "healthy" is never read as more than was examined.
public sealed record DiagnosisReport(
    DateTimeOffset At,
    string Status,
    int HostsChecked,
    int ContainersChecked,
    int EventWindowSeconds,
    int Critical,
    int Warnings,
    int Omitted,
    string Checked,
    IReadOnlyList<string> Measured,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<string> Gaps);

// Without a host or container, the whole current fleet is checked.
public sealed record DiagnoseQuery(string? HostId = null, string? ContainerId = null, int? EventSeconds = null, int? Limit = null);

// Reads what the rules need, runs each area of rules (Rules/), and puts the report together. A read or an area that
// fails is logged and listed in the report's gaps: the rest of the diagnosis still answers.
public static class DiagnoseHandler
{
    public const int DefaultEventSeconds = 900;
    public const int DefaultLimit = 30;
    public const int MaxLimit = 500;
    private const int HostsReadAtOnce = 4;
    // Process checks read two queries per host; across a large fleet they are left to a host or container diagnosis.
    private const int MaxHostsForProcessChecks = 10;
    private const int MaxEventsRead = 1000;
    // Containers leave the fleet view a few minutes after they stop; this keeps them in sight for longer.
    private static readonly TimeSpan StoppedLookback = TimeSpan.FromHours(6);

    private sealed record HostReads(
        IReadOnlyList<ContainerProcesses>? Live, IReadOnlyList<ContainerProcessWindow>? Window, IReadOnlyList<HostEvent> Events, bool EventsTruncated);

    public static async Task<Result<DiagnosisReport>> Handle(DiagnoseQuery query, FleetSnapshot snapshot, SoundingDatabase database, NetworkReader networkReader,
        FleetRepository repository, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (Validate.Limit(query.Limit, MaxLimit, out var limit, DefaultLimit) is { } badLimit) return badLimit;
        if (Validate.Window(query.EventSeconds, out var eventWindow, DefaultEventSeconds) is { } badWindow) return badWindow;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, query.HostId, query.ContainerId, false, cancellationToken);
        if (failure is not null) return failure;

        var guard = new Guard(loggers.CreateLogger(typeof(DiagnoseHandler)), cancellationToken);
        var hosts = scope.Hosts(fleet);
        var networkRead = guard.Read(networkReader.GetAsync(scope.Host?.HostId, cancellationToken), [], "Network");
        var stoppedRead = guard.Read(repository.GetStoppedContainersAsync(scope.Host?.HostId, StoppedLookback, cancellationToken), [], "Stopped containers");

        // Each host's reads are independent of the others': a few hosts at a time, each host's reads together.
        var withProcesses = hosts.Count <= MaxHostsForProcessChecks;
        var reads = new HostReads[hosts.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, hosts.Count),
            new ParallelOptions { MaxDegreeOfParallelism = HostsReadAtOnce, CancellationToken = cancellationToken },
            async (i, token) =>
            {
                var host = hosts[i];
                // Only what a check uses (warnings, errors and lifecycle events), filtered in the database and most
                // severe first, so routine events cannot push them out of the limit.
                var eventsRead = guard.Read(repository.GetEventsAsync(new EventFilter([host.HostId], eventWindow, MaxEventsRead, scope.Container?.ContainerId,
                    Significant: true, SevereFirst: true), token), new EventPage([], false), $"Events of host {host.DisplayName}");
                IReadOnlyList<ContainerProcesses>? live = null;
                IReadOnlyList<ContainerProcessWindow>? window = null;
                if (host.Online && withProcesses)
                {
                    var liveRead = guard.Read(repository.GetHostProcessesAsync(host.HostId, token), [], $"Processes of host {host.DisplayName}");
                    window = await guard.Read(repository.GetHostProcessWindowAsync(host.HostId, ProcessRules.GrowthWindow, token), [],
                        $"Process history of host {host.DisplayName}");
                    live = await liveRead;
                }

                var (events, truncated) = await eventsRead;
                // The rules read events newest first.
                var ordered = events as List<HostEvent> ?? [.. events];
                ordered.Sort(static (a, b) => b.Time.CompareTo(a.Time));
                reads[i] = new HostReads(live, window, ordered, truncated);
            });

        var labels = await guard.Read(ContainerLabels.ResolveAsync(fleet, repository,
            reads.SelectMany(r => r.Events).Select(e => e.ContainerId).OfType<string>(), cancellationToken), [], "Container names");
        var networks = await networkRead;
        var stopped = await stoppedRead;

        var findings = new List<Finding>();
        var containersChecked = 0;
        for (var i = 0; i < hosts.Count; i++)
        {
            var host = hosts[i];
            var read = reads[i];
            if (scope.Container is null) guard.Run($"Host {host.DisplayName}", () => HostRules.Check(host, findings));
            guard.Run($"Containers of host {host.DisplayName}", () =>
            {
                foreach (var container in host.Containers)
                {
                    if (!scope.Includes(container.ContainerId)) continue;
                    ContainerRules.Check(host, container, findings);
                    containersChecked++;
                }

                // A container no longer in the fleet view was found in the database: check how it ended.
                if (scope.Container is { } gone && host.Containers.All(c => c.ContainerId != gone.ContainerId))
                {
                    ContainerRules.Check(host, gone, findings);
                    containersChecked++;
                }
            });

            if (read is { Live: { } live, Window: { } window }) guard.Run($"Processes of host {host.DisplayName}", () => ProcessRules.Check(host, scope, live, window, fleet.Timestamp, findings));
            guard.Run($"Events of host {host.DisplayName}", () => EventRules.Check(host, scope.Container?.ContainerId, read.Events, labels, (int)eventWindow.TotalSeconds, findings));
        }

        guard.Run("Network", () =>
        {
            NetworkRules.Check(fleet, scope, networks, database, findings);
            NetworkRules.CheckUnanswered(networks, findings);
        });
        guard.Run("Stopped containers", () => StoppedRules.Check(fleet, scope, stopped, findings));
        guard.Run("Relating findings", () =>
        {
            FindingRelations.Dedupe(findings);
            FindingRelations.KeepWithImpact(findings);
            FindingRelations.Relate(fleet, findings);
        });
        var measured = new List<string>();
        guard.Run("Measurements", () => measured.AddRange(Measurements.Of(hosts, scope, networks)));

        findings.Sort(static (a, b) => Severity.Rank(b.Severity).CompareTo(Severity.Rank(a.Severity)));
        var status = findings.Count == 0 ? Severity.Healthy : findings[0].Severity;
        var critical = findings.Count(f => f.Severity == Severity.Critical);
        var omitted = Math.Max(0, findings.Count - limit);
        var total = findings.Count;
        if (omitted > 0) findings.RemoveRange(limit, omitted);

        var gaps = guard.Gaps;
        if (!withProcesses)
        {
            gaps.Add($"Processes were not checked: more than {MaxHostsForProcessChecks} hosts. Diagnose one host or container to check them.");
        }

        var truncated = new List<string>();
        for (var i = 0; i < hosts.Count; i++)
        {
            if (reads[i].EventsTruncated) truncated.Add(hosts[i].DisplayName);
        }

        if (truncated.Count > 0)
        {
            gaps.Add($"More than {MaxEventsRead} warnings, errors and lifecycle events in the window on {string.Join(", ", truncated.Take(5))}"
                + (truncated.Count > 5 ? $" and {truncated.Count - 5} more host(s)" : "") + ": only the most severe, newest first, were checked.");
        }

        return new DiagnosisReport(fleet.Timestamp, status, hosts.Count, containersChecked, (int)eventWindow.TotalSeconds,
            critical, total - critical, omitted,
            $"Checked {hosts.Count} host(s) and {containersChecked} container(s): whether each reports, limits, pressure, disks, "
            + (withProcesses ? "processes, " : "") + $"requests between services, connections, DNS, and the events of the last {eventWindow.TotalMinutes:0} min."
            + " Anything that recovered before that window is not in this diagnosis.",
            measured,
            findings,
            gaps);
    }

    // Keeps one failed read or one failing area of rules from failing the whole diagnosis: it is logged with its
    // exception, and the report says what was not checked. Cancellation still cancels.
    private sealed class Guard(ILogger logger, CancellationToken cancellationToken)
    {
        private readonly List<string> _gaps = [];

        // Read once every read and area has finished.
        public List<string> Gaps => _gaps;

        public async Task<T> Read<T>(Task<T> read, T fallback, string what)
        {
            try
            {
                return await read;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Fail(exception, $"{what} could not be read: not checked.", what);
                return fallback;
            }
        }

        public void Run(string what, Action rules)
        {
            try
            {
                rules();
            }
            catch (Exception exception)
            {
                Fail(exception, $"{what}: the checks failed and were skipped.", what);
            }
        }

        private void Fail(Exception exception, string gap, string what)
        {
            logger.LogError(exception, "Diagnosis step failed: {Step}", what);
            lock (_gaps) _gaps.Add(gap);
        }
    }
}
