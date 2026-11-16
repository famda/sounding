using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.History;
using Sounding.Dashboard.Application.Network;

namespace Sounding.Dashboard.Application.Diagnostics.Rules;

// What each container's network shows: requests failing or slow, local ports and accept queues running out,
// connections with problems, and failed DNS lookups; and whether a container that stopped reporting still answers.
internal static class NetworkRules
{
    public static void Check(FleetState fleet, Scope scope, IReadOnlyList<ContainerNetwork> networks, SoundingDatabase database, List<Finding> findings)
    {
        var owners = new Dictionary<string, (HostState Host, ContainerState Container)>();
        foreach (var host in fleet.Hosts)
        {
            foreach (var container in host.Containers) owners[container.ContainerId] = (host, container);
        }

        var peers = new PeerDirectory(networks);
        foreach (var network in networks)
        {
            if (!scope.Includes(network.ContainerId) || !owners.TryGetValue(network.ContainerId, out var owner)) continue;
            if (!scope.IncludesHost(owner.Host.HostId)) continue;
            var hostId = owner.Host.HostId;
            var n = network.Network;
            void Add(string severity, string code, string message, Lookup[]? next = null, FindingFacts? facts = null) =>
                findings.Add(new Finding(severity, code, ResourceTarget.Container, network.ContainerId, owner.Container.Name,
                    owner.Host.HostId, message, next ?? [Look.Network], Facts: facts));
            Lookup[] trace = [Look.Trace];

            // One finding per peer and kind of trouble, listing the operations: a service that is down fails every
            // operation that goes to it, and a finding for each of them buries the others.
            var requests = new Dictionary<(string Code, string Side, string? CalledId), (string Severity, List<string> Operations)>();
            void Collect(string code, string severity, string side, string? calledId, string operation)
            {
                var key = (code, side, calledId);
                if (!requests.TryGetValue(key, out var group)) group = (severity, []);
                group.Operations.Add(operation);
                requests[key] = (Severity.Rank(severity) > Severity.Rank(group.Severity) ? severity : group.Severity, group.Operations);
            }

            foreach (var r in n.Requests ?? [])
            {
                // Low-traffic services matter too: a few failing requests a minute is still every request failing.
                if (SoundingTraffic.IsOwn(r) || r.RequestsPerSecond < 0.01) continue;
                // Both ends of a call between two monitored containers see it: across a host or the fleet the caller
                // reports it, so one failing call is one finding.
                var monitored = peers.ContainerAt(hostId, r.Peer);
                if (scope.Container is null && r.Role != "client" && monitored is not null) continue;
                // A caller also names the service it calls: one that stopped reporting has no address on record, but
                // keeps the name the caller used.
                var calledId = monitored ?? (r.Role == "client" && r.PeerName is { } peerName
                    ? PeerDirectory.Named(owner.Host.Containers, static c => c.Name, peerName)?.ContainerId
                    : null);
                var peer = calledId is not null && owners.TryGetValue(calledId, out var called) ? called.Container.Name ?? calledId : r.PeerName ?? r.Peer;
                var side = r.Role == "client" ? $"calling {peer}:{r.Port}" : $"serving {peer}";
                var errorPercent = r.ErrorsPerSecond * 100 / r.RequestsPerSecond;
                if (errorPercent >= 5 && r.ErrorsPerSecond >= 0.01)
                {
                    Collect(FindingCode.RequestsFailing, errorPercent >= 25 ? Severity.Critical : Severity.Warning, side, calledId,
                        $"{r.Protocol} {r.Operation} ({errorPercent:0.#} % of {r.RequestsPerSecond:0.##}/s fail)");
                }
                else if (r.P95Ms is >= 1000 and var p95)
                {
                    Collect(FindingCode.RequestsSlow, Severity.Warning, side, calledId,
                        $"{r.Protocol} {r.Operation} (p95 {p95:0} ms at {r.RequestsPerSecond:0.##}/s)");
                }
            }

            foreach (var ((code, side, calledId), (severity, operations)) in requests)
            {
                var what = code == FindingCode.RequestsFailing ? "failing" : "slow";
                Add(severity, code, $"Requests {side} are {what}: {string.Join("; ", operations.Take(4))}"
                    + (operations.Count > 4 ? $"; and {operations.Count - 4} more" : "") + ".", trace, calledId is null ? null : new FindingFacts(CalledId: calledId));
            }

            if (n.Ephemeral is { UsedPercent: >= 80 } ephemeral)
            {
                Add(ephemeral.UsedPercent >= 95 ? Severity.Critical : Severity.Warning, "ephemeral-ports",
                    $"{ephemeral.UsedPercent:0} % of local ports to {ephemeral.Destination} are in use ({ephemeral.InUse} of {ephemeral.Range}): new connections will fail. Reuse connections (pooling, keep-alive).");
            }

            foreach (var listener in n.Listening)
            {
                if (listener.AcceptQueue is > 0 and var queue && n.ListenBacklogLimit is > 0 and var backlog && queue * 10 >= backlog * 8)
                {
                    Add(Severity.Warning, "listen-backlog", $"Port {listener.Port} ({listener.Process}) has {queue} connections waiting to be accepted of a backlog of {backlog}: the application accepts too slowly.");
                }
            }

            // Only what is happening now: a connection refused while a neighbour was still starting stays listed for
            // minutes after it stopped mattering.
            var problems = n.Connections.Where(c => c.Problems is { Count: > 0 } && c.LastSeenSecondsAgo <= 60 && !SoundingTraffic.IsOwn(c.Process)).ToList();
            if (problems.Count >= 3)
            {
                var common = problems.SelectMany(c => c.Problems!).GroupBy(p => p).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} ({g.Count()})");
                Add(Severity.Warning, "connection-problems", $"{problems.Count} connections show problems: {string.Join(", ", common)}.",
                    [Look.Connections(problemsOnly: true)]);
            }

            var failed = n.Lookups.Where(l => l.Result != GetNetworkOverviewHandler.DnsAnswered && l.LastSeenSecondsAgo < 300 && !database.IsOwnLookup(l)).Take(5).ToList();
            if (failed.Count > 0)
            {
                Add(Severity.Warning, FindingCode.DnsFailed, $"DNS lookups failing in the last 5 min: {string.Join(", ", failed.Select(l => $"{l.Name} ({l.Result})"))}.", trace,
                    new FindingFacts(UnresolvedNames: [.. failed.Select(l => l.Name)]));
            }
        }
    }

    // A container that stopped reporting is shown not to answer by the requests its callers on the same host sent it
    // and never got an answer to: then it is down for them, not only silent.
    public static void CheckUnanswered(IReadOnlyList<ContainerNetwork> networks, List<Finding> findings)
    {
        for (var i = 0; i < findings.Count; i++)
        {
            if (findings[i] is not { Code: FindingCode.ContainerNotReporting, TargetName: { } name, Facts.Since: { } since } silent) continue;
            var callers = 0;
            string? evidence = null;
            foreach (var network in networks)
            {
                if (network.HostId != silent.HostId || network.ContainerId == silent.TargetId) continue;
                if (CallEvidence.Unanswered(network, c => c.RemoteName is { } remote && PeerDirectory.NameMatches(name, remote)) is not { } seen) continue;
                callers++;
                evidence ??= seen;
            }

            if (evidence is null) continue;
            findings[i] = silent with
            {
                Severity = Severity.Critical,
                Message = DiagnosisText.NotAnswering(since, $"{callers} container(s) that call it got no answer: {evidence}"),
                Next = null,
                Advice = DiagnosisText.NotAnsweringWhy,
            };
        }
    }
}
