using System.Diagnostics;
using System.Globalization;

namespace Sounding.Agent.Sampling;

public sealed class TcpTracker(string snmpPath, string netstatPath)
{
    // The counters the rates below use; the files hold some two hundred.
    private static readonly string[] UdpCounters = ["InErrors", "RcvbufErrors", "SndbufErrors", "NoPorts"];
    private static readonly string[] TcpCounters = ["RetransSegs", "OutRsts", "AttemptFails", "ActiveOpens", "PassiveOpens", "CurrEstab"];
    private static readonly string[] TcpExtCounters = ["ListenDrops"];

    private Dictionary<string, long>? _previous;
    private long _previousTimestamp;

    public UdpStats? Udp { get; private set; }

    public TcpStats? Sample()
    {
        var now = new Dictionary<string, long>();
        var snmp = KernelFile.TryReadAllLines(snmpPath);
        var udp = Read(snmp, "Udp:", UdpCounters, now);
        if (!Read(snmp, "Tcp:", TcpCounters, now) || !Read(KernelFile.TryReadAllLines(netstatPath), "TcpExt:", TcpExtCounters, now))
        {
            Udp = null;
            return null;
        }

        var timestamp = Stopwatch.GetTimestamp();
        var previous = _previous;
        var elapsed = Stopwatch.GetElapsedTime(_previousTimestamp, timestamp).TotalSeconds;
        _previous = now;
        _previousTimestamp = timestamp;
        if (previous is null || elapsed <= 0)
        {
            Udp = null;
            return null;
        }

        double Rate(string counter) =>
            Math.Round(Math.Max(0, now.GetValueOrDefault(counter) - previous.GetValueOrDefault(counter)) / elapsed, 2);

        Udp = udp ? new UdpStats(Rate("Udp:InErrors"), Rate("Udp:RcvbufErrors") + Rate("Udp:SndbufErrors"), Rate("Udp:NoPorts")) : null;

        return new TcpStats(
            Rate("Tcp:RetransSegs"),
            Rate("Tcp:OutRsts"),
            Rate("Tcp:AttemptFails"),
            Rate("TcpExt:ListenDrops"),
            Rate("Tcp:ActiveOpens"),
            Rate("Tcp:PassiveOpens"),
            now.GetValueOrDefault("Tcp:CurrEstab"));
    }

    // A protocol's two rows, names then values: whether they are there, and its wanted counters.
    private static bool Read(string[]? lines, string prefix, string[] wanted, Dictionary<string, long> into)
    {
        if (lines is null)
        {
            return false;
        }

        string? header = null, row = null;
        foreach (var line in lines)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (header is null)
            {
                header = line;
            }
            else
            {
                row = line;
                break;
            }
        }

        if (header is null || row is null)
        {
            return false;
        }

        var names = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var values = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < Math.Min(names.Length, values.Length); i++)
        {
            if (Array.IndexOf(wanted, names[i]) >= 0 && long.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                into[prefix + names[i]] = value;
            }
        }

        return true;
    }
}
