using System.Diagnostics;

namespace Sounding.Dashboard.Api;

public sealed record FleetUpdate(
    DateTimeOffset Timestamp,
    IReadOnlyList<HostSummary> Hosts,
    IReadOnlyDictionary<string, HostSample> Details,
    double AgeMilliseconds);

public sealed record HostSummary(
    string HostId,
    string DisplayName,
    bool Online,
    DateTimeOffset LastSeen,
    string? LeaderContainerId,
    DateTimeOffset? SampledAt,
    string? Kernel,
    double? UptimeSeconds,
    double? CpuPercent,
    int? LogicalCores,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? MemoryPercent,
    IReadOnlyList<GpuSummary> Gpus,
    double? DiskReadBytesPerSecond,
    double? DiskWriteBytesPerSecond,
    double? NetReceiveBytesPerSecond,
    double? NetTransmitBytesPerSecond,
    ContainerTotals? ContainerTotals,
    IReadOnlyList<VolumeSummary> Volumes,
    IReadOnlyList<ContainerState> Containers)
{
    public static HostSummary From(HostState host)
    {
        var s = host.Latest?.Snapshot;
        var network = s?.Network is { Available: true } n ? n : null;
        var validVolumes = s?.Disk.Volumes.Where(v => v.Error is null).ToList() ?? [];
        if (validVolumes.Count > 1 && validVolumes.Any(v => v.Format != "overlay" && v.Path != "/"))
        {
            validVolumes = validVolumes.Where(v => v.Format != "overlay" && v.Path != "/").ToList();
        }

        return new HostSummary(
            host.HostId,
            host.DisplayName,
            host.Online,
            host.LastSeen,
            host.LeaderContainerId,
            host.Latest?.Timestamp,
            s?.System.Kernel,
            s?.System.UptimeSeconds,
            s?.Cpu?.UsagePercent,
            s?.Cpu?.LogicalCores,
            s?.Memory?.UsedBytes,
            s?.Memory?.TotalBytes,
            s?.Memory?.UsagePercent,
            s?.Gpu.Devices.Select(d => new GpuSummary(
                d.Index, d.Name, d.UtilizationPercent, d.MemoryUsagePercent, d.MemoryUsedBytes, d.MemoryTotalBytes, d.TemperatureCelsius)).ToList() ?? [],
            s?.Disk.ReadBytesPerSecond,
            s?.Disk.WriteBytesPerSecond,
            network?.ReceiveBytesPerSecond,
            network?.TransmitBytesPerSecond,
            s?.Containers,
            [.. validVolumes.Select(v => new VolumeSummary(v.Name, v.Path, v.UsedBytes, v.TotalBytes, v.UsagePercent))],
            host.Containers);
    }
}

public sealed record VolumeSummary(string Name, string Path, long UsedBytes, long TotalBytes, double UsagePercent);

public sealed record GpuSummary(
    int Index,
    string Name,
    double? UtilizationPercent,
    double? MemoryUsagePercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? TemperatureCelsius);

public sealed class FleetView(FleetState state, long sampledAt)
{
    private readonly IReadOnlyList<HostSummary> _hosts = [.. state.Hosts.Select(host => HostSummary.From(host) with
    {
        Containers = [.. host.Containers.Select(container => container with
        {
            Usage = container.Usage is { } usage ? usage with { Detail = null } : null,
        })],
    })];
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ContainerState>> _containers =
        state.Hosts.ToDictionary(host => host.HostId, host => host.Containers);

    public FleetUpdate ForHosts(IReadOnlySet<string> hostIds, IReadOnlySet<string>? containerIds = null) => new(
        state.Timestamp,
        hostIds.Count == 0 ? _hosts : [.. _hosts.Select(host => hostIds.Contains(host.HostId)
            ? host with
            {
                Containers = containerIds is not { Count: > 0 } ? _containers[host.HostId]
                    : [.. host.Containers.Select(container => containerIds.Contains(container.ContainerId)
                        ? _containers[host.HostId].First(full => full.ContainerId == container.ContainerId) : container)],
            } : host)],
        hostIds.Count == 0 ? []
            : state.Hosts.Where(h => h.Latest is not null && hostIds.Contains(h.HostId)).ToDictionary(h => h.HostId, h => h.Latest!),
        Stopwatch.GetElapsedTime(sampledAt).TotalMilliseconds);
}
