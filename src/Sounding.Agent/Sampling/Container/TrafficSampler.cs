using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Sounding.Agent.Sampling;

public sealed class TrafficSampler(IOptions<AgentOptions> options, ILogger<TrafficSampler> logger) : IDisposable
{
    private const int MaxListed = 200;
    private const int MaxMetadata = 8192;
    private const int MaxPeers = 512;
    private const int FirstEphemeralPort = 32768;

    private static readonly TimeSpan FlowExpiry = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan NewWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan WarmUp = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryCapture = TimeSpan.FromMinutes(1);

    private readonly string _helperPath = options.Value.NetMeterPath;
    private readonly string _procPath = options.Value.ProcPath;
    private readonly string _procsPath = Path.Combine(options.Value.CgroupPath, "cgroup.procs");
    private string[] _scannedPids = [];
    private HashSet<long> _scannedInodes = [];
    private Dictionary<long, int> _socketPids = [];
    private readonly Lock _lock = new();
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly LocalNetwork _local = new(options.Value.ProcPath);
    private readonly PublishedPorts _published = new(options.Value.ProcPath, options.Value.HostProcPath);
    private readonly RequestTracker _requests = new();
    private readonly List<AgentEvent> _events = [];
    private PacketCapture? _capture;
    private PacketCapture? _captured;

    private static readonly (string File, string Protocol)[] SocketTables = [("tcp", "tcp"), ("tcp6", "tcp"), ("udp", "udp"), ("udp6", "udp")];

    // The few users that own sockets, each as one string. Only used from the sampling loop.
    private static readonly NamePool SocketUids = new();

    private readonly Dictionary<FlowKey, Flow> _flows = [];
    // Flows come and go by the thousand on a busy service; who it talks to is kept as long as samples are.
    private readonly Dictionary<PeerKey, PeerLink> _peers = [];
    private readonly TimeSpan _peerExpiry = TimeSpan.FromMinutes(Math.Max(1, options.Value.RetentionMinutes));
    private readonly ExpiringCache<(string Protocol, string Remote, int Port), DateTime> _destinations = new(MaxMetadata, FlowExpiry); // first contact
    private readonly ExpiringCache<(string Protocol, string Address, int Port), DateTime> _listeners = new(MaxMetadata, FlowExpiry); // first seen
    private readonly ExpiringCache<string, string?> _names = new(MaxMetadata, FlowExpiry); // IP -> DNS name
    private readonly ExpiringCache<string, Lookup?> _lookups = new(MaxMetadata, FlowExpiry); // latest reply

    private Process? _helper;
    private DateTime _captureStarted;
    private DateTime _retryAt = DateTime.MinValue;
    private string? _captureError;
    private long _previousTimestamp;

    private readonly record struct FlowKey(string Protocol, int LocalPort, string Remote, int RemotePort);

    private readonly record struct PeerKey(string Protocol, string Remote, int Port, bool Inbound);

    private sealed class PeerLink
    {
        public DateTime FirstSeen, LastSeen, RateAt;
        public long Bytes;
        public double Rate;
        public string Zone = "";
        public bool Internet, Sure;
    }

    private sealed class Flow
    {
        public required string Protocol, Remote;
        public required int LocalPort, RemotePort;
        // What the remote address says of it, worked out once; Zone is this tick's.
        public required bool Internet;
        public required uint? RemoteV4;
        public string Zone = "";
        public long? BytesOut, BytesIn;
        public long PreviousOut, PreviousIn;
        public double? OutRate, InRate;
        public DateTime FirstSeen, LastSeen;
        public string? State, Process, User;
        public int? Pid;
        public double? HandshakeMs;
        public long? RetransmitsOut, RetransmitsIn;
        public string? TlsName, HttpRequest, TlsVersion, Alpn, AlpnOffered;
        public int? HttpStatus;
        public double? HttpResponseMs;
        public long? SendQueue, ReceiveQueue;
        public long ResetsOut, ResetsIn, ZeroWindowOut, ZeroWindowIn, Unreachable, TooBig;
        public bool Refused, Unanswered;
        public double? RttMs, Delivery;
        public string? LimitedBy;
        public long? BusyUs, ReceiveWindowUs, SendBufferUs;
    }

    // Which processes listen on which ports and hold how many connections, from the last sample.
    public IReadOnlyDictionary<int, List<int>> PortsByPid { get; private set; } = new Dictionary<int, List<int>>();
    public IReadOnlyDictionary<int, int> ConnectionsByPid { get; private set; } = new Dictionary<int, int>();

