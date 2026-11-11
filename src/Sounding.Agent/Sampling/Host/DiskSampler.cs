using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
// A block device's counters in /proc/diskstats.
using Counters = (long SectorsRead, long SectorsWritten, long IoMs, long Reads, long Writes, long ReadMs, long WriteMs, long WeightedMs);

namespace Sounding.Agent.Sampling;

public sealed partial class DiskSampler(IOptions<AgentOptions> options)
{
    private const int SectorSize = 512; // diskstats always counts 512-byte sectors

    private readonly string _procPath = options.Value.ProcPath;
    private static readonly DiskTarget Root = new() { Name = "Root", Path = "/" };

    // Configured disks, or the host disk mounted at HostDiskPath; none without either.
    private readonly IReadOnlyList<DiskTarget> _targets = options.Value.Disks is { Count: > 0 } disks
        ? disks
        : HostDisk(options.Value);

    private Dictionary<string, Counters> _previous = [];
    private long _previousTimestamp;
    private readonly NamePool _names = new();

    public bool HasHostDisk => _targets.Count > 0;

    public IReadOnlyList<DiskVolume> SampleVolumes() => _targets.Select(SampleVolume).ToList();

    // The block devices, and the filesystem holding the container's root (Docker's data disk on Linux).
    public DiskStats Sample()
    {
        var volumes = new List<DiskVolume> { SampleVolume(Root) };
        var devices = SampleDevices();
        return new DiskStats(
            volumes,
            devices,
            Math.Round(devices.Sum(d => d.ReadBytesPerSecond)),
            Math.Round(devices.Sum(d => d.WriteBytesPerSecond)));
    }

    private static IReadOnlyList<DiskTarget> HostDisk(AgentOptions options)
    {
        var mountInfoPath = Path.Combine(options.ProcPath, "self/mountinfo");
        var hostDisk = File.Exists(mountInfoPath)
            ? MountInfo.Read(mountInfoPath).LastOrDefault(m => m.MountPoint == options.HostDiskPath)
            : null;
        return hostDisk is null ? [] : [new DiskTarget { Name = HostDiskName(hostDisk.Source), Path = options.HostDiskPath }];
    }

    private static string HostDiskName(string source) =>
        source is [var letter, ':', ..] && char.IsAsciiLetter(letter) ? $"Host {char.ToUpperInvariant(letter)}:"
        : source.StartsWith("/dev/", StringComparison.Ordinal) ? $"Host disk ({source[5..]})"
        : "Host disk";

    private static DiskVolume SampleVolume(DiskTarget target)
    {
        var name = string.IsNullOrWhiteSpace(target.Name) ? target.Path : target.Name;
        try
        {
            var drive = new DriveInfo(target.Path);
            var total = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
            var used = total - drive.TotalFreeSpace;
            var inodes = StatVfs.Read(target.Path);
            return new DiskVolume(name, target.Path, drive.DriveFormat, total, used, free,
                total > 0 ? Math.Round(100.0 * used / total, 1) : 0, null,
                inodes is { InodesTotal: > 0 } i ? i.InodesTotal : null,
                inodes is { InodesTotal: > 0 } f ? f.InodesFree : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new DiskVolume(name, target.Path, null, 0, 0, 0, 0, ex.Message);
        }
    }

    private List<DiskDevice> SampleDevices()
    {
        var diskStatsPath = Path.Combine(_procPath, "diskstats");
        if (!File.Exists(diskStatsPath))
        {
            return [];
        }

        var timestamp = Stopwatch.GetTimestamp();
        var elapsedSeconds = _previousTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_previousTimestamp, timestamp).TotalSeconds;
        var current = new Dictionary<string, Counters>();
        var devices = new List<DiskDevice>();

        var text = KernelFile.ReadAllText(diskStatsPath).AsSpan();
        Span<Range> parts = stackalloc Range[15];
        foreach (var range in text.Split('\n'))
        {
            var line = text[range];
            var count = line.Split(parts, ' ', StringSplitOptions.RemoveEmptyEntries);
            if (count < 13 || !WholeDevice().IsMatch(line[parts[2]]))
            {
                continue;
            }

            static long Field(ReadOnlySpan<char> line, Range field) => long.Parse(line[field]);
            Counters counters = (Field(line, parts[5]), Field(line, parts[9]), Field(line, parts[12]), Field(line, parts[3]), Field(line, parts[7]),
                Field(line, parts[6]), Field(line, parts[10]), count > 13 ? Field(line, parts[13]) : 0);
            var name = _names.Get(line[parts[2]]);
            current[name] = counters;

            if (elapsedSeconds > 0 && _previous.TryGetValue(name, out var previous))
            {
                devices.Add(new DiskDevice(
                    name,
                    Math.Round(Math.Max(0, counters.SectorsRead - previous.SectorsRead) * SectorSize / elapsedSeconds),
                    Math.Round(Math.Max(0, counters.SectorsWritten - previous.SectorsWritten) * SectorSize / elapsedSeconds),
                    Math.Round(Math.Clamp((counters.IoMs - previous.IoMs) / (elapsedSeconds * 10), 0, 100), 1),
                    Math.Round(Math.Max(0, counters.Reads - previous.Reads) / elapsedSeconds, 1),
                    Math.Round(Math.Max(0, counters.Writes - previous.Writes) / elapsedSeconds, 1),
                    Latency(counters.ReadMs - previous.ReadMs, counters.Reads - previous.Reads),
                    Latency(counters.WriteMs - previous.WriteMs, counters.Writes - previous.Writes),
                    Math.Round(Math.Max(0, counters.WeightedMs - previous.WeightedMs) / (elapsedSeconds * 1000), 2)));
            }
        }

        _previous = current;
        _previousTimestamp = timestamp;
        return devices;
    }

    private static double Latency(long milliseconds, long requests) =>
        requests > 0 ? Math.Round(Math.Max(0, milliseconds) / (double)requests, 2) : 0;

    [GeneratedRegex(@"^(sd[a-z]+|vd[a-z]+|xvd[a-z]+|nvme\d+n\d+|mmcblk\d+)$")]
    private static partial Regex WholeDevice();
}
