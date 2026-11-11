using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Sounding.Agent.Sampling;

public sealed partial class StorageSampler(IOptions<AgentOptions> options, ILogger<StorageSampler> logger)
{
    private static readonly TimeSpan WalkEvery = TimeSpan.FromSeconds(30);
    private readonly string _mountInfoPath = Path.Combine(options.Value.ProcPath, "self/mountinfo");
    private readonly string _procPath = options.Value.ProcPath;
    private readonly string _procsPath = Path.Combine(options.Value.CgroupPath, "cgroup.procs");
    private Dictionary<string, (long Bytes, long Files)>? _previousFolders;

    private const int LargestCount = 10;

    private static readonly TimeSpan GrowthWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinGrowthSpan = TimeSpan.FromMinutes(2);
    private readonly Queue<(long Timestamp, long Bytes)> _sizes = new();

    private static readonly HashSet<string> SystemFileSystems =
        ["proc", "sysfs", "cgroup", "cgroup2", "devpts", "devtmpfs", "mqueue", "securityfs", "debugfs", "tracefs", "configfs", "fusectl", "bpf", "binfmt_misc", "autofs", "pstore", "nsfs"];

    private Task<StorageSample?>? _walk;
    private long _walkFinished;
    private StorageSample? _latest;

    public StorageSample? Sample()
    {
        if (_walk is { IsCompleted: true } finished)
        {
            _walk = null;
            _walkFinished = Stopwatch.GetTimestamp();
            _latest = finished.GetAwaiter().GetResult() is { } sample ? sample with { GrowthBytesPerHour = Growth(sample.UsedBytes) } : null;
        }

        if (_walk is null && (_walkFinished == 0 || Stopwatch.GetElapsedTime(_walkFinished) >= WalkEvery))
        {
            _walk = Task.Run(Walk);
        }

        return _latest;
    }

    private StorageSample? Walk()
    {
        try
        {
            var mounts = MountInfo.Read(_mountInfoPath);
            var mountPoints = mounts.Select(m => m.MountPoint).ToHashSet();
            var folders = new Dictionary<string, (long Bytes, long Files)>(StringComparer.Ordinal);
            var largestFiles = new PriorityQueue<string, long>();
            var (total, count) = Measure("/", mountPoints, folders, largestFiles);

            var largest = folders
                .OrderByDescending(f => f.Value.Bytes)
                .Take(LargestCount)
                .Select(f => new PathSize(f.Key, f.Value.Bytes, f.Value.Files))
                .ToList();
            var volumes = mounts
                .Where(m => IsDataMount(m) && Directory.Exists(m.MountPoint))
                .Select(m => Volume(m, mountPoints))
                .ToList();
            // Folders that grew since the last walk.
            var growing = _previousFolders is null ? null : folders
                .Select(f => new PathSize(f.Key, f.Value.Bytes - _previousFolders.GetValueOrDefault(f.Key).Bytes, f.Value.Files - _previousFolders.GetValueOrDefault(f.Key).Files))
                .Where(f => f.Bytes > 0)
                .OrderByDescending(f => f.Bytes)
                .Take(LargestCount)
                .ToList();
            _previousFolders = folders;
            var files = new List<FileSize>();
            while (largestFiles.TryDequeue(out var path, out var bytes))
            {
                files.Insert(0, new FileSize(path, bytes));
            }

            return new StorageSample(total, count, largest, volumes, StatVfs.Read("/")?.FreeBytes, null, growing, files, DeletedFiles());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning("Could not measure container storage: {Message}", ex.Message);
            return null;
        }
    }

