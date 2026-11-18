using Sounding.Dashboard.Application.Common;

namespace Sounding.Dashboard.Application.Network;

public sealed record GetNetworkQuery(string? HostId = null);

public sealed record GetContainerNetworkQuery(string ContainerId);

public sealed record GetNetworkOverviewQuery(string? HostId = null, string? ContainerId = null, int? Limit = null);

public sealed record GetContainerNetworkOverviewQuery(string ContainerId, int? Limit = null);

public sealed record GetConnectionsQuery(
    string ContainerId,
    string? Peer = null,
    int? Port = null,
    string? Process = null,
    bool ProblemsOnly = false,
    string? Sort = null,
    int? Limit = null,
    bool IncludeOwn = true);

// Who calls whom across the fleet, with the request rates, errors and latency each side observed.
public sealed record GetDependenciesQuery(string? HostId = null, string? ContainerId = null, int? Limit = null);

// A bounded view of each container's network for readers that cannot take every connection: the busiest peers,
// the slowest or failing request kinds, connections with problems and failed DNS lookups.
public sealed record ContainerNetworkOverview(
    string ContainerId,
    string? ContainerName,
    string? HostId,
    string Source,
    string? CaptureError,
    int ConnectionsTotal,
    IReadOnlyDictionary<string, int>? SocketStates,
    int? ListenBacklogLimit,
    IReadOnlyList<Listener> Listening,
    IReadOnlyList<Peer> TopPeers,
    IReadOnlyList<RequestStats> Requests,
    IReadOnlyList<Connection> ProblemConnections,
    IReadOnlyList<DnsLookup> FailedLookups,
    EphemeralPorts? Ephemeral,
    UntrackedTraffic? Untracked);

public sealed record ContainerConnections(
    string ContainerId,
    string? ContainerName,
    string Source,
    string? CaptureError,
    int ConnectionsTotal,
    int Matched,
    IReadOnlyList<Connection> Connections);

public sealed record DependencyEdge(
    string? FromContainerId,
    string From,
    string? ToContainerId,
    string To,
    int Port,
    string Protocol,
    bool Internet,
    double BytesPerSecond,
    long Bytes,
    double LastSeenSecondsAgo,
    string? RequestProtocol = null,
    string? ObservedBy = null,
    double? RequestsPerSecond = null,
    double? ErrorsPerSecond = null,
    double? ErrorPercent = null,
    double? P95Ms = null,
    IReadOnlyList<string>? Operations = null);

public sealed record Dependencies(string Scope, int TotalEdges, IReadOnlyList<DependencyEdge> Edges);

public static class ConnectionSort
{
    public const string Traffic = "traffic";
    public const string Rtt = "rtt";
    public const string Retransmits = "retransmits";
    public const string Queue = "queue";
    public const string Recent = "recent";
    public static readonly string[] All = [Traffic, Rtt, Retransmits, Queue, Recent];
}

public sealed class ConnectionSortsAttribute() : ChoicesAttribute(ConnectionSort.All);

public static class GetNetworkHandler
{
    public static async Task<Result<IReadOnlyList<ContainerNetwork>>> Handle(GetNetworkQuery query, FleetSnapshot snapshot, NetworkReader network, CancellationToken cancellationToken)
    {
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        if (FleetSnapshot.ResolveScope(fleet, query.HostId, null, required: false, out var scope) is { } failure) return failure;
        return Result<IReadOnlyList<ContainerNetwork>>.Ok(await network.GetAsync(scope.Host?.HostId, cancellationToken));
    }
}

public static class GetContainerNetworkHandler
{
    public static async Task<Result<ContainerNetwork>> Handle(GetContainerNetworkQuery query, FleetSnapshot snapshot, NetworkReader network, CancellationToken cancellationToken)
    {
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        if (FleetSnapshot.ResolveScope(fleet, null, query.ContainerId, required: true, out var scope) is { } failure) return failure;
        if (await Find(network, scope, cancellationToken) is { } found) return found;
        return NoActivity(scope.Container!);
    }

    public static async Task<ContainerNetwork?> Find(NetworkReader network, Scope scope, CancellationToken cancellationToken)
    {
        foreach (var candidate in await network.GetAsync(scope.Host!.HostId, cancellationToken))
        {
            if (candidate.ContainerId == scope.Container!.ContainerId) return candidate;
        }

        return null;
    }

    public static Failure NoActivity(ContainerState container) =>
        Failure.NotFound($"No network activity is recorded for container {container.Name ?? container.ContainerId}: it is not running, or its agent cannot read its network.");
}

