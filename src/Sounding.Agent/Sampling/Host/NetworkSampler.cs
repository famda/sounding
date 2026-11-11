using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Sounding.Agent.Sampling;

// The host's network, from a read-only mount of the host's /proc (interfaces, TCP, UDP, conntrack of its
// /proc/1/net) or, failing that, of its /sys (interfaces only). A container's own /proc and /sys only show
// its own network namespace.
public sealed class NetworkSampler(IOptions<AgentOptions> options)
{
    private readonly string _procNet = Path.Combine(options.Value.HostProcPath, "1/net");
    private readonly string _sysNet = Path.Combine(options.Value.HostSysPath, "class/net");
    private readonly string _procPath = options.Value.ProcPath;
    private readonly string[] _excludePrefixes = options.Value.NetworkExcludePrefixes
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private Dictionary<string, NetDevCounters> _previous = [];
    private long _previousTimestamp;
    private TcpTracker? _tcp;

    private bool FromProc => File.Exists(Path.Combine(_procNet, "dev"));

    public bool CanSeeHost => FromProc || Directory.Exists(_sysNet);

    public void Reset()
    {
        if (_previous.Count == 0 && _previousTimestamp == 0 && _tcp is null)
        {
            return; // nothing to forget: called every tick while another agent has the role
        }

        _previous = [];
        _previousTimestamp = 0;
        _tcp = null;
    }

    // Null on the first read, which is only a baseline for the rates.
    public NetworkStats? Sample()
    {
        var fromProc = FromProc;
        var current = fromProc ? NetDev.Read(Path.Combine(_procNet, "dev")) : SysNet.Read(_sysNet);
        if (fromProc)
        {
            _tcp ??= new TcpTracker(Path.Combine(_procNet, "snmp"), Path.Combine(_procNet, "netstat"));
        }

        var tcp = _tcp?.Sample();
        var timestamp = Stopwatch.GetTimestamp();
        var elapsed = _previousTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_previousTimestamp, timestamp).TotalSeconds;
        var previous = _previous;
        _previous = current;
        _previousTimestamp = timestamp;
        if (elapsed <= 0)
        {
            return null;
        }

        var interfaces = new List<NetworkInterfaceStats>();
        foreach (var (name, now) in current.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (_excludePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            var before = previous.GetValueOrDefault(name, now);
            double Rate(long a, long b) => Math.Round(Math.Max(0, a - b) / elapsed);
            interfaces.Add(new NetworkInterfaceStats(
                name,
                Rate(now.ReceiveBytes, before.ReceiveBytes),
                Rate(now.TransmitBytes, before.TransmitBytes),
                Rate(now.ReceivePackets, before.ReceivePackets),
                Rate(now.TransmitPackets, before.TransmitPackets),
                now.ReceiveBytes,
                now.TransmitBytes,
                now.Errors,
                now.Drops));
        }

        return new NetworkStats(
            true,
            null,
            interfaces,
            interfaces.Sum(i => i.ReceiveBytesPerSecond),
            interfaces.Sum(i => i.TransmitBytesPerSecond),
            tcp,
            _tcp?.Udp,
            fromProc ? ConntrackReader.Read(Path.Combine(_procNet, "stat/nf_conntrack"), _procPath) : null);
    }
}
