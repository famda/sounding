namespace Sounding.Shared;

public sealed record TrafficSplit(
    double InternetOutBytesPerSecond,
    double InternetInBytesPerSecond,
    double InternalOutBytesPerSecond,
    double InternalInBytesPerSecond,
    double? NetworkOutBytesPerSecond = null,
    double? NetworkInBytesPerSecond = null);

public sealed record NetworkActivity(
    string Source,
    string? CaptureError,
    IReadOnlyList<Connection> Connections,
    int ConnectionsTotal,
    IReadOnlyList<Listener> Listening,
    IReadOnlyList<DnsLookup> Lookups,
    IReadOnlyDictionary<string, int>? SocketStates = null,
    int? ListenBacklogLimit = null,
    IReadOnlyList<string>? Addresses = null,
    IReadOnlyList<RequestStats>? Requests = null,
    EphemeralPorts? Ephemeral = null,
    IReadOnlyList<string>? Networks = null,
    UntrackedTraffic? Untracked = null,
    IReadOnlyList<Peer>? Peers = null);

// Who this container talked to over the retention period, one entry per peer, service port and direction, however
// many connections that took: what the map draws for any time range. Internet peers are one address, "*".
public sealed record Peer(
    string Protocol,
    string Address,
    string? Name,
    int Port,
    bool Inbound,
    bool Sure,
    string? Zone,
    bool Internet,
    long Bytes,
    double BytesPerSecond,
    double FirstSeenSecondsAgo,
    double LastSeenSecondsAgo);

// Traffic of connections beyond those net-meter can follow at once: counted, but in no connection's figures.
public sealed record UntrackedTraffic(double OutBytesPerSecond, double InBytesPerSecond, long OutBytes, long InBytes);

// Requests decoded from the packets (HTTP, PostgreSQL, MySQL, Redis, MongoDB) over the last minute, per peer and operation.
public sealed record RequestStats(
    string Protocol,
    string Role,
    string Peer,
    string? PeerName,
    int Port,
    string Operation,
    double RequestsPerSecond,
    double ErrorsPerSecond,
    double? P50Ms,
    double? P95Ms,
    double? RequestBytes,
    double? ResponseBytes,
    double? RequestsPerConnection,
    string? Process = null);

// The busiest destination's share of the local port range: new connections fail when it runs out.
public sealed record EphemeralPorts(double UsedPercent, int InUse, int Range, string Destination);

public sealed record Connection(
    string Protocol,
    string RemoteAddress,
    int RemotePort,
    string? RemoteName,
    int LocalPort,
    bool Internet,
    bool NewDestination,
    long? BytesOut,
    long? BytesIn,
    double? OutBytesPerSecond,
    double? InBytesPerSecond,
    double FirstSeenSecondsAgo,
    double LastSeenSecondsAgo,
    string? State,
    int? Pid,
    string? Process,
    string? User,
    double? HandshakeMs = null,
    long? RetransmitsOut = null,
    long? RetransmitsIn = null,
    string? TlsName = null,
    string? HttpRequest = null,
    int? HttpStatus = null,
    double? HttpResponseMs = null,
    string? TlsVersion = null,
    string? Alpn = null,
    string? AlpnOffered = null,
    long? SendQueueBytes = null,
    long? ReceiveQueueBytes = null,
    string? Zone = null,
    double? RttMs = null,
    double? DeliveryBytesPerSecond = null,
    string? LimitedBy = null,
    IReadOnlyList<string>? Problems = null);

public sealed record Listener(
    string Protocol, string Address, int Port, bool New, int? Pid, string? Process, string? User, int? AcceptQueue = null,
    int? Clients = null, int? InternetClients = null, int? HostClients = null, IReadOnlyList<int>? HostPorts = null);

public sealed record DnsLookup(
    string Name,
    string Result,
    IReadOnlyList<string> Answers,
    double FirstSeenSecondsAgo,
    double LastSeenSecondsAgo,
    double? LatencyMs = null,
    IReadOnlyList<string>? Aliases = null);