public static class GetContainerNetworkOverviewHandler
{
    public static async Task<Result<ContainerNetworkOverview>> Handle(
        GetContainerNetworkOverviewQuery query, FleetSnapshot snapshot, SoundingDatabase database, NetworkReader network, CancellationToken cancellationToken)
    {
        var result = await GetNetworkOverviewHandler.Handle(new GetNetworkOverviewQuery(null, query.ContainerId, query.Limit), snapshot, database, network, cancellationToken);
        if (!result.IsSuccess) return result.AsFailure();
        if (result.Value!.Count > 0) return result.Value[0];
        return Failure.NotFound($"No network activity is recorded for container '{query.ContainerId}': it is not running, or its agent cannot read its network.");
    }
}

public static class GetNetworkOverviewHandler
{
    public const int MaxLimit = 50;
    public const string DnsAnswered = "NOERROR";

    public static async Task<Result<IReadOnlyList<ContainerNetworkOverview>>> Handle(
        GetNetworkOverviewQuery query, FleetSnapshot snapshot, SoundingDatabase database, NetworkReader network, CancellationToken cancellationToken)
    {
        if (Validate.Limit(query.Limit, MaxLimit, out var limit) is { } badLimit) return badLimit;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        if (FleetSnapshot.ResolveScope(fleet, query.HostId, query.ContainerId, required: false, out var scope) is { } failure) return failure;

        var names = NetworkNames.Of(fleet);
        var result = new List<ContainerNetworkOverview>();
        foreach (var container in await network.GetAsync(scope.Host?.HostId, cancellationToken))
        {
            if (!scope.Includes(container.ContainerId)) continue;
            var n = container.Network;
            var own = SoundingTraffic.OwnPeers(n);
            result.Add(new ContainerNetworkOverview(
                container.ContainerId,
                names.GetValueOrDefault(container.ContainerId),
                container.HostId,
                n.Source,
                n.CaptureError,
                n.ConnectionsTotal,
                n.SocketStates,
                n.ListenBacklogLimit,
                [.. n.Listening.Take(limit)],
                [.. (n.Peers ?? []).Where(p => !own.Contains((p.Address, p.Port))).OrderByDescending(p => p.BytesPerSecond).ThenByDescending(p => p.Bytes).Take(limit)],
                [.. (n.Requests ?? []).Where(r => !SoundingTraffic.IsOwn(r)).OrderByDescending(r => r.ErrorsPerSecond).ThenByDescending(r => r.P95Ms ?? 0).Take(limit)],
                [.. n.Connections.Where(c => c.Problems is { Count: > 0 } && !SoundingTraffic.IsOwn(c.Process)).Take(limit)],
                [.. n.Lookups.Where(l => l.Result != DnsAnswered && !database.IsOwnLookup(l)).Take(limit)],
                n.Ephemeral,
                n.Untracked));
        }

        return Result<IReadOnlyList<ContainerNetworkOverview>>.Ok(result);
    }
}

public static class GetConnectionsHandler
{
    public const int MaxLimit = 200;

