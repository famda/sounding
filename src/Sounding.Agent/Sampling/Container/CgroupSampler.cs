using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Sounding.Agent.Sampling;

public sealed class CgroupSampler
{
    private readonly string _root;

    private readonly string _netDevPath;

    private readonly int _hostCores;

    private readonly PressureTracker _pressure;
    private readonly TcpTracker _tcp;
    private readonly string _conntrackPath;
    private readonly string _procPath;
    private (long MajorFaults, long Refaults, long Scanned, long Stolen, long Timestamp)? _previousFaults;
    private (long LastPid, long Timestamp)? _previousPid;
    private bool? _ownPidNamespace;
    private readonly Dictionary<string, string> _deviceNames = [];
    private readonly NamePool _names = new();
    private readonly Dictionary<string, Dictionary<string, long>> _keys = [];

    private (long CpuMicroseconds, long NetReceived, long NetSent, long Periods, long Throttled, long ThrottledMicroseconds, long Timestamp)? _previous;
    private Dictionary<string, IoCounters> _previousIo = [];
    private Dictionary<string, NetDevCounters> _previousNet = [];

    public CgroupSampler(IOptions<AgentOptions> options)
    {
        _root = options.Value.CgroupPath;
        _netDevPath = Path.Combine(options.Value.ProcPath, "self/net/dev");
        _hostCores = Math.Max(1, CountHostCores(options.Value.ProcPath));
        _pressure = new PressureTracker(
            Path.Combine(_root, "cpu.pressure"), Path.Combine(_root, "memory.pressure"), Path.Combine(_root, "io.pressure"),
            cpuFullMeaningful: true);
        _tcp = new TcpTracker(Path.Combine(options.Value.ProcPath, "self/net/snmp"), Path.Combine(options.Value.ProcPath, "self/net/netstat"));
        _conntrackPath = Path.Combine(options.Value.ProcPath, "self/net/stat/nf_conntrack");
        _procPath = options.Value.ProcPath;
    }