    private int _dnsReplies, _dnsFailures, _dnsTimed;

    // net-meter's "O" line: the bytes of flows that didn't fit in its table, in total; null until there are any.
    private (long Out, long In)? _overflow;
    private (long Out, long In) _previousOverflow;
    private double _dnsTotalMs;

    private sealed record Lookup(string Result, IReadOnlyList<string> Answers, DateTime FirstSeen, DateTime LastSeen, double? LatencyMs, IReadOnlyList<string>? Aliases);

    private readonly record struct SocketEntry(string Protocol, string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, string State, string Uid, long Inode, long TxQueue, long RxQueue);

    public (TrafficSplit? Traffic, DnsStats? Dns, NetworkActivity Activity) Sample()
    {
        EnsureHelper();
        var now = DateTime.UtcNow;
        var timestamp = Stopwatch.GetTimestamp();
        var elapsed = _previousTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_previousTimestamp, timestamp).TotalSeconds;
        _previousTimestamp = timestamp;

        var sockets = ReadSockets();
        var owners = SocketOwners(sockets);
        var tcp = TcpInfoReader.Read();
        _local.Refresh();

        lock (_lock)
        {
            _destinations.Prune(now);
            _listeners.Prune(now);
            _names.Prune(now);
            _lookups.Prune(now);
            var capturing = _helper is { HasExited: false };
            if (!capturing)
            {
                foreach (var s in sockets.Where(s => s.RemotePort != 0 && s.State != "LISTEN"))
                {
                    Track(s.Protocol, s.LocalPort, s.RemoteAddress, s.RemotePort, now).State = s.State;
                }
            }

            // Each connection's own socket, or else the listening socket it was accepted on.
            var bySocket = new Dictionary<FlowKey, SocketEntry>();
            var byLocalPort = new Dictionary<(string Protocol, int Port), SocketEntry>();
            foreach (var s in sockets)
            {
                if (s.RemotePort == 0)
                {
                    byLocalPort.TryAdd((s.Protocol, s.LocalPort), s);
                }
                else
                {
                    bySocket.TryAdd(new FlowKey(s.Protocol, s.LocalPort, s.RemoteAddress, s.RemotePort), s);
                }
            }

            foreach (var (key, flow) in _flows)
            {
                var accepted = byLocalPort.TryGetValue((flow.Protocol, flow.LocalPort), out var listener);
                if (!bySocket.TryGetValue(key, out var socket))
                {
                    if (!accepted)
                    {
                        (flow.SendQueue, flow.ReceiveQueue) = (null, null);
                        continue;
                    }

                    socket = listener;
                }

                var connected = socket.Protocol == "tcp" && socket.RemotePort != 0 && socket.State != "LISTEN";
                (flow.SendQueue, flow.ReceiveQueue) = connected ? (socket.TxQueue, socket.RxQueue) : ((long?)null, (long?)null);
                if (socket.RemotePort != 0)
                {
                    flow.State = socket.State;
                }

                if (socket.Inode != 0)
                {
                    flow.User ??= UserNames.Of(socket.Uid);
                    if (owners.TryGetValue(socket.Inode, out var owner))
                    {
                        (flow.Pid, flow.Process) = (owner.Pid, owner.Name);
                    }
                }

                // A closed connection's socket (TIME_WAIT) has no owner left; one accepted on a listening socket
                // belongs to that socket's process.
                if (flow.Process is null && accepted && listener.Inode != 0 && owners.TryGetValue(listener.Inode, out var server))
                {
                    (flow.Pid, flow.Process) = (server.Pid, server.Name);
                    flow.User ??= UserNames.Of(listener.Uid);
                }
            }

            double internetOut = 0, internetIn = 0, internalOut = 0, internalIn = 0, networkOut = 0, networkIn = 0;
            // Removing while enumerating is allowed: Remove doesn't invalidate a dictionary's enumerators.
            foreach (var (key, flow) in _flows)
            {
                if (now - flow.LastSeen > FlowExpiry)
                {
                    _flows.Remove(key);
                    continue;
                }

                if (flow.Protocol == "tcp" && tcp.TryGetValue((flow.LocalPort, flow.Remote, flow.RemotePort), out var info))
                {
                    Measure(flow, info);
                }
                else
                {
                    (flow.RttMs, flow.Delivery, flow.LimitedBy) = (null, null, null);
                }

                long moved = 0;
                if (flow.BytesOut is { } bytesOut && flow.BytesIn is { } bytesIn)
                {
                    moved = Math.Max(0, bytesOut - flow.PreviousOut) + Math.Max(0, bytesIn - flow.PreviousIn);
                    flow.OutRate = elapsed > 0 ? Math.Round(Math.Max(0, bytesOut - flow.PreviousOut) / elapsed) : 0;
                    flow.InRate = elapsed > 0 ? Math.Round(Math.Max(0, bytesIn - flow.PreviousIn) / elapsed) : 0;
                    (flow.PreviousOut, flow.PreviousIn) = (bytesOut, bytesIn);
                }

                flow.Zone = _local.Zone(flow.Remote, flow.Internet, flow.RemoteV4);
                Remember(flow, byLocalPort.ContainsKey((flow.Protocol, flow.LocalPort)), moved, now);
                if (flow.Zone == "network") { networkOut += flow.OutRate ?? 0; networkIn += flow.InRate ?? 0; }
                if (flow.Internet) { internetOut += flow.OutRate ?? 0; internetIn += flow.InRate ?? 0; }
                else { internalOut += flow.OutRate ?? 0; internalIn += flow.InRate ?? 0; }
            }

            PrunePeers(now);
            var traffic = capturing ? new TrafficSplit(internetOut, internetIn, internalOut, internalIn, networkOut, networkIn) : null;
            DnsStats? dns = null;
            if (capturing && elapsed > 0)
            {
                dns = new DnsStats(
                    Math.Round(_dnsReplies / elapsed, 2),
                    Math.Round(_dnsFailures / elapsed, 2),
                    _dnsTimed > 0 ? Math.Round(_dnsTotalMs / _dnsTimed, 1) : null);
            }

            (_dnsReplies, _dnsFailures, _dnsTimed, _dnsTotalMs) = (0, 0, 0, 0);

            UntrackedTraffic? untracked = null;
            if (capturing && _overflow is { } overflow)
            {
                untracked = new UntrackedTraffic(
                    elapsed > 0 ? Math.Round(Math.Max(0, overflow.Out - _previousOverflow.Out) / elapsed) : 0,
                    elapsed > 0 ? Math.Round(Math.Max(0, overflow.In - _previousOverflow.In) / elapsed) : 0,
                    overflow.Out,
                    overflow.In);
                _previousOverflow = overflow;
            }

            return (traffic, dns, Activity(sockets, owners, capturing, now, untracked));
        }
    }

    // A flow counts toward its peer at the service port: this container's when it listens on it (or, failing that,
    // when only the remote port is ephemeral), else the remote's. Internet addresses are one peer.
    private void Remember(Flow flow, bool listening, long moved, DateTime now)
    {
        // A refused or unanswered attempt, or a lone packet, is no conversation: a peer counts once data went both ways.
        if (flow.RemotePort == 0 || flow.Process is "sounding-agent" or "net-meter" || flow.Refused || flow.Unanswered
            || flow.BytesOut == 0 || flow.BytesIn == 0)
        {
            return;
        }

        var inbound = listening || (flow.LocalPort < FirstEphemeralPort && flow.RemotePort >= FirstEphemeralPort);
        var key = new PeerKey(flow.Protocol, flow.Internet ? "*" : flow.Remote, inbound ? flow.LocalPort : flow.RemotePort, inbound);
        if (!_peers.TryGetValue(key, out var link))
        {
            _peers[key] = link = new PeerLink { FirstSeen = flow.FirstSeen, LastSeen = flow.LastSeen };
        }

        if (flow.FirstSeen < link.FirstSeen) link.FirstSeen = flow.FirstSeen;
        if (flow.LastSeen > link.LastSeen) link.LastSeen = flow.LastSeen;
        if (link.RateAt != now) (link.Rate, link.RateAt) = (0, now);
        link.Rate += (flow.OutRate ?? 0) + (flow.InRate ?? 0);
        link.Bytes += moved;
        (link.Zone, link.Internet) = (flow.Zone, flow.Internet);
        link.Sure |= listening;
    }

    private void PrunePeers(DateTime now)
    {
        foreach (var (key, link) in _peers)
        {
            if (now - link.LastSeen > _peerExpiry) _peers.Remove(key);
        }

        if (_peers.Count > MaxPeers)
        {
            foreach (var key in _peers.OrderBy(p => p.Value.LastSeen).Take(_peers.Count - MaxPeers).Select(p => p.Key).ToList())
            {
                _peers.Remove(key);
            }
        }
    }

    // What limited a connection since the last sample, from the kernel's time counters: the receiver's window,
    // the send buffer, the application not giving it data, or else the network itself.
    private static void Measure(Flow flow, TcpInfoReader.TcpInfo info)
    {
        flow.RttMs = info.RttMs;
        flow.Delivery = info.DeliveryBytesPerSecond;
        long Delta(long? now, long? before) => now is { } a && before is { } b ? Math.Max(0, a - b) : 0;
        var busy = Delta(info.BusyUs, flow.BusyUs);
        var window = Delta(info.ReceiveWindowLimitedUs, flow.ReceiveWindowUs);
        var buffer = Delta(info.SendBufferLimitedUs, flow.SendBufferUs);
        flow.LimitedBy = busy == 0 ? null
            : window * 2 > busy ? "receiver"
            : buffer * 2 > busy ? "send buffer"
            : info.AppLimited ? "application"
            : "network";
        (flow.BusyUs, flow.ReceiveWindowUs, flow.SendBufferUs) = (info.BusyUs, info.ReceiveWindowLimitedUs, info.SendBufferLimitedUs);
        if (info.PeerWindow == 0 && info.Unacked > 0)
        {
            flow.ZeroWindowIn = Math.Max(1, flow.ZeroWindowIn);
        }
    }

    private static IReadOnlyList<string>? Problems(Flow flow)
    {
        List<string>? problems = null;
        void Add(bool found, string problem)
        {
            if (found) (problems ??= []).Add(problem);
        }

        Add(flow.Refused, "refused");
        Add(flow.Unanswered, "no answer");
        Add(flow.ResetsIn > 0, "reset by peer");
        Add(flow.ResetsOut > 0, "reset by this end");
        Add(flow.ZeroWindowIn > 0, "zero window (peer)");
        Add(flow.ZeroWindowOut > 0, "zero window (this end)");
        Add(flow.Unreachable > 0, "unreachable");
        Add(flow.TooBig > 0, "fragmentation needed");
        return problems;
    }

    public void StartCapture(int seconds)
    {
        lock (_lock)
        {
            if (_helper is not { HasExited: false } || _capture is not null)
            {
                return;
            }

            _capture = new PacketCapture();
            _helper.StandardInput.WriteLine($"C {seconds}");
            _helper.StandardInput.Flush();
        }
    }

    public void CancelCapture()
    {
        lock (_lock)
        {
            _capture = null;
            _captured = null;
            // Restart the helper so a late completion cannot be mistaken for the next capture.
            try { if (_helper is { HasExited: false }) _helper.Kill(); }
            catch (InvalidOperationException) { }
            _helper = null;
            _retryAt = DateTime.MinValue;
        }
    }

    // A finished capture, once.
    public PacketCapture? TakeCapture()
    {
        lock (_lock)
        {
            var captured = _captured;
            _captured = null;
            return captured;
        }
    }

    public bool Capturing
    {
        get { lock (_lock) return _capture is not null; }
    }

    public IReadOnlyList<AgentEvent> TakeEvents()
    {
        lock (_lock)
        {
            if (_events.Count == 0)
            {
                return [];
            }

            var events = _events.ToList();
            _events.Clear();
            return events;
        }
    }

    private NetworkActivity Activity(
        List<SocketEntry> sockets, Dictionary<long, (int Pid, string Name)> owners, bool capturing, DateTime now, UntrackedTraffic? untracked)
    {
        var connections = _flows.Values
            .OrderBy(flow => Age(flow.LastSeen, now))
            .ThenByDescending(flow => (flow.OutRate ?? 0) + (flow.InRate ?? 0))
            .Take(MaxListed)
            .Select(flow => new Connection(
                flow.Protocol, flow.Remote, flow.RemotePort, _names.GetValueOrDefault(flow.Remote), flow.LocalPort,
                flow.Internet, IsNew(_destinations.GetValueOrDefault((flow.Protocol, flow.Remote, flow.RemotePort), flow.FirstSeen), now),
                flow.BytesOut, flow.BytesIn, flow.OutRate, flow.InRate,
                Age(flow.FirstSeen, now), Age(flow.LastSeen, now), flow.State, flow.Pid, flow.Process, flow.User,
                flow.HandshakeMs, flow.RetransmitsOut, flow.RetransmitsIn, flow.TlsName, flow.HttpRequest,
                flow.HttpStatus, flow.HttpResponseMs, flow.TlsVersion, flow.Alpn, flow.AlpnOffered, flow.SendQueue, flow.ReceiveQueue,
                flow.Zone, flow.RttMs, flow.Delivery, flow.LimitedBy, Problems(flow)))
            .ToList();

        // Only listeners use these sets; avoid grouping every outgoing connection.
        var clients = new Dictionary<(string Protocol, int Port), HashSet<string>>();
        foreach (var socket in sockets.Where(s => s.State == "LISTEN" || (s.Protocol == "udp" && s.RemotePort == 0)))
            clients.TryAdd((socket.Protocol, socket.LocalPort), []);
        foreach (var flow in _flows.Values)
            if (flow.RemotePort != 0 && clients.TryGetValue((flow.Protocol, flow.LocalPort), out var peers)) peers.Add(flow.Remote);

        var listening = new List<Listener>();
        foreach (var s in sockets.Where(s => s.State == "LISTEN" || (s.Protocol == "udp" && s.RemotePort == 0)))
        {
            if (s.LocalAddress == "127.0.0.11")
            {
                continue;
            }

            var key = (s.Protocol, s.LocalAddress, s.LocalPort);
            var owned = owners.TryGetValue(s.Inode, out var owner);
            if (!_listeners.TryGetValue(key, out var firstSeen))
            {
                _listeners[key] = firstSeen = now;
                if (IsNew(firstSeen, now))
                {
                    _events.Add(new AgentEvent("port", "info", $"Listening on {s.LocalPort}/{s.Protocol}{(owned ? $" ({owner.Name})" : "")}"));
                }
            }

            var from = clients.GetValueOrDefault((s.Protocol, s.LocalPort)) ?? [];
            listening.Add(new Listener(s.Protocol, s.LocalAddress, s.LocalPort, IsNew(firstSeen, now),
                owned ? owner.Pid : null, owned ? owner.Name : null, UserNames.Of(s.Uid), s.Protocol == "tcp" ? (int)s.RxQueue : null,
                from.Count, from.Count(IsInternet), from.Count(a => _local.Zone(a) == "host"), _published.Of(s.Protocol, s.LocalPort)));
        }

        PortsByPid = listening.Where(l => l.Pid is not null).GroupBy(l => l.Pid!.Value)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Port).Distinct().Order().ToList());
        ConnectionsByPid = sockets.Where(s => s.RemotePort != 0 && s.State != "LISTEN" && owners.ContainsKey(s.Inode))
            .GroupBy(s => owners[s.Inode].Pid)
            .ToDictionary(g => g.Key, g => g.Count());

        var lookups = _lookups
            .OrderByDescending(l => l.Value!.LastSeen)
            .Take(MaxListed)
            .Select(l => new DnsLookup(l.Key, l.Value!.Result, l.Value.Answers, Age(l.Value.FirstSeen, now), Age(l.Value.LastSeen, now), l.Value.LatencyMs, l.Value.Aliases))
            .ToList();

        // Counted by protocol and state: a name is only built for each of the few there are.
        var states = new Dictionary<(string Protocol, string State), int>();
        foreach (var socket in sockets)
        {
            if (socket.LocalAddress != "127.0.0.11")
            {
                var state = (socket.Protocol, socket.State);
                states[state] = states.GetValueOrDefault(state) + 1;
            }
        }

        return new NetworkActivity(
            capturing ? "packets" : "sockets",
            capturing ? null : _captureError,
            connections,
            _flows.Count,
            listening.OrderBy(l => l.Port).ToList(),
            lookups,
            states
                .Select(s => (Name: $"{s.Key.Protocol.ToUpperInvariant()} {s.Key.State}", Count: s.Value))
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToDictionary(s => s.Name, s => s.Count),
            ReadInt("sys/net/core/somaxconn"),
            _local.Addresses,
            capturing ? _requests.Stats(now, (now - _captureStarted).TotalSeconds, ip => _names.GetValueOrDefault(ip), ProcessOf) : null,
            Ephemeral(sockets),
            _local.Networks,
            untracked,
            _peers.Select(p => new Peer(
                p.Key.Protocol, p.Key.Remote, p.Value.Internet ? null : _names.GetValueOrDefault(p.Key.Remote), p.Key.Port, p.Key.Inbound,
                p.Value.Sure, p.Value.Zone, p.Value.Internet, p.Value.Bytes, p.Value.RateAt == now ? p.Value.Rate : 0,
                Age(p.Value.FirstSeen, now), Age(p.Value.LastSeen, now))).ToList());
    }

    private string? ProcessOf(RequestConnection connection) =>
        _flows.TryGetValue(new FlowKey("tcp", connection.LocalPort, connection.Remote, connection.RemotePort), out var flow) ? flow.Process : null;

    // Connections to one destination each take a local port from this range; when it runs out, new ones fail.
    private EphemeralPorts? Ephemeral(List<SocketEntry> sockets)
    {
        if (KernelFile.TryReadAllText(Path.Combine(_procPath, "sys/net/ipv4/ip_local_port_range")) is not { } range
            || range.Split((char[])['\t', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries) is not [var low, var high]
            || !int.TryParse(low, CultureInfo.InvariantCulture, out var first) || !int.TryParse(high, CultureInfo.InvariantCulture, out var last))
        {
            return null;
        }

        var busiest = sockets
            .Where(s => s.Protocol == "tcp" && s.RemotePort != 0 && s.State != "LISTEN" && s.LocalPort >= first && s.LocalPort <= last)
            .GroupBy(s => (s.RemoteAddress, s.RemotePort))
            .MaxBy(g => g.Count());
        var size = last - first + 1;
        return busiest is null ? null
            : new EphemeralPorts(Math.Round(100.0 * busiest.Count() / size, 2), busiest.Count(), size, $"{busiest.Key.RemoteAddress}:{busiest.Key.RemotePort}");
    }

    private Flow Track(string protocol, int localPort, string remote, int remotePort, DateTime now)
    {
        var key = new FlowKey(protocol, localPort, remote, remotePort);
        var destination = (protocol, remote, remotePort);
        if (!_flows.TryGetValue(key, out var flow))
        {
            _flows[key] = flow = new Flow
            {
                Protocol = protocol, LocalPort = localPort, Remote = remote, RemotePort = remotePort, FirstSeen = now,
                Internet = IsInternet(remote), RemoteV4 = LocalNetwork.V4(remote),
            };
            if (_destinations.TryAdd(destination, now) && flow.Internet && IsNew(now, now))
            {
                var name = _names.GetValueOrDefault(remote);
                _events.Add(new AgentEvent("destination", "info", $"New internet destination {(name is null ? remote : $"{name} ({remote})")}:{remotePort}/{protocol}"));
            }
        }

        flow.LastSeen = now;
        _destinations.TryAdd(destination, flow.FirstSeen);
        if (_names.GetValueOrDefault(remote) is { } activeName) _lookups.TryGetValue(activeName, out _);
        return flow;
    }

    private bool IsNew(DateTime firstSeen, DateTime now)
    {
        var observedSince = _helper is { HasExited: false } ? _captureStarted : _started;
        return firstSeen - observedSince > WarmUp && now - firstSeen < NewWindow;
    }

    private static double Age(DateTime then, DateTime now) => Math.Round(Math.Max(0, (now - then).TotalSeconds));

    private void EnsureHelper()
    {
        if (_helper is { HasExited: false } || DateTime.UtcNow < _retryAt)
        {
            return;
        }

        _retryAt = DateTime.UtcNow + RetryCapture;
        try
        {
            var helper = Process.Start(new ProcessStartInfo(_helperPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("could not start");
            lock (_lock)
            {
                // A new net-meter counts from zero.
                (_overflow, _previousOverflow) = (null, default);
            }

            _helper = helper;
            _captureStarted = DateTime.UtcNow;
            _captureError = null;
            new Thread(() => ReadReports(helper)) { IsBackground = true, Name = "net-meter" }.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _captureError = $"net-meter not available: {ex.Message}";
            logger.LogWarning("Packet capture off, using socket tables only: {Reason}", _captureError);
        }
    }

    private void ReadReports(Process helper)
    {
        try
        {
            while (helper.StandardOutput.ReadLine() is { } line)
            {
                var f = line.Split(' ');
                lock (_lock)
                {
                    if (!ReferenceEquals(helper, _helper)) return;
                    var now = DateTime.UtcNow;
                    if (f is ["F", var proto, _, var localPort, var remote, var remotePort, var bytesOut, var bytesIn, _, _,
                            var handshake, var retransOut, var retransIn, var tls, var method, var host, var path, var status, var response,
                            var version, var alpn, var offered, var resetsOut, var resetsIn, var refused, var unanswered,
                            var zeroOut, var zeroIn, var unreachable, var tooBig]
                        && !IsHousekeeping(remote))
                    {
                        var flow = Track(proto, int.Parse(localPort, CultureInfo.InvariantCulture), Normalize(remote), int.Parse(remotePort, CultureInfo.InvariantCulture), now);
                        flow.BytesOut = Long(bytesOut);
                        flow.BytesIn = Long(bytesIn);
                        flow.Unreachable = Long(unreachable);
                        flow.TooBig = Long(tooBig);
                        if (proto == "tcp")
                        {
                            flow.HttpStatus = status == "-" ? null : int.Parse(status, CultureInfo.InvariantCulture);
                            flow.HttpResponseMs = response == "-" ? null : double.Parse(response, CultureInfo.InvariantCulture);
                            flow.TlsVersion = version == "-" ? flow.TlsVersion : version;
                            flow.Alpn = alpn == "-" ? flow.Alpn : alpn;
                            flow.AlpnOffered = offered == "-" ? flow.AlpnOffered : offered;
                            flow.HandshakeMs = handshake == "-" ? flow.HandshakeMs : double.Parse(handshake, CultureInfo.InvariantCulture);
                            flow.RetransmitsOut = Long(retransOut);
                            flow.RetransmitsIn = Long(retransIn);
                            flow.TlsName = tls == "-" ? flow.TlsName : tls;
                            if (method != "-")
                            {
                                flow.HttpRequest = host == "-" ? $"{method} {path}" : $"{method} {host}{(path == "-" ? "" : path)}";
                            }

                            (flow.ResetsOut, flow.ResetsIn, flow.ZeroWindowOut, flow.ZeroWindowIn) = (Long(resetsOut), Long(resetsIn), Long(zeroOut), Long(zeroIn));
                            (flow.Refused, flow.Unanswered) = (refused == "1", unanswered == "1");
                        }
                    }
                    else if (f is ["Q", ..])
                    {
                        _requests.Add(f, Normalize, now);
                    }
                    else if (f is ["P", ..])
                    {
                        _capture?.Add(f);
                    }
                    else if (f is ["O", var overflowOut, var overflowIn])
                    {
                        _overflow = (Long(overflowOut), Long(overflowIn));
                    }
                    else if (f is ["C", _])
                    {
                        (_captured, _capture) = (_capture, null);
                    }
                    else if (f is ["D", var name, var result, var answers, .. var rest])
                    {
                        var ips = answers == "-" ? [] : answers.Split(',').Select(Normalize).ToArray();
                        foreach (var ip in ips.Where(a => IPAddress.TryParse(a, out _)))
                        {
                            _names[ip] = name;
                        }

                        double? latency = rest is [var took, ..] && double.TryParse(took, CultureInfo.InvariantCulture, out var ms) ? ms : null;
                        string[]? aliases = rest is [_, var chain, ..] && chain != "-" ? chain.Split(',') : null;
                        _dnsReplies++;
                        _dnsFailures += result == "NOERROR" ? 0 : 1;
                        if (latency is { } replyMs)
                        {
                            _dnsTimed++;
                            _dnsTotalMs += replyMs;
                        }

                        var previous = _lookups.GetValueOrDefault(name);
                        var answered = result == "NOERROR" && previous?.Result == "NOERROR"
                            ? previous.Answers.Union(ips).Take(16).ToArray()
                            : ips;
                        _lookups[name] = new Lookup(result, answered, previous?.FirstSeen ?? now, now, latency ?? previous?.LatencyMs, aliases ?? previous?.Aliases);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException)
        {
            logger.LogWarning("net-meter output unreadable: {Message}", ex.Message);
        }

        var error = helper.StandardError.ReadToEnd().Trim();
        lock (_lock)
        {
            if (!ReferenceEquals(helper, _helper)) return;
            _captureError = $"net-meter stopped{(error.Length > 0 ? $": {error}" : "")}";
        }

        logger.LogWarning("Packet capture off, using socket tables only: {Reason}", _captureError);
    }

    public void Dispose()
    {
        try
        {
            _helper?.Kill();
        }
        catch (InvalidOperationException)
        {
        }

        _helper?.Dispose();
    }

    private List<SocketEntry> ReadSockets()
    {
        var sockets = new List<SocketEntry>();
        Span<Range> f = stackalloc Range[11];
        foreach (var (file, protocol) in SocketTables)
        {
            if (KernelFile.TryReadAllText(Path.Combine(_procPath, "self/net", file)) is not { } table)
            {
                continue;
            }

            // sl local_address rem_address st tx_queue:rx_queue tr:tm->when retrnsmt uid timeout inode ...
            var text = table.AsSpan();
            var header = true;
            foreach (var range in text.Split('\n'))
            {
                if (header)
                {
                    header = false;
                    continue;
                }

                var line = text[range];
                if (line.Split(f, ' ', StringSplitOptions.RemoveEmptyEntries) < 10
                    || ParseEndpoint(line[f[1]]) is not { } local || ParseEndpoint(line[f[2]]) is not { } remote)
                {
                    continue;
                }

                var queues = line[f[4]];
                var colon = queues.IndexOf(':');
                var received = colon < 0 ? default : queues[(colon + 1)..];
                // What follows a second colon was never part of the number.
                received = received.IndexOf(':') is var end and >= 0 ? received[..end] : received;
                sockets.Add(new SocketEntry(protocol, local.Address, local.Port, remote.Address, remote.Port,
                    protocol == "tcp" ? TcpState(line[f[3]]) : remote.Port == 0 ? "UNCONN" : "CONNECTED",
                    SocketUids.Get(line[f[7]]), long.Parse(line[f[9]], CultureInfo.InvariantCulture),
                    long.Parse(colon < 0 ? queues : queues[..colon], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    colon < 0 ? 0 : long.Parse(received, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }
        }

        return sockets;
    }

    private static (string Address, int Port)? ParseEndpoint(ReadOnlySpan<char> text)
    {
        var colon = text.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        // 8 hex digits (IPv4) or 32 (IPv6), read on the stack.
        var hex = text[..colon];
        Span<byte> bytes = hex.Length <= 32 ? stackalloc byte[hex.Length / 2] : new byte[hex.Length / 2];
        bytes.Clear();
        for (var word = 0; word < bytes.Length / 4; word++)
        {
            var value = uint.Parse(hex.Slice(word * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            BitConverter.TryWriteBytes(bytes.Slice(word * 4, 4), value); // little-endian, as the kernel wrote it
        }

        var address = new IPAddress(bytes);
        return ((address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString(), int.Parse(text[(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static string TcpState(ReadOnlySpan<char> code) => code switch
    {
        "01" => "ESTABLISHED", "02" => "SYN_SENT", "03" => "SYN_RECV", "04" => "FIN_WAIT1", "05" => "FIN_WAIT2",
        "06" => "TIME_WAIT", "07" => "CLOSE", "08" => "CLOSE_WAIT", "09" => "LAST_ACK", "0A" => "LISTEN", "0B" => "CLOSING",
        _ => code.ToString(),
    };

    // Which process holds each socket. Reading every descriptor of every process is the costly part, so that is
    // only redone when a process or a socket appeared; the process names are read every time.
    private Dictionary<long, (int Pid, string Name)> SocketOwners(List<SocketEntry> sockets)
    {
        if (KernelFile.TryReadAllLines(_procsPath) is not { } pids)
        {
            return [];
        }

        if (!pids.AsSpan().SequenceEqual(_scannedPids) || sockets.Any(s => s.Inode != 0 && !_scannedInodes.Contains(s.Inode)))
        {
            _socketPids = ScanSocketPids(pids);
            _scannedPids = pids;
            _scannedInodes = sockets.Select(s => s.Inode).ToHashSet();
        }

        var names = new Dictionary<int, string?>();
        var owners = new Dictionary<long, (int, string)>();
        foreach (var (inode, pid) in _socketPids)
        {
            if (!names.TryGetValue(pid, out var name))
            {
                names[pid] = name = Comm(pid);
            }

            if (name is not null)
            {
                owners[inode] = (pid, name);
            }
        }

        return owners;
    }

    private Dictionary<long, int> ScanSocketPids(string[] pids)
    {
        var socketPids = new Dictionary<long, int>();
        foreach (var pid in pids)
        {
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(_procPath, pid, "fd")))
                {
                    if (new FileInfo(fd).LinkTarget is { } target && target.StartsWith("socket:[", StringComparison.Ordinal)
                        && long.TryParse(target.AsSpan(8, target.Length - 9), CultureInfo.InvariantCulture, out var inode))
                    {
                        socketPids[inode] = int.Parse(pid, CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return socketPids;
    }

    // Null once the process has exited.
    private string? Comm(int pid) =>
        KernelFile.TryReadAllText(Path.Combine(_procPath, pid.ToString(CultureInfo.InvariantCulture), "comm"))?.Trim();

    private int? ReadInt(string relativePath) =>
        int.TryParse(KernelFile.TryReadAllText(Path.Combine(_procPath, relativePath))?.Trim(), CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long Long(string value) => long.Parse(value, CultureInfo.InvariantCulture);

    private static string Normalize(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : address;

    private static bool IsHousekeeping(string address)
    {
        if (!IPAddress.TryParse(address, out var ip))
        {
            return false;
        }

        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast)
        {
            return true;
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> b = stackalloc byte[4];
        ip.TryWriteBytes(b, out _);
        return b[0] >= 224 || (b[0] == 169 && b[1] == 254);
    }

    public static bool IsInternet(string address)
    {
        if (!IPAddress.TryParse(address, out var ip))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
        }

        return !(IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal
            || ip.Equals(IPAddress.IPv6None));
    }
}
