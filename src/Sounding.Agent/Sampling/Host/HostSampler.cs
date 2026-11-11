using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Sounding.Agent.Sampling;

// What this agent reports about its host, by the roles it holds: the snapshot (Sampler), and the parts only some
// agents can see, stored apart and merged into the snapshot by the database. A role's first tick is a baseline.
public sealed record HostReport(HostSnapshot? Snapshot, IReadOnlyDictionary<string, string> Parts);

public sealed class HostSampler(
    CpuSampler cpu,
    MemorySampler memory,
    DiskSampler disk,
    NetworkSampler network,
    GpuSampler gpu,
    SystemSampler system,
    TemperatureSampler temperatures,
    IOptions<AgentOptions> options,
    ILogger<HostSampler> logger)
{
    private readonly PressureTracker _pressure = new(
        Path.Combine(options.Value.ProcPath, "pressure/cpu"),
        Path.Combine(options.Value.ProcPath, "pressure/memory"),
        Path.Combine(options.Value.ProcPath, "pressure/io"),
        cpuFullMeaningful: false);

    private readonly string _fileNrPath = Path.Combine(options.Value.ProcPath, "sys/fs/file-nr");
    private readonly HashSet<string> _failing = [];
    private IReadOnlySet<HostRole> _held = new HashSet<HostRole>();

    private static readonly NetworkStats NoNetwork = new(false, "Not available", [], 0, 0);
    private static readonly GpuStats NoGpu = new(false, "Not available", []);

    // The roles this agent can take.
    public IReadOnlyList<HostRole> Roles =>
    [
        HostRole.Sampler,
        .. network.CanSeeHost ? [HostRole.Network] : Array.Empty<HostRole>(),
        .. gpu.HasGpu ? [HostRole.Gpu] : Array.Empty<HostRole>(),
        .. disk.HasHostDisk ? [HostRole.Disk] : Array.Empty<HostRole>(),
    ];

    public HostReport Sample(IReadOnlySet<HostRole> held)
    {
        var baseline = held.Except(_held).ToHashSet();
        _held = held;
        if (!held.Contains(HostRole.Network))
        {
            network.Reset();
        }

        var parts = new Dictionary<string, string>();
        if (held.Contains(HostRole.Network) && Try("network", network.Sample) is { } networkStats)
        {
            parts["network"] = JsonSerializer.Serialize(networkStats, SoundingJson.Stored.NetworkStats);
        }

        if (!held.Contains(HostRole.Gpu))
        {
            gpu.Stop();
        }
        else if (gpu.Sample() is { Available: true } gpuStats)
        {
            parts["gpu"] = JsonSerializer.Serialize(gpuStats, SoundingJson.Stored.GpuStats);
        }

        if (held.Contains(HostRole.Disk) && Try("host disk", disk.SampleVolumes) is { } volumes)
        {
            parts["disk"] = JsonSerializer.Serialize(volumes, SoundingJson.Stored.IReadOnlyListDiskVolume);
        }

        if (!held.Contains(HostRole.Sampler))
        {
            return new HostReport(null, parts);
        }

        var snapshot = new HostSnapshot(
            Try("system", system.Sample) ?? new SystemStats("", null, null, null),
            Try("cpu", cpu.Sample),
            Try("memory", memory.Sample),
            NoGpu,
            Try("disk", disk.Sample) ?? new DiskStats([], [], 0, 0),
            NoNetwork,
            ContainerTotals.Empty,
            Try("pressure", _pressure.Sample),
            Try("temperatures", temperatures.Sample),
            Try("file handles", ReadFileHandles));
        return new HostReport(baseline.Contains(HostRole.Sampler) ? null : snapshot, parts);
    }

    private FileHandles? ReadFileHandles() =>
        File.Exists(_fileNrPath) && KernelFile.ReadAllText(_fileNrPath).Split((char[])['\t', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries) is [var allocated, var free, var max]
            ? new FileHandles(
                long.Parse(allocated, CultureInfo.InvariantCulture) - long.Parse(free, CultureInfo.InvariantCulture),
                long.Parse(max, CultureInfo.InvariantCulture))
            : null;

    private T? Try<T>(string part, Func<T?> sample) where T : class
    {
        try
        {
            var value = sample();
            if (_failing.Remove(part))
            {
                logger.LogInformation("Host {Part} readable again", part);
            }

            return value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_failing.Add(part))
            {
                logger.LogWarning("Host {Part} not available: {Message}", part, ex.Message);
            }

            return null;
        }
    }
}