    public ContainerUsage? Sample()
    {
        var cpuStat = ReadKeys("cpu.stat");
        if (!cpuStat.TryGetValue("usage_usec", out var cpuMicroseconds))
        {
            return null; // not cgroup v2
        }

        var io = ReadIo();
        var net = File.Exists(_netDevPath) ? NetDev.Read(_netDevPath) : [];
        net.Remove("lo");
        var netReceived = net.Values.Sum(i => i.ReceiveBytes);
        var netSent = net.Values.Sum(i => i.TransmitBytes);
        var periods = cpuStat.GetValueOrDefault("nr_periods");
        var throttled = cpuStat.GetValueOrDefault("nr_throttled");
        var throttledMicroseconds = cpuStat.GetValueOrDefault("throttled_usec");
        var timestamp = Stopwatch.GetTimestamp();

        double cores = 0, receiveRate = 0, sendRate = 0, throttledPercent = 0, throttledMsPerSecond = 0;
        var devices = new List<IoDevice>();
        var interfaces = new List<NetworkInterfaceStats>();
        if (_previous is { } previous)
        {
            var elapsed = Stopwatch.GetElapsedTime(previous.Timestamp, timestamp).TotalSeconds;
            cores = Math.Max(0, cpuMicroseconds - previous.CpuMicroseconds) / 1_000_000.0 / elapsed;
            receiveRate = Math.Max(0, netReceived - previous.NetReceived) / elapsed;
            sendRate = Math.Max(0, netSent - previous.NetSent) / elapsed;
            var periodDelta = periods - previous.Periods;
            throttledPercent = periodDelta > 0 ? 100.0 * Math.Max(0, throttled - previous.Throttled) / periodDelta : 0;
            throttledMsPerSecond = Math.Max(0, throttledMicroseconds - previous.ThrottledMicroseconds) / 1000.0 / elapsed;

            foreach (var (name, now) in net)
            {
                var before = _previousNet.TryGetValue(name, out var counters) ? counters : now;
                interfaces.Add(new NetworkInterfaceStats(
                    name,
                    Math.Round(Math.Max(0, now.ReceiveBytes - before.ReceiveBytes) / elapsed),
                    Math.Round(Math.Max(0, now.TransmitBytes - before.TransmitBytes) / elapsed),
                    Math.Round(Math.Max(0, now.ReceivePackets - before.ReceivePackets) / elapsed, 1),
                    Math.Round(Math.Max(0, now.TransmitPackets - before.TransmitPackets) / elapsed, 1),
                    now.ReceiveBytes,
                    now.TransmitBytes,
                    now.Errors,
                    now.Drops));
            }

            foreach (var (device, now) in io)
            {
                var before = _previousIo.TryGetValue(device, out var counters) ? counters : now;
                devices.Add(new IoDevice(
                    device,
                    Math.Round(Math.Max(0, now.ReadBytes - before.ReadBytes) / elapsed),
                    Math.Round(Math.Max(0, now.WriteBytes - before.WriteBytes) / elapsed),
                    Math.Round(Math.Max(0, now.ReadOps - before.ReadOps) / elapsed, 1),
                    Math.Round(Math.Max(0, now.WriteOps - before.WriteOps) / elapsed, 1)));
            }
        }

        _previous = (cpuMicroseconds, netReceived, netSent, periods, throttled, throttledMicroseconds, timestamp);
        _previousIo = io;
        _previousNet = net;

        var memoryStat = ReadKeys("memory.stat");
        // Like docker stats: page cache the kernel can drop (inactive_file) is not counted as used.
        var current = ReadLong("memory.current");
        var memory = Math.Max(0, (current ?? 0) - memoryStat.GetValueOrDefault("inactive_file"));
        var cpuLimit = ReadCpuLimit();

        var events = ReadKeys("memory.events");
        var (majorFaults, refaults, scanned, reclaimed) = FaultRates(memoryStat);
        long? Stat(string key) => memoryStat.TryGetValue(key, out var v) ? v : null;
        var detail = new ContainerDetail(
            _pressure.Sample(),
            cpuLimit is null ? null : new CpuThrottling(Math.Round(throttledPercent, 2), Math.Round(throttledMsPerSecond, 2), throttled),
            ReadText("cpuset.cpus.effective"),
            ReadLong("pids.current"),
            ReadLong("pids.max"),
            memoryStat.Count == 0 ? null : new MemoryBreakdown(
                memoryStat.GetValueOrDefault("anon"),
                memoryStat.GetValueOrDefault("file"),
                memoryStat.GetValueOrDefault("kernel"),
                memoryStat.GetValueOrDefault("shmem"),
                memoryStat.GetValueOrDefault("sock"),
                ReadLong("memory.peak"),
                majorFaults,
                refaults,
                ReadLong("memory.swap.current"),
                current,
                Stat("inactive_file"),
                Stat("file_dirty"),
                Stat("file_writeback"),
                scanned,
                reclaimed),
            events.Count == 0 ? null : new MemoryEvents(
                events.GetValueOrDefault("high"), events.GetValueOrDefault("max"), events.GetValueOrDefault("oom"), events.GetValueOrDefault("oom_kill")),
            devices,
            Gpus: [],
            GpuMemoryPeakBytes: null,
            Interfaces: interfaces,
            Tcp: _tcp.Sample(),
            TasksStartedPerSecond: TasksStarted(),
            Udp: _tcp.Udp,
            Conntrack: ConntrackReader.Read(_conntrackPath, _procPath));

        return new ContainerUsage(
            Math.Round(100 * cores / _hostCores, 2),
            Math.Round(cores, 3),
            cpuLimit,
            memory,
            ReadLong("memory.max"),
            devices.Sum(d => d.ReadBytesPerSecond),
            devices.Sum(d => d.WriteBytesPerSecond),
            Math.Round(receiveRate),
            Math.Round(sendRate),
            Detail: detail);
    }

    private (double?, double?, double?, double?) FaultRates(Dictionary<string, long> memoryStat)
    {
        if (!memoryStat.TryGetValue("pgmajfault", out var major))
        {
            return (null, null, null, null);
        }

        var refault = memoryStat.TryGetValue("workingset_refault_file", out var file) ? file : memoryStat.GetValueOrDefault("workingset_refault");
        var reclaim = memoryStat.ContainsKey("pgscan");
        (long MajorFaults, long Refaults, long Scanned, long Stolen, long Timestamp) now =
            (major, refault, memoryStat.GetValueOrDefault("pgscan"), memoryStat.GetValueOrDefault("pgsteal"), Stopwatch.GetTimestamp());
        var previous = _previousFaults;
        _previousFaults = now;
        if (previous is not { } before)
        {
            return (null, null, null, null);
        }

        var elapsed = Stopwatch.GetElapsedTime(before.Timestamp, now.Timestamp).TotalSeconds;
        if (elapsed <= 0)
        {
            return (null, null, null, null);
        }

        double Rate(long a, long b, long scale = 1) => Math.Round(Math.Max(0, a - b) * scale / elapsed, 1);
        var page = Environment.SystemPageSize;
        return (Rate(major, before.MajorFaults), Rate(refault, before.Refaults),
            reclaim ? Rate(now.Scanned, before.Scanned, page) : null, reclaim ? Rate(now.Stolen, before.Stolen, page) : null);
    }