    public static async Task<Result<ContainerConnections>> Handle(GetConnectionsQuery query, FleetSnapshot snapshot, NetworkReader reader, CancellationToken cancellationToken)
    {
        if (Validate.OneOf(query.Sort, "sort", ConnectionSort.All) is { } badSort) return badSort;
        if (Validate.Limit(query.Limit, MaxLimit, out var limit, 50) is { } badLimit) return badLimit;
        if (query.Port is < 1 or > 65535) return Failure.Invalid("port", "'port' must be between 1 and 65535.");
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        if (FleetSnapshot.ResolveScope(fleet, null, query.ContainerId, required: true, out var scope) is { } failure) return failure;

        var container = scope.Container!;
        if (await GetContainerNetworkHandler.Find(reader, scope, cancellationToken) is not { } network) return GetContainerNetworkHandler.NoActivity(container);

        var matched = new List<Connection>();
        foreach (var c in network.Network.Connections)
        {
            if (query.ProblemsOnly && c.Problems is not { Count: > 0 }) continue;
            if (!query.IncludeOwn && SoundingTraffic.IsOwn(c.Process)) continue;
            if (query.Port is { } port && c.RemotePort != port && c.LocalPort != port) continue;
            if (query.Process is { } process && c.Process?.Contains(process, StringComparison.OrdinalIgnoreCase) != true) continue;
            if (query.Peer is { } peer
                && !c.RemoteAddress.Contains(peer, StringComparison.OrdinalIgnoreCase)
                && c.RemoteName?.Contains(peer, StringComparison.OrdinalIgnoreCase) != true
                && c.TlsName?.Contains(peer, StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            matched.Add(c);
        }

        matched.Sort(Comparer(query.Sort ?? ConnectionSort.Traffic));
        var n = network.Network;
        return new ContainerConnections(container.ContainerId, container.Name, n.Source, n.CaptureError, n.ConnectionsTotal, matched.Count, [.. matched.Take(limit)]);
    }

    private static Comparison<Connection> Comparer(string sort) => sort switch
    {
        ConnectionSort.Rtt => static (a, b) => Nullable.Compare(b.RttMs ?? b.HandshakeMs, a.RttMs ?? a.HandshakeMs),
        ConnectionSort.Retransmits => static (a, b) => Nullable.Compare(b.RetransmitsOut + b.RetransmitsIn, a.RetransmitsOut + a.RetransmitsIn),
        ConnectionSort.Queue => static (a, b) => Nullable.Compare(b.SendQueueBytes + b.ReceiveQueueBytes, a.SendQueueBytes + a.ReceiveQueueBytes),
        ConnectionSort.Recent => static (a, b) => a.FirstSeenSecondsAgo.CompareTo(b.FirstSeenSecondsAgo),
        _ => static (a, b) => ((b.OutBytesPerSecond ?? 0) + (b.InBytesPerSecond ?? 0)).CompareTo((a.OutBytesPerSecond ?? 0) + (a.InBytesPerSecond ?? 0)),
    };
}

public static class GetDependenciesHandler
{
    public const int MaxLimit = 500;
    private const string Client = "client";
    private const string Server = "server";
    private const string Both = "both";

    public static async Task<Result<Dependencies>> Handle(GetDependenciesQuery query, FleetSnapshot snapshot, NetworkReader reader, CancellationToken cancellationToken)
    {
        if (Validate.Limit(query.Limit, MaxLimit, out var limit, 100) is { } badLimit) return badLimit;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        if (FleetSnapshot.ResolveScope(fleet, query.HostId, query.ContainerId, required: false, out var scope) is { } failure) return failure;

        // Every container's network is read, even for one container's view: its callers are other containers.
        var networks = await reader.GetAsync(null, cancellationToken);
        var all = Edges(fleet, networks);
        var hostOf = new Dictionary<string, string?>(networks.Count);
        foreach (var network in networks) hostOf[network.ContainerId] = network.HostId;

        var result = new List<DependencyEdge>();
        foreach (var edge in all)
        {
            if (!scope.IsFleet && !Touches(edge.FromContainerId) && !Touches(edge.ToContainerId)) continue;
            result.Add(edge);
        }

        return new Dependencies(scope.Describe(), result.Count, [.. result.Take(limit)]);

        bool Touches(string? containerId) => containerId is not null
            && (scope.Container is { } c ? c.ContainerId == containerId : hostOf.GetValueOrDefault(containerId) == scope.Host!.HostId);
    }

    // Every edge between the containers whose networks were read, failing first, then busiest. An end is a container
    // only when it owns the observed address (see PeerDirectory), never because of a name: a Kubernetes Service stays
    // an end of its own, named as the caller called it, whatever pods answer it.
    //
    // Each side reports what it saw. The client's and the server's views are merged only when they name the same edge
    // (both ends, direction, transport and port), and then the client's figures are shown, not their sum. A call
    // through a Service is two edges: the client's, to the Service, which keeps every failure the client saw, and each
    // answering server's, from the client, with what that server saw. They overlap; ObservedBy tells which side saw each.
    public static List<DependencyEdge> Edges(FleetState fleet, IReadOnlyList<ContainerNetwork> networks)
    {
        var names = NetworkNames.Of(fleet);
        var peers = new PeerDirectory(networks);
        var ownOf = new Dictionary<string, HashSet<(string Address, int Port)>>(networks.Count);
        foreach (var network in networks) ownOf[network.ContainerId] = SoundingTraffic.OwnPeers(network.Network);

        string Label(string? containerId, string fallback) => containerId is null ? fallback : names.GetValueOrDefault(containerId) ?? containerId;

        var edges = new Dictionary<(string From, string To, int Port, string Protocol), EdgeBuilder>();
        EdgeBuilder Edge(string? fromId, string fromLabel, string? toId, string toLabel, int port, string protocol, bool internet)
        {
            var key = (fromId ?? fromLabel, toId ?? toLabel, port, protocol);
            if (!edges.TryGetValue(key, out var edge)) edges[key] = edge = new EdgeBuilder(fromId, fromLabel, toId, toLabel, port, protocol, internet);
            return edge;
        }

        // What a server sees arriving from a monitored caller is Sounding's own only when the caller's processes show
        // it: every one of the caller's connections to this server's address and port belongs to Sounding.
        bool CallerOwn(string callerId, ContainerNetwork server, int port)
        {
            if (!ownOf.TryGetValue(callerId, out var own) || own.Count == 0) return false;
            foreach (var address in server.Network.Addresses ?? [])
            {
                if (own.Contains((address, port))) return true;
            }

            return false;
        }

        foreach (var network in networks)
        {
            var self = network.ContainerId;
            var selfLabel = Label(self, self);
            var n = network.Network;
            var own = ownOf[self];
            foreach (var peer in n.Peers ?? [])
            {
                if (own.Contains((peer.Address, peer.Port))) continue;
                var other = peers.ContainerAt(network.HostId, peer.Address, n.ClusterNetworks);
                if (peer.Inbound)
                {
                    if (other is not null && CallerOwn(other, network, peer.Port)) continue;
                    Edge(other, Label(other, peer.Name ?? peer.Address), self, selfLabel, peer.Port, peer.Protocol, peer.Internet).Observe(peer, Server);
                }
                else
                {
                    Edge(self, selfLabel, other, Label(other, peers.Called(n, peer.Address, peer.Name)), peer.Port, peer.Protocol, peer.Internet)
                        .Observe(peer, Client);
                }
            }

            foreach (var request in n.Requests ?? [])
            {
                if (SoundingTraffic.IsOwn(request)) continue;
                var other = peers.ContainerAt(network.HostId, request.Peer, n.ClusterNetworks);
                if (request.Role == Client)
                {
                    Edge(self, selfLabel, other, Label(other, peers.Called(n, request.Peer, request.PeerName)), request.Port, "tcp", false)
                        .Add(request, Client);
                }
                else
                {
                    if (other is not null && CallerOwn(other, network, request.Port)) continue;
                    Edge(other, Label(other, request.PeerName ?? request.Peer), self, selfLabel, request.Port, "tcp", false).Add(request, Server);
                }
            }
        }

        var result = new List<DependencyEdge>(edges.Count);
        foreach (var edge in edges.Values) result.Add(edge.Build());
        result.Sort(static (a, b) => b.ErrorsPerSecond.GetValueOrDefault().CompareTo(a.ErrorsPerSecond.GetValueOrDefault()) is var byErrors and not 0
            ? byErrors : b.BytesPerSecond.CompareTo(a.BytesPerSecond));
        return result;
    }

    private sealed class EdgeBuilder(string? fromId, string from, string? toId, string to, int port, string protocol, bool internet)
    {
        // Each side's view, kept apart: the client's counts what failed to reach the server, the server's what arrived.
        private readonly List<RequestStats> _clientRequests = [];
        private readonly List<RequestStats> _serverRequests = [];
        private Traffic _clientTraffic;
        private Traffic _serverTraffic;
        private double _lastSeen = double.MaxValue;

        public void Observe(Peer peer, string side)
        {
            ref var traffic = ref side == Client ? ref _clientTraffic : ref _serverTraffic;
            traffic = new Traffic(true, traffic.Rate + peer.BytesPerSecond, traffic.Bytes + peer.Bytes);
            _lastSeen = Math.Min(_lastSeen, peer.LastSeenSecondsAgo);
        }

        public void Add(RequestStats request, string side) => (side == Client ? _clientRequests : _serverRequests).Add(request);

        public DependencyEdge Build()
        {
            var byClient = _clientTraffic.Seen || _clientRequests.Count > 0;
            var byServer = _serverTraffic.Seen || _serverRequests.Count > 0;
            var traffic = _clientTraffic.Seen ? _clientTraffic : _serverTraffic;
            var requests = _clientRequests.Count > 0 ? _clientRequests : _serverRequests;
            double? rps = null, errors = null, errorPercent = null, p95 = null;
            string? requestProtocol = null;
            IReadOnlyList<string>? operations = null;
            if (requests.Count > 0)
            {
                rps = Math.Round(requests.Sum(r => r.RequestsPerSecond), 2);
                errors = Math.Round(requests.Sum(r => r.ErrorsPerSecond), 2);
                errorPercent = rps > 0 ? Math.Round(errors.Value * 100 / rps.Value, 1) : null;
                p95 = requests.Max(r => r.P95Ms);
                requestProtocol = requests[0].Protocol;
                operations = [.. requests.OrderByDescending(r => r.RequestsPerSecond).Take(5)
                    .Select(r => $"{r.Operation}: {r.RequestsPerSecond:0.##}/s, {r.ErrorsPerSecond:0.##} err/s, p95 {r.P95Ms?.ToString("0") ?? "?"} ms")];
            }

            return new DependencyEdge(fromId, from, toId, to, port, protocol, internet, Math.Round(traffic.Rate),
                traffic.Bytes, _lastSeen == double.MaxValue ? 0 : _lastSeen, requestProtocol,
                byClient && byServer ? Both : byClient ? Client : Server, rps, errors, errorPercent, p95, operations);
        }

        private readonly record struct Traffic(bool Seen, double Rate, long Bytes);
    }
}

internal static class NetworkNames
{
    public static Dictionary<string, string?> Of(FleetState fleet)
    {
        var names = new Dictionary<string, string?>();
        foreach (var host in fleet.Hosts)
        {
            foreach (var container in host.Containers) names[container.ContainerId] = container.Name;
        }

        return names;
    }
}
