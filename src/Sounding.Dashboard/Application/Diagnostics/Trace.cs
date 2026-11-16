using System.Globalization;
using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.Network;

namespace Sounding.Dashboard.Application.Diagnostics;

// Follows what a container calls, hop by hop, and puts next to each hop what is known about the service called: its
// state and its own findings. A reader then sees in one answer where along the chain a problem sits, instead of
// joining a dependency map with a diagnosis of every service on it.
public sealed record TraceFailureQuery(string ContainerId);

public sealed record TraceHop(
    int Depth,
    string From,
    string To,
    int Port,
    string? Protocol,
    string Calls,
    double? RequestsPerSecond,
    double? ErrorPercent,
    double? P95Ms,
    string? ToState,
    IReadOnlyList<string>? ToFindings);

// Unreachable: services shown not to answer (stopped, a name that no longer resolves, or calls to a silent one that
// fail). NotReporting: services that went silent with nothing showing whether they still answer. Coverage says what
// of the chain was not followed, so an empty answer is not read as proof.
public sealed record FailureTrace(
    string Target,
    string TargetState,
    IReadOnlyList<string> TargetFindings,
    IReadOnlyList<TraceHop> Hops,
    IReadOnlyList<string> Unreachable,
    IReadOnlyList<string> NotReporting,
    IReadOnlyList<string> CalledBy,
    string Furthest,
    string? Coverage = null);

public static class CallStatus
{
    public const string Ok = "ok";
    public const string Failing = "failing";
    public const string Slow = "slow";
    public const string NoRequests = "no decoded requests";
}

public static class TraceFailureHandler
{
    private const int MaxDepth = 4;
    private const int MaxHops = 30;
    private const int MaxFindingsPerService = 3;
    private static readonly TimeSpan StoppedLookback = TimeSpan.FromHours(6);

    public static async Task<Result<FailureTrace>> Handle(
        TraceFailureQuery query, FleetSnapshot snapshot, SoundingDatabase database, NetworkReader networkReader, FleetRepository repository, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, null, query.ContainerId, required: true, cancellationToken);
        if (failure is not null) return failure;

        var target = scope.Container!;
        var containers = new Dictionary<string, (HostState Host, ContainerState Container)>();
        foreach (var host in fleet.Hosts)
        {
            foreach (var container in host.Containers) containers[container.ContainerId] = (host, container);
        }

        containers.TryAdd(target.ContainerId, (scope.Host!, target));

        // Every edge, uncapped: the target's own calls are followed before any bound applies, so busy edges elsewhere
        // in the fleet cannot push them out.
        var networks = await networkReader.GetAsync(null, cancellationToken);
        var networkOf = new Dictionary<string, ContainerNetwork>(networks.Count);
        foreach (var network in networks) networkOf[network.ContainerId] = network;
        var edges = GetDependenciesHandler.Edges(fleet, networks);
        var outgoing = edges.Where(e => e.FromContainerId is not null).ToLookup(e => e.FromContainerId!);

        var hops = new List<(TraceHop Hop, string? CalleeId)>();
        var down = new List<string>();
        var downIds = new HashSet<string>();
        var silent = new Dictionary<string, string>();
        var depthOf = new Dictionary<string, int> { [target.ContainerId] = 0 };
        var queue = new Queue<string>();
        var notFollowed = false;
        queue.Enqueue(target.ContainerId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var depth = depthOf[current] + 1;
            var callerHost = containers[current].Host;
            foreach (var edge in outgoing[current])
            {
                if (hops.Count >= MaxHops || depth > MaxDepth)
                {
                    notFollowed = true;
                    break;
                }

                // A container that no longer reports has no address on record, so a call to it is only known by the
                // name the caller used: matched to a container on the caller's own host.
                var callee = edge.ToContainerId is { } id ? containers.GetValueOrDefault(id).Container
                    : PeerDirectory.Named(callerHost.Containers, static c => c.Name, edge.To);
                if (callee is { Status: ContainerStatus.Stopped } stopped)
                {
                    Once(down, $"{edge.From} calls {Name(stopped)}, which is {State(stopped)}.");
                    downIds.Add(stopped.ContainerId);
                }
                else if (callee is { Status: ContainerStatus.Offline } quiet)
                {
                    if (Unanswered(edge, networkOf.GetValueOrDefault(current)) is { } evidence)
                    {
                        Once(down, $"{edge.From} calls {Name(quiet)}, which stopped reporting at {quiet.LastSeen:u}, and those calls get no answer: {evidence}.");
                        downIds.Add(quiet.ContainerId);
                    }
                    else
                    {
                        silent.TryAdd(quiet.ContainerId, $"{Name(quiet)} stopped reporting at {quiet.LastSeen:u} with no stop or exit recorded; no failed or unanswered call to it was seen, so whether it still answers is not known.");
                    }
                }

                hops.Add((new TraceHop(depth, edge.From, edge.To, edge.Port, edge.RequestProtocol, Status(edge), edge.RequestsPerSecond, edge.ErrorPercent, edge.P95Ms,
                    callee is null ? null : State(callee), null), callee?.ContainerId));
                if (callee is not null && depthOf.TryAdd(callee.ContainerId, depth))
                {
                    containers.TryAdd(callee.ContainerId, (callerHost, callee));
                    queue.Enqueue(callee.ContainerId);
                }
            }
        }