    // The PID namespace's last allocated PID only grows, so its change counts new processes and threads.
    // Only meaningful with the container's own PID namespace (its PID 1 is in this cgroup).
    private double? TasksStarted()
    {
        var lastPidPath = Path.Combine(_procPath, "sys/kernel/ns_last_pid");
        _ownPidNamespace ??= ReadText("cgroup.procs")?.Split('\n').Contains("1") == true;
        if (_ownPidNamespace != true || !File.Exists(lastPidPath)
            || !long.TryParse(KernelFile.ReadAllText(lastPidPath).Trim(), out var lastPid))
        {
            return null;
        }

        (long LastPid, long Timestamp) now = (lastPid, Stopwatch.GetTimestamp());
        var previous = _previousPid;
        _previousPid = now;
        if (previous is not { } before || lastPid < before.LastPid)
        {
            return null; // first read, or the counter wrapped
        }

        var elapsed = Stopwatch.GetElapsedTime(before.Timestamp, now.Timestamp).TotalSeconds;
        return elapsed > 0 ? Math.Round((lastPid - before.LastPid) / elapsed, 1) : null;
    }

    private double? ReadCpuLimit() =>
        ReadText("cpu.max")?.Split(' ') is [var quota, var period] && long.TryParse(quota, out var q) && long.TryParse(period, out var p) && p > 0
            ? Math.Round((double)q / p, 2)
            : null;

    private record struct IoCounters(long ReadBytes, long WriteBytes, long ReadOps, long WriteOps);

    private Dictionary<string, IoCounters> ReadIo()
    {
        var devices = new Dictionary<string, IoCounters>();
        var text = ReadRaw("io.stat").AsSpan().Trim();
        foreach (var range in text.Split('\n'))
        {
            // "8:0 rbytes=1 wbytes=2 rios=3 wios=4 ...": the device, then its counters by name.
            var line = text[range];
            ReadOnlySpan<char> device = default;
            long readBytes = 0, writeBytes = 0, readOps = 0, writeOps = 0;
            var found = 0;
            foreach (var fieldRange in line.Split(' '))
            {
                var field = line[fieldRange];
                if (field.IsEmpty)
                {
                    continue;
                }

                if (device.IsEmpty)
                {
                    device = field;
                    continue;
                }

                Take(field, "rbytes=", 1, ref readBytes, ref found);
                Take(field, "wbytes=", 2, ref writeBytes, ref found);
                Take(field, "rios=", 4, ref readOps, ref found);
                Take(field, "wios=", 8, ref writeOps, ref found);
            }

            if (device.IsEmpty)
            {
                continue;
            }

            var name = DeviceName(device);
            var counters = new IoCounters(readBytes, writeBytes, readOps, writeOps);
            devices[name] = devices.TryGetValue(name, out var existing)
                ? new IoCounters(existing.ReadBytes + counters.ReadBytes, existing.WriteBytes + counters.WriteBytes,
                    existing.ReadOps + counters.ReadOps, existing.WriteOps + counters.WriteOps)
                : counters;
        }

        return devices;
    }

    // The first field with this name gives the counter: its number, or 0 when it isn't one.
    private static void Take(ReadOnlySpan<char> field, string name, int bit, ref long value, ref int found)
    {
        if ((found & bit) == 0 && field.StartsWith(name, StringComparison.Ordinal))
        {
            found |= bit;
            value = long.TryParse(field[name.Length..], out var number) ? number : 0;
        }
    }

    private string DeviceName(ReadOnlySpan<char> device)
    {
        if (_deviceNames.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(device, out var name))
        {
            return name;
        }

        var majorMinor = device.ToString();
        name = majorMinor;
        try
        {
            var devName = KernelFile.ReadAllLines($"/sys/dev/block/{majorMinor}/uevent").FirstOrDefault(l => l.StartsWith("DEVNAME=", StringComparison.Ordinal));
            if (devName is not null)
            {
                name = devName[8..];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return _deviceNames[majorMinor] = name;
    }

    // One dictionary a file, filled again on each read: what it returns is only good until the next sample.
    private Dictionary<string, long> ReadKeys(string file)
    {
        if (!_keys.TryGetValue(file, out var values))
        {
            _keys[file] = values = [];
        }

        values.Clear();
        var text = ReadRaw(file).AsSpan().Trim();
        foreach (var range in text.Split('\n'))
        {
            // "name value", and nothing else on the line.
            var line = text[range];
            var space = line.IndexOf(' ');
            if (space >= 0 && !line[(space + 1)..].Contains(' ') && long.TryParse(line[(space + 1)..], out var number))
            {
                values[_names.Get(line[..space])] = number;
            }
        }

        return values;
    }

    private long? ReadLong(string file) => long.TryParse(ReadText(file), out var value) ? value : null;

    private string? ReadText(string file) => ReadRaw(file)?.Trim();

    private string? ReadRaw(string file)
    {
        var path = Path.Combine(_root, file);
        return File.Exists(path) ? KernelFile.ReadAllText(path) : null;
    }

    private static int CountHostCores(string procPath)
    {
        var statPath = Path.Combine(procPath, "stat");
        return File.Exists(statPath)
            ? KernelFile.ReadAllLines(statPath).Count(line => line.StartsWith("cpu", StringComparison.Ordinal) && line.Length > 3 && char.IsDigit(line[3]))
            : Environment.ProcessorCount;
    }
}
