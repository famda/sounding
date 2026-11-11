using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using Microsoft.Extensions.Options;

namespace Sounding.Agent.Sampling;

public sealed class ProcessSampler(IOptions<AgentOptions> options)
{
    private const double TicksPerSecond = 100;

    private const int MaxProcesses = 100;

    // Threads are listed for the busiest processes only: one read per thread.
    private const int ThreadedProcesses = 3;
    private const int HotThreadsListed = 5;

    // What Docker grants every container; anything beyond is shown.
    private const ulong DockerCapabilities = 0xA80425FB;

    private static readonly string[] CapabilityNames =
    [
        "CHOWN", "DAC_OVERRIDE", "DAC_READ_SEARCH", "FOWNER", "FSETID", "KILL", "SETGID", "SETUID", "SETPCAP", "LINUX_IMMUTABLE",
        "NET_BIND_SERVICE", "NET_BROADCAST", "NET_ADMIN", "NET_RAW", "IPC_LOCK", "IPC_OWNER", "SYS_MODULE", "SYS_RAWIO", "SYS_CHROOT",
        "SYS_PTRACE", "SYS_PACCT", "SYS_ADMIN", "SYS_BOOT", "SYS_NICE", "SYS_RESOURCE", "SYS_TIME", "SYS_TTY_CONFIG", "MKNOD", "LEASE",
        "AUDIT_WRITE", "AUDIT_CONTROL", "SETFCAP", "MAC_OVERRIDE", "MAC_ADMIN", "SYSLOG", "WAKE_ALARM", "BLOCK_SUSPEND", "AUDIT_READ",
        "PERFMON", "BPF", "CHECKPOINT_RESTORE",
    ];

    private Dictionary<(int Pid, int Tid), long> _previousThreads = [];

    private readonly string _procsPath = Path.Combine(options.Value.CgroupPath, "cgroup.procs");
    private readonly string _procPath = options.Value.ProcPath;

    private Dictionary<(int Pid, long Start), Counters> _previous = [];
    private long _previousTimestamp;
    private DateTimeOffset? _bootTime;

    private readonly record struct Counters(long CpuTicks, long? WaitNs, long? Voluntary, long? Involuntary, long? ReadBytes, long? WriteBytes);

    private record struct Status(
        long Rss, string? Uid, long? Voluntary, long? Involuntary, long? Anon, long? File, long? Shmem, long? Swap, ulong? Capabilities, int? Seccomp);

    public long? OpenFilesTotal { get; private set; }

    public int OpenFilesUncounted { get; private set; }

    // Whether the last sample listed every process, not only the busiest.
    public bool Complete { get; private set; }

    public IReadOnlyList<ProcessUsage> Sample(IReadOnlyDictionary<int, ProcessGpu> gpu)
    {
        var timestamp = Stopwatch.GetTimestamp();
        var elapsed = _previousTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_previousTimestamp, timestamp).TotalSeconds;
        var current = new Dictionary<(int, long), Counters>();
        var processes = new List<ProcessUsage>();
        Span<Range> fields = stackalloc Range[21];

        var pids = KernelFile.TryReadAllLines(_procsPath) ?? [];
        // With more processes than are listed, what only the list shows is read after the cut, for those that stay.
        var describeLater = pids.Length > MaxProcesses;