        // The own findings of the services on the chain: a diagnosis of only the hosts it runs on.
        var own = new Dictionary<string, List<string>>();
        var findingsOmitted = false;
        foreach (var hostId in depthOf.Keys.Select(id => containers[id].Host.HostId).Distinct())
        {
            var diagnosis = await DiagnoseHandler.Handle(new DiagnoseQuery(hostId, null, null, DiagnoseHandler.MaxLimit), snapshot, database, networkReader, repository, loggers, cancellationToken);
            if (!diagnosis.IsSuccess) return diagnosis.AsFailure();
            findingsOmitted |= diagnosis.Value!.Omitted > 0;
            foreach (var finding in diagnosis.Value.Findings)
            {
                if (finding.Target != History.ResourceTarget.Container || FindingCode.IsAboutCalls(finding.Code)) continue;
                if (!own.TryGetValue(finding.TargetId, out var list)) own[finding.TargetId] = list = [];
                if (list.Count < MaxFindingsPerService) list.Add($"{finding.Severity}: {finding.Message}");
            }
        }

        var unreachable = await UnreachableAsync(depthOf.Keys, containers, networkOf, database, repository, cancellationToken);
        unreachable.InsertRange(0, down);
        var notReporting = silent.Where(s => !downIds.Contains(s.Key)).Select(s => s.Value).ToList();
        var calledBy = new List<string>();
        foreach (var edge in edges)
        {
            if (edge.ToContainerId == target.ContainerId) calledBy.Add($"{edge.From}: {Status(edge)}{Figures(edge)}");
        }

        var coverage = new List<string>(3);
        if (notFollowed) coverage.Add($"Not every call was followed: at most {MaxHops} calls, {MaxDepth} deep.");
        if (findingsOmitted) coverage.Add("Some findings of the services on the chain were left out (too many).");

