using System.Globalization;
using System.Text.RegularExpressions;

namespace Sounding.Agent.Sampling;

// A connection as net-meter names it in its "Q" lines.
public readonly record struct RequestConnection(int LocalPort, string Remote, int RemotePort);

// The requests net-meter decoded ("Q" lines), kept for the last minute per protocol, peer and operation.
public sealed partial class RequestTracker
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MaxListed = 100;

    // The upper bound of each latency bucket, in ms; the last one is open.
    private static readonly double[] Bounds = [0.1, 0.25, 0.5, 1, 2.5, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 5000];

    private readonly record struct Second(DateTime Time, long Requests, long Errors, long RequestBytes, long ResponseBytes, long[] Histogram);

    // The window's seconds and their totals, kept as seconds come and go.
    private sealed class Entry
    {
        public required string Protocol, Role, Peer, Operation;
        public int Port;
        public readonly Queue<Second> Seconds = new();
        public readonly Dictionary<RequestConnection, DateTime> Connections = [];
        public long Requests, Errors, RequestBytes, ResponseBytes;
        public readonly long[] Histogram = new long[Bounds.Length];

        public void Add(in Second second) => Count(second, 1);

        public void Remove(in Second second) => Count(second, -1);

        private void Count(in Second second, int sign)
        {
            Requests += sign * second.Requests;
            Errors += sign * second.Errors;
            RequestBytes += sign * second.RequestBytes;
            ResponseBytes += sign * second.ResponseBytes;
            for (var i = 0; i < Math.Min(Histogram.Length, second.Histogram.Length); i++)
            {
                Histogram[i] += sign * second.Histogram[i];
            }
        }
    }

    private readonly Dictionary<(string Protocol, string Role, string Peer, int Port, string Operation), Entry> _entries = [];

    // Q <protocol> <client|server> <remote ip> <remote port> <local port> <operation> <requests> <errors> <request bytes> <response bytes> <histogram>
    public void Add(string[] f, Func<string, string> normalize, DateTime now)
    {
        if (f is not ["Q", var protocol, var role, var remote, var remotePort, var localPort, var operation, var requests, var errors, var sent, var received, var histogram])
        {
            return;
        }

        var peer = normalize(remote);
        var connection = new RequestConnection(int.Parse(localPort, CultureInfo.InvariantCulture), peer, int.Parse(remotePort, CultureInfo.InvariantCulture));
        var port = role == "client" ? connection.RemotePort : connection.LocalPort;
        var name = operation == "-" ? "?" : operation.Replace('\t', ' ');
        if (protocol == "http")
        {
            name = Route(name);
        }

        var key = (protocol, role, peer, port, name);
        if (!_entries.TryGetValue(key, out var entry))
        {
            _entries[key] = entry = new Entry { Protocol = protocol, Role = role, Peer = peer, Port = port, Operation = name };
        }

        var second = new Second(now, Long(requests), Long(errors), Long(sent), Long(received), histogram.Split(',').Select(Long).ToArray());
        entry.Seconds.Enqueue(second);
        entry.Add(second);
        entry.Connections[connection] = now;
    }

    // nameOf: a peer's DNS name; processOf: the process holding a connection.
    public IReadOnlyList<RequestStats>? Stats(DateTime now, double observedSeconds, Func<string, string?> nameOf, Func<RequestConnection, string?> processOf)
    {
        var seconds = Math.Clamp(observedSeconds, 1, Window.TotalSeconds);
        var stats = new List<RequestStats>();
        // Removing while enumerating is allowed: Remove doesn't invalidate a dictionary's enumerators.
        foreach (var (key, entry) in _entries)
        {
            while (entry.Seconds.TryPeek(out var oldest) && now - oldest.Time > Window)
            {
                entry.Remove(entry.Seconds.Dequeue());
            }

            foreach (var (connection, seen) in entry.Connections)
            {
                if (now - seen > Window)
                {
                    entry.Connections.Remove(connection);
                }
            }

            if (entry.Seconds.Count == 0)
            {
                _entries.Remove(key);
                continue;
            }

            var requests = entry.Requests;
            if (requests == 0)
            {
                continue;
            }

            string? process = null;
            foreach (var connection in entry.Connections.Keys)
            {
                if ((process = processOf(connection)) is not null)
                {
                    break;
                }
            }

            stats.Add(new RequestStats(
                entry.Protocol, entry.Role, entry.Peer, nameOf(entry.Peer), entry.Port, entry.Operation,
                Math.Round(requests / seconds, 2),
                Math.Round(entry.Errors / seconds, 2),
                Percentile(entry.Histogram, 0.5),
                Percentile(entry.Histogram, 0.95),
                Math.Round((double)entry.RequestBytes / requests),
                Math.Round((double)entry.ResponseBytes / requests),
                entry.Connections.Count > 0 ? Math.Round((double)requests / entry.Connections.Count, 1) : null,
                process));
        }

        return stats.Count == 0 ? null : stats.OrderByDescending(s => s.RequestsPerSecond).Take(MaxListed).ToList();
    }

    private static double? Percentile(long[] histogram, double share)
    {
        var total = histogram.Sum();
        if (total == 0)
        {
            return null; // only requests that get no reply: no time to tell
        }

        long seen = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            seen += histogram[i];
            if (seen >= share * total)
            {
                return Bounds[i];
            }
        }

        return null;
    }

    // "/users/42/orders/9f8a…" is "/users/{id}/orders/{id}": one row per route, not per URL.
    private static string Route(string request) => Identifier().Replace(request, "/{id}");

    [GeneratedRegex(@"/(\d+|[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}|[0-9a-fA-F]{16,})(?=/|$)")]
    private static partial Regex Identifier();

    private static long Long(string value) => long.TryParse(value, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