        foreach (var line in pids)
        {
            if (!int.TryParse(line, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            var directory = ProcessDirectory(pid);
            if (Read(directory, "stat") is not { } stat)
            {
                continue; // exited since cgroup.procs was read
            }

            var close = stat.LastIndexOf(')');
            var open = stat.IndexOf('(');
            if (open < 0 || close < open || close + 2 > stat.Length)
            {
                continue;
            }

            var values = stat.AsSpan(close + 2);
            if (values.Split(fields, ' ') < 20)
            {
                continue;
            }

            var start = long.Parse(values[fields[19]], CultureInfo.InvariantCulture);
            var status = ReadStatus(directory);
            var io = ReadIo(directory);
            var now = new Counters(
                long.Parse(values[fields[11]], CultureInfo.InvariantCulture) + long.Parse(values[fields[12]], CultureInfo.InvariantCulture),
                ReadWaitNs(directory),
                status.Voluntary,
                status.Involuntary,
                io.Read,
                io.Write);
            current[(pid, start)] = now;
            Counters before = default;
            var hasBefore = elapsed > 0 && _previous.TryGetValue((pid, start), out before);

            double? Rate(long? a, long? b, double scale = 1) =>
                hasBefore && a is { } x && b is { } y ? Math.Round(Math.Max(0, x - y) * scale / elapsed, 2) : null;

            gpu.TryGetValue(pid, out var gpuUsage);
            // Named after its main thread until Describe reads the command line.
            var process = new ProcessUsage(
                pid,
                stat[(open + 1)..close],
                null,
                status.Uid is null ? null : UserNames.Of(status.Uid),
                hasBefore ? Math.Round(Math.Max(0, now.CpuTicks - before.CpuTicks) / TicksPerSecond / elapsed, 3) : 0,
                status.Rss,
                int.Parse(values[fields[17]], CultureInfo.InvariantCulture),
                gpuUsage?.Percent,
                gpuUsage?.MemoryBytes,
                gpuUsage?.PeakMemoryBytes,
                values[fields[0]].ToString(),
                Rate(now.WaitNs, before.WaitNs, 1e-6),
                Rate(now.Voluntary, before.Voluntary),
                Rate(now.Involuntary, before.Involuntary),
                PssBytes: null,
                OomScore: null,
                Rate(now.ReadBytes, before.ReadBytes),
                Rate(now.WriteBytes, before.WriteBytes),
                OpenFiles(directory),
                OpenFilesLimit: null,
                BootTime() is { } boot ? boot.AddSeconds(start / TicksPerSecond) : null,
                Math.Round(now.CpuTicks / TicksPerSecond, 2),
                now.ReadBytes,
                now.WriteBytes,
                ParentPid: int.Parse(values[fields[1]], CultureInfo.InvariantCulture),
                AnonBytes: status.Anon,
                FileBytes: status.File,
                ShmemBytes: status.Shmem,
                SwapBytes: status.Swap,
                Capabilities: Extra(status.Capabilities),
                Unconfined: status.Seccomp == 0 ? true : null);
            processes.Add(describeLater ? process : Describe(process));
        }

        var agent = processes.Find(p => p.Pid == Environment.ProcessId);
        AddHotThreads(processes, elapsed);
        _previous = current;
        _previousTimestamp = timestamp;
        OpenFilesTotal = processes.Any(p => p.OpenFiles is not null) ? processes.Sum(p => (long)(p.OpenFiles ?? 0)) : null;
        OpenFilesUncounted = processes.Count(p => p.OpenFiles is null);
        Complete = processes.Count <= MaxProcesses;
        var listed = processes
            .OrderByDescending(p => p.CpuCores)
            .ThenByDescending(p => p.MemoryBytes)
            .Take(MaxProcesses)
            .ToList();
        if (describeLater)
        {
            for (var i = 0; i < listed.Count; i++)
            {
                listed[i] = Describe(listed[i]);
            }
        }

        MarkSounding(listed, agent);
        return listed;
    }

    // What only the list shows of a process: its command line and the name that gives, PSS, the OOM score and
    // the open files limit.
    private ProcessUsage Describe(ProcessUsage process)
    {
        var directory = ProcessDirectory(process.Pid);
        var arguments = Read(directory, "cmdline") ?? "";
        var commandLine = arguments.Replace('\0', ' ').Trim();
        var threadName = process.Name;
        var name = Executable(arguments) ?? threadName;
        return process with
        {
            Name = name,
            CommandLine = commandLine.Length == 0 ? null : commandLine.Length > 300 ? commandLine[..300] : commandLine,
            PssBytes = ReadPss(directory),
            OomScore = int.TryParse(Read(directory, "oom_score")?.Trim(), CultureInfo.InvariantCulture, out var oom) ? oom : null,
            OpenFilesLimit = FilesLimit(directory),
            ThreadName = threadName == name ? null : threadName,
        };
    }

    private string ProcessDirectory(int pid) => Path.Combine(_procPath, pid.ToString(CultureInfo.InvariantCulture));

    private DateTimeOffset? BootTime()
    {
        if (_bootTime is null)
        {
            var line = (KernelFile.TryReadAllLines(Path.Combine(_procPath, "stat")) ?? []).FirstOrDefault(l => l.StartsWith("btime ", StringComparison.Ordinal));
            if (long.TryParse(line?[6..], CultureInfo.InvariantCulture, out var seconds))
            {
                _bootTime = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }

        return _bootTime;
    }

    // Readable for every process, unlike PSS and I/O: the resident memory split, capabilities and seccomp.
    private static Status ReadStatus(string directory)
    {
        var status = new Status();
        var text = (Read(directory, "status") ?? "").AsSpan();
        foreach (var range in text.Split('\n'))
        {
            var line = text[range];
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var value = line[(colon + 1)..].Trim();
            switch (line[..colon])
            {
                case "VmRSS": status.Rss = Kilobytes(value); break;
                case "RssAnon": status.Anon = Kilobytes(value); break;
                case "RssFile": status.File = Kilobytes(value); break;
                case "RssShmem": status.Shmem = Kilobytes(value); break;
                case "VmSwap": status.Swap = Kilobytes(value); break;
                case "Uid": status.Uid = FirstWord(value).ToString(); break;
                case "voluntary_ctxt_switches": status.Voluntary = long.Parse(value, CultureInfo.InvariantCulture); break;
                case "nonvoluntary_ctxt_switches": status.Involuntary = long.Parse(value, CultureInfo.InvariantCulture); break;
                case "CapEff": status.Capabilities = ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture); break;
                case "Seccomp": status.Seccomp = int.Parse(value, CultureInfo.InvariantCulture); break;
            }
        }

        return status;
    }

    // The program's own name: argv[0] without its directory ("postgres: checkpointer" keeps "postgres"). The kernel's
    // name for the process (comm) is the main thread's, which a program may rename ("MainThread").
    private static string? Executable(string arguments)
    {
        var first = arguments.AsSpan(0, Math.Max(0, arguments.IndexOf('\0') is var end and >= 0 ? end : arguments.Length));
        var space = first.IndexOf(' ');
        if (space >= 0)
        {
            first = first[..space];
        }

        first = first[(first.LastIndexOf('/') + 1)..].TrimStart('-').TrimEnd(':');
        return first.IsEmpty ? null : first.ToString();
    }

    private static IReadOnlyList<string>? Extra(ulong? capabilities)
    {
        var extra = (capabilities ?? 0) & ~DockerCapabilities;
        return extra == 0 ? null : CapabilityNames.Where((_, bit) => (extra & (1UL << bit)) != 0).ToList();
    }

    // The launcher, the agent and the helpers the agent starts.
    private static void MarkSounding(List<ProcessUsage> processes, ProcessUsage? agent)
    {
        if (agent is null)
        {
            return;
        }

        for (var i = 0; i < processes.Count; i++)
        {
            var p = processes[i];
            if (p.Pid == agent.Pid || p.ParentPid == agent.Pid || (p.Pid == agent.ParentPid && p.CommandLine?.StartsWith("/opt/sounding/run", StringComparison.Ordinal) == true))
            {
                processes[i] = p with { Sounding = true };
            }
        }
    }

    // The busiest threads of the busiest processes, by CPU since the last sample.
    private void AddHotThreads(List<ProcessUsage> processes, double elapsed)
    {
        var current = new Dictionary<(int, int), long>();
        Span<Range> fields = stackalloc Range[14];
        var busiest = processes.Where(p => p.Threads > 1 && p.CpuCores >= 0.01).OrderByDescending(p => p.CpuCores).Take(ThreadedProcesses).ToList();
        foreach (var process in busiest)
        {
            var threads = new List<ThreadUsage>();
            var tasks = Path.Combine(ProcessDirectory(process.Pid), "task");
            foreach (var task in Tasks(tasks))
            {
                if (KernelFile.TryReadAllText(Path.Combine(tasks, task, "stat")) is not { } stat)
                {
                    continue; // the thread ended
                }

                var close = stat.LastIndexOf(')');
                if (close < 0 || close + 2 > stat.Length) continue;
                var values = stat.AsSpan(close + 2);
                if (values.Split(fields, ' ') < 13 || !int.TryParse(task, CultureInfo.InvariantCulture, out var tid))
                {
                    continue;
                }

                var ticks = long.Parse(values[fields[11]], CultureInfo.InvariantCulture) + long.Parse(values[fields[12]], CultureInfo.InvariantCulture);
                current[(process.Pid, tid)] = ticks;
                if (elapsed > 0 && _previousThreads.TryGetValue((process.Pid, tid), out var before) && ticks > before)
                {
                    threads.Add(new ThreadUsage(tid, stat[(stat.IndexOf('(') + 1)..close], Math.Round((ticks - before) / TicksPerSecond / elapsed, 3)));
                }
            }

            if (threads.Count > 0)
            {
                var i = processes.IndexOf(process);
                processes[i] = process with { HotThreads = threads.OrderByDescending(t => t.CpuCores).Take(HotThreadsListed).ToList() };
            }
        }

        _previousThreads = current;
    }

    private static IEnumerable<string> Tasks(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory).Select(Path.GetFileName)!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long? ReadWaitNs(string directory)
    {
        var text = (Read(directory, "schedstat") ?? "").AsSpan();
        Span<Range> fields = stackalloc Range[3];
        return text.Split(fields, ' ') >= 2 && long.TryParse(text[fields[1]], CultureInfo.InvariantCulture, out var ns) ? ns : null;
    }

    private static (long? Read, long? Write) ReadIo(string directory)
    {
        long? read = null, write = null;
        var text = (Read(directory, "io") ?? "").AsSpan();
        foreach (var range in text.Split('\n'))
        {
            var line = text[range];
            if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
            {
                read = long.Parse(line[11..].Trim(), CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
            {
                write = long.Parse(line[12..].Trim(), CultureInfo.InvariantCulture);
            }
        }

        return (read, write);
    }

    private static long? ReadPss(string directory)
    {
        var text = (Read(directory, "smaps_rollup") ?? "").AsSpan();
        foreach (var range in text.Split('\n'))
        {
            var line = text[range];
            if (line.StartsWith("Pss:", StringComparison.Ordinal))
            {
                return Kilobytes(line[4..]);
            }
        }

        return null;
    }

    private static readonly EnumerationOptions EveryEntry = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    // Counted without building a path string for each descriptor.
    private static int? OpenFiles(string directory)
    {
        try
        {
            var count = 0;
            foreach (var _ in new FileSystemEnumerable<bool>(Path.Combine(directory, "fd"), static (ref FileSystemEntry _) => true, EveryEntry))
            {
                count++;
            }

            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // another user's process
        }
    }

    // The soft "Max open files" limit.
    private static long? FilesLimit(string directory)
    {
        var text = (Read(directory, "limits") ?? "").AsSpan();
        foreach (var range in text.Split('\n'))
        {
            var line = text[range];
            if (line.StartsWith("Max open files", StringComparison.Ordinal))
                return long.TryParse(FirstWord(line[14..].Trim()), CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        return null;
    }

    private static ReadOnlySpan<char> FirstWord(ReadOnlySpan<char> value) =>
        value.IndexOfAny(' ', '\t') is var end and >= 0 ? value[..end] : value;

    private static long Kilobytes(ReadOnlySpan<char> value) =>
        long.Parse(value.Trim().TrimEnd("kB").Trim(), CultureInfo.InvariantCulture) * 1024;

    private static string? Read(string directory, string file) => KernelFile.TryReadAllText(Path.Combine(directory, file));
}
