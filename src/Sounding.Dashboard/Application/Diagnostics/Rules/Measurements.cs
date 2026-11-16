using System.Globalization;
using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.Network;

namespace Sounding.Dashboard.Application.Diagnostics.Rules;

// The figures behind "nothing is wrong" (or next to what is): how fast and how reliable requests are and how close the
// busiest containers are to their limits. A reader told "it feels slow" needs a number to answer with, or it keeps
// looking.
internal static class Measurements
{
    public static IReadOnlyList<string> Of(IReadOnlyList<HostState> hosts, Scope scope, IReadOnlyList<ContainerNetwork> networks)
    {
        var names = new Dictionary<string, string>();
        (ContainerState? Container, double Share) cpu = default, memory = default;
        foreach (var host in hosts)
        {
            foreach (var container in host.Containers)
            {
                names[container.ContainerId] = container.Name ?? container.ContainerId;
                if (!scope.Includes(container.ContainerId) || container.Usage is not { } usage) continue;
                if (usage.CpuLimitCores is > 0 and var cpuLimit && usage.CpuCores / cpuLimit >= cpu.Share) cpu = (container, usage.CpuCores / cpuLimit);
                if (usage.MemoryLimitBytes is > 0 and var memoryLimit && (double)usage.MemoryBytes / memoryLimit >= memory.Share) memory = (container, (double)usage.MemoryBytes / memoryLimit);
            }
        }

        int kinds = 0, failing = 0, slow = 0;
        (RequestStats? Request, string Caller) slowest = default;
        foreach (var network in networks)
        {
            if (!scope.Includes(network.ContainerId) || !names.TryGetValue(network.ContainerId, out var caller)) continue;
            foreach (var r in network.Network.Requests ?? [])
            {
                if (SoundingTraffic.IsOwn(r) || r.RequestsPerSecond < 0.01) continue;
                kinds++;
                if (r.ErrorsPerSecond * 100 / r.RequestsPerSecond >= 5) failing++;
                if (r.P95Ms >= 1000) slow++;
                if (r.P95Ms is { } p95 && p95 >= (slowest.Request?.P95Ms ?? 0)) slowest = (r, caller);
            }
        }

        var measured = new List<string>(3);
        measured.Add(kinds == 0 ? "No requests between services were seen in the last minute."
            : string.Create(CultureInfo.InvariantCulture, $"Requests in the last minute: {kinds} kinds seen, {failing} failing, {slow} slow (p95 of 1 s or more).")
                + (slowest.Request is { } s ? string.Create(CultureInfo.InvariantCulture, $" The slowest is {s.Protocol} {s.Operation} at {slowest.Caller} with a p95 of {s.P95Ms:0.#} ms.") : ""));
        if (cpu.Container is { } busiest)
        {
            measured.Add(string.Create(CultureInfo.InvariantCulture, $"Closest to its CPU limit: {names[busiest.ContainerId]} at {cpu.Share * 100:0} % ({busiest.Usage!.CpuCores:0.##} of {busiest.Usage.CpuLimitCores:0.##} cores)."));
        }

        if (memory.Container is { } fullest)
        {
            measured.Add(string.Create(CultureInfo.InvariantCulture, $"Closest to its memory limit: {names[fullest.ContainerId]} at {memory.Share * 100:0} %."));
        }

        if (scope.Container is null)
        {
            foreach (var host in hosts) Gpu(host, measured);
        }

        return measured;
    }

    private const double GpuMemoryShown = 80;

    // A GPU's memory filled by programs that keep what they load resident is a figure, not a problem (HostRules
    // reports it once allocations fail). Who holds it matters to the reader: the monitored containers, or programs
    // outside them, such as a model the reader runs on the same machine.
    private static void Gpu(HostState host, List<string> measured)
    {
        if (host.Latest?.Snapshot?.Gpu.Devices is not { Count: > 0 } devices || !devices.Any(d => d.MemoryUsagePercent >= GpuMemoryShown)) return;
        long used = 0, total = 0, monitored = 0;
        foreach (var device in devices)
        {
            used += device.MemoryUsedBytes ?? 0;
            total += device.MemoryTotalBytes ?? 0;
        }

        var failing = false;
        foreach (var container in host.Containers)
        {
            monitored += container.Usage?.GpuMemoryBytes ?? 0;
            failing |= container.Usage?.Detail?.GpuAllocationFailures is > 0;
        }

        if (total <= 0) return;
        var outside = Math.Max(0, used - monitored);
        measured.Add(string.Create(CultureInfo.InvariantCulture,
            $"GPU memory on {host.DisplayName}: {DiagnosisText.Bytes(used)} of {DiagnosisText.Bytes(total)} in use ({used * 100d / total:0} %), ")
            + $"{DiagnosisText.Bytes(monitored)} by the monitored containers and {DiagnosisText.Bytes(outside)} by processes outside them."
            + (failing ? " Some GPU memory allocations failed: see the findings." : " No GPU memory allocation has failed."));
    }
}