    private static (long Bytes, long Files) Measure(
        string root, HashSet<string> mountPoints, Dictionary<string, (long Bytes, long Files)>? folders, PriorityQueue<string, long>? largestFiles = null)
    {
        var files = new FileSystemEnumerable<string>(root, (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
        {
            // Not entry.Attributes for files: that costs a second stat per file; statx below tells symlinks apart.
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                (entry.Attributes & FileAttributes.ReparsePoint) == 0 && !mountPoints.Contains(entry.ToFullPath()),
        };

        long total = 0, count = 0;
        var linked = new HashSet<(uint, uint, ulong)>();
        var byFolder = folders?.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var path in files)
        {
            if (mountPoints.Contains(path) || Statx(AtCurrentDirectory, path, AtSymlinkNoFollow, StatxBasicStats, out var stat) != 0)
            {
                continue; // deleted since it was listed
            }

            if ((stat.Mode & FileTypeMask) == SymbolicLink)
            {
                continue;
            }

            if (stat.Links > 1 && !linked.Add((stat.DeviceMajor, stat.DeviceMinor, stat.Inode)))
            {
                continue;
            }

            total += (long)stat.Size;
            count++;
            if (largestFiles is not null && (largestFiles.Count < LargestCount || largestFiles.TryPeek(out _, out var smallest) && (long)stat.Size > smallest))
            {
                if (largestFiles.Count == LargestCount)
                {
                    largestFiles.Dequeue();
                }

                largestFiles.Enqueue(path, (long)stat.Size);
            }
            if (byFolder is { } lookup)
            {
                var folder = Folder(path);
                lookup.TryGetValue(folder, out var sum);
                lookup[folder] = (sum.Bytes + (long)stat.Size, sum.Files + 1);
            }
        }

        return (total, count);
    }

    // Files deleted while a process still has them open: their space is only freed when the process closes them.
    // Another user's descriptors can't be read without CAP_SYS_PTRACE, so theirs aren't seen.
    private List<DeletedFile> DeletedFiles()
    {
        var found = new List<DeletedFile>();
        var seen = new HashSet<(uint, uint, ulong)>();
        string[] pids;
        try
        {
            pids = KernelFile.ReadAllLines(_procsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return found;
        }

        foreach (var pid in pids)
        {
            try
            {
                string? name = null;
                foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(_procPath, pid, "fd")))
                {
                    if (new FileInfo(fd).LinkTarget is not { } target || !target.EndsWith(" (deleted)", StringComparison.Ordinal) || !target.StartsWith('/')
                        || target.StartsWith("/memfd:", StringComparison.Ordinal) || Statx(AtCurrentDirectory, fd, 0, StatxBasicStats, out var stat) != 0
                        || (stat.Mode & FileTypeMask) != RegularFile || !seen.Add((stat.DeviceMajor, stat.DeviceMinor, stat.Inode)))
                    {
                        continue;
                    }

                    name ??= KernelFile.ReadAllText(Path.Combine(_procPath, pid, "comm")).Trim();
                    found.Add(new DeletedFile(int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture), name, target[..^10], (long)stat.Size));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // exited, or another user's process
            }
        }

        return found.OrderByDescending(f => f.Bytes).Take(LargestCount).ToList();
    }

    private static bool IsDataMount(Mount m) =>
        m.MountPoint != "/" && !SystemFileSystems.Contains(m.FileSystemType)
        && !new[] { "/proc", "/sys", "/dev" }.Any(p => m.MountPoint == p || m.MountPoint.StartsWith(p + "/", StringComparison.Ordinal));

    private static MountUsage Volume(Mount m, HashSet<string> mountPoints)
    {
        var fs = StatVfs.Read(m.MountPoint);
        long? used = null;
        if (!m.ReadOnly)
        {
            var nested = mountPoints.Where(p => p != m.MountPoint).ToHashSet();
            used = Measure(m.MountPoint, nested, null).Bytes;
        }

        return new MountUsage(m.MountPoint, SourceName(m), m.FileSystemType, m.ReadOnly,
            fs?.TotalBytes, fs?.FreeBytes, fs is { InodesTotal: > 0 } i ? i.InodesTotal : null, fs is { InodesTotal: > 0 } f ? f.InodesFree : null, used);
    }

    private static string SourceName(Mount m)
    {
        if (VolumeRoot().Match(m.Root) is { Success: true } volume)
        {
            var name = volume.Groups[1].Value;
            return name.Length == 64 && name.All(char.IsAsciiHexDigitLower) ? $"anonymous volume {name[..12]}" : $"volume {name}";
        }

        if (m.Source is [var letter, ':', ..] && char.IsAsciiLetter(letter))
        {
            return $"{char.ToUpperInvariant(letter)}:\\{m.Root.TrimStart('/').Replace('/', '\\')}";
        }

        return m.Root == "/" ? m.Source : $"{m.Source}:{m.Root}";
    }

    [GeneratedRegex(@"/volumes/([^/]+)/_data$")]
    private static partial Regex VolumeRoot();

    private double? Growth(long bytes)
    {
        var now = Stopwatch.GetTimestamp();
        _sizes.Enqueue((now, bytes));
        while (_sizes.Count > 1 && Stopwatch.GetElapsedTime(_sizes.Peek().Timestamp, now) > GrowthWindow)
        {
            _sizes.Dequeue();
        }

        var (since, before) = _sizes.Peek();
        var span = Stopwatch.GetElapsedTime(since, now);
        return span >= MinGrowthSpan ? Math.Round((bytes - before) / span.TotalHours) : null;
    }

    private static ReadOnlySpan<char> Folder(string path)
    {
        var second = path.IndexOf('/', 1);
        if (second < 0)
        {
            return "/";
        }

        var third = path.IndexOf('/', second + 1);
        return third < 0 ? path.AsSpan(0, second) : path.AsSpan(0, third);
    }

    private const int AtCurrentDirectory = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;
    private const ushort FileTypeMask = 0xF000;
    private const ushort SymbolicLink = 0xA000;
    private const ushort RegularFile = 0x8000;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxResult
    {
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [DllImport("libc", EntryPoint = "statx")]
    private static extern int Statx(int directoryFd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out StatxResult result);
}

public sealed record StorageSample(
    long UsedBytes,
    long FileCount,
    IReadOnlyList<PathSize> LargestPaths,
    IReadOnlyList<MountUsage> Mounts,
    long? FreeBytes,
    double? GrowthBytesPerHour = null,
    IReadOnlyList<PathSize>? GrowingPaths = null,
    IReadOnlyList<FileSize>? LargestFiles = null,
    IReadOnlyList<DeletedFile>? DeletedFiles = null);