        return new FailureTrace(Name(target), State(target), own.GetValueOrDefault(target.ContainerId) ?? [],
            [.. hops.Select(h => h.CalleeId is { } id && own.TryGetValue(id, out var theirs) ? h.Hop with { ToFindings = theirs } : h.Hop)],
            unreachable, notReporting, calledBy,
            Furthest(depthOf, containers, own, unreachable, notReporting, coverage.Count > 0),
            coverage.Count == 0 ? null : string.Join(" ", coverage));
    }

    // A fact about the chain, not a verdict: which service with a problem of its own is furthest from the target.
    // What sits before it may only be suffering from it; whether it explains the problem being investigated is still
    // for the reader to establish.
    private static string Furthest(Dictionary<string, int> depthOf, Dictionary<string, (HostState Host, ContainerState Container)> containers,
        Dictionary<string, List<string>> own, List<string> unreachable, List<string> notReporting, bool partial)
    {
        var deepest = -1;
        var names = new List<string>();
        foreach (var (id, depth) in depthOf)
        {
            if (!own.ContainsKey(id) || depth < deepest) continue;
            if (depth > deepest) names.Clear();
            deepest = depth;
            names.Add(containers.TryGetValue(id, out var c) ? Name(c.Container) : id);
        }

        var also = deepest > 0 ? $" Also with a problem of its own: {string.Join(", ", names)}." : "";
        if (unreachable.Count > 0)
        {
            return $"A service this chain calls cannot be reached: {unreachable[0]} Calls that need it fail with it: if the problem being investigated needs it, this is its cause. "
                + $"Sounding shows that it cannot be reached, not why: the reason is on its host, not in more Sounding calls.{also}";
        }

        if (notReporting.Count > 0)
        {
            return $"{notReporting[0]} Check it on its host: its silence alone does not show that it causes the problem.{also}";
        }

        var followed = partial ? " in the part of the chain that was followed (see coverage)" : "";
        return deepest switch
        {
            < 0 => $"No service{followed} has a finding of its own. If calls fail or are slow anyway, look at the last failing or slow hop.",
            0 => $"Only {names[0]} itself has findings of its own; nothing it calls{followed} does.",
            _ => $"The furthest service in the chain with a problem of its own is {string.Join(", ", names)} ({deepest} call(s) away). The services before it may only be affected by it: check it first.",
        };
    }

    // What shows that a silent service does not answer: calls to it failing, or the caller's own connections to it.
    private static string? Unanswered(DependencyEdge edge, ContainerNetwork? caller)
    {
        if (Status(edge) == CallStatus.Failing) return string.Create(CultureInfo.InvariantCulture, $"{edge.ErrorPercent:0.#} % of {edge.RequestsPerSecond:0.##}/s fail");
        return caller is null ? null : CallEvidence.Unanswered(caller, c => c.RemotePort == edge.Port
            && (c.RemoteAddress == edge.To || string.Equals(c.RemoteName, edge.To, StringComparison.OrdinalIgnoreCase)));
    }

    // A name that no longer resolves leaves no connection and so no hop: the failed lookup is the trace of the call,
    // and a single stopped container of that name on the caller's host explains it.
    private static async Task<List<string>> UnreachableAsync(IEnumerable<string> visited, Dictionary<string, (HostState Host, ContainerState Container)> containers,
        Dictionary<string, ContainerNetwork> networkOf, SoundingDatabase database, FleetRepository repository, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var stoppedByHost = new Dictionary<string, IReadOnlyList<(string HostId, ContainerState Container)>>();
        foreach (var id in visited)
        {
            if (!networkOf.TryGetValue(id, out var network)) continue;
            var (host, caller) = containers[id];
            foreach (var lookup in network.Network.Lookups)
            {
                if (lookup.Result == GetNetworkOverviewHandler.DnsAnswered || lookup.LastSeenSecondsAgo >= 300 || database.IsOwnLookup(lookup)) continue;
                var name = lookup.Name.TrimEnd('.');
                if (!stoppedByHost.TryGetValue(host.HostId, out var stopped))
                {
                    stoppedByHost[host.HostId] = stopped = await repository.GetStoppedContainersAsync(host.HostId, StoppedLookback, cancellationToken);
                }

                var candidates = new Dictionary<string, ContainerState>();
                foreach (var (_, container) in stopped) candidates.TryAdd(container.ContainerId, container);
                foreach (var container in host.Containers)
                {
                    if (container.Status != ContainerStatus.Online) candidates.TryAdd(container.ContainerId, container);
                }

                var gone = candidates.Values.Where(c => PeerDirectory.NameMatches(c.Name, name)).ToList();
                var text = $"{Name(caller)} cannot resolve \"{name}\" ({lookup.Result}, {lookup.LastSeenSecondsAgo:0} s ago)" + gone.Count switch
                {
                    0 => "; no stopped container of that name is known on its host.",
                    1 => $": container {Name(gone[0])} on its host is {gone[0].Status} since {gone[0].LastSeen:u}{Ended(gone[0])}.",
                    _ => $"; several containers on its host carried that name ({string.Join(", ", gone.Take(3).Select(Name))}), so which one is not known.",
                };
                if (!result.Contains(text)) result.Add(text);
            }
        }

        return result;
    }

    private static void Once(List<string> list, string text)
    {
        if (!list.Contains(text)) list.Add(text);
    }

    private static string Name(ContainerState container) => container.Name ?? container.ContainerId;

    private static string Ended(ContainerState container) =>
        container.ExitSignal is { } signal ? $" (signal {signal})" : container.ExitCode is { } code ? $" (exit code {code})" : "";

    private static string Status(DependencyEdge edge) =>
        edge.RequestsPerSecond is null ? CallStatus.NoRequests
        : edge.ErrorPercent >= 5 ? CallStatus.Failing
        : edge.P95Ms >= 1000 ? CallStatus.Slow
        : CallStatus.Ok;

    private static string Figures(DependencyEdge edge) => edge.RequestsPerSecond is null ? ""
        : string.Create(CultureInfo.InvariantCulture, $" ({edge.RequestsPerSecond:0.##}/s, {edge.ErrorPercent ?? 0:0.#} % errors, p95 {edge.P95Ms ?? 0:0} ms)");

    public static string State(ContainerState container)
    {
        if (container.Status == ContainerStatus.Offline)
        {
            return $"not reporting since {container.LastSeen:u} (no stop or exit recorded; whether it still runs and answers is not known from this alone)";
        }

        if (container.Status != ContainerStatus.Online) return $"{container.Status} since {container.LastSeen:u}{Ended(container)}";
        if (container.Usage is not { } usage) return "running";

        var cpu = usage.CpuLimitCores is > 0 and var limit
            ? string.Create(CultureInfo.InvariantCulture, $"CPU {usage.CpuCores:0.##} of {limit:0.##} cores")
            : string.Create(CultureInfo.InvariantCulture, $"CPU {usage.CpuCores:0.##} cores, no limit");
        var throttled = usage.Detail?.Throttling is { ThrottledPercent: >= 1 } throttling
            ? string.Create(CultureInfo.InvariantCulture, $", held back by its own CPU limit {throttling.ThrottledPercent:0} % of the time") : "";
        var memory = usage.MemoryLimitBytes is > 0 and var memoryLimit
            ? string.Create(CultureInfo.InvariantCulture, $"memory {usage.MemoryBytes * 100d / memoryLimit:0} % of its limit")
            : string.Create(CultureInfo.InvariantCulture, $"memory {usage.MemoryBytes / 1048576d:0} MiB, no limit");
        return $"running; {cpu}{throttled}; {memory}";
    }
}
