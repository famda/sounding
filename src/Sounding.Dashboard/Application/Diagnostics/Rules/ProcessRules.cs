using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.History;
using Sounding.Dashboard.Application.Network;
using static Sounding.Dashboard.Application.Diagnostics.Rules.DiagnosisText;

namespace Sounding.Dashboard.Application.Diagnostics.Rules;

// The processes of a host's containers, now and over the last half hour: stuck in I/O, zombies, open files against
// their limit, and memory that keeps growing.
internal static class ProcessRules
{
    public static readonly TimeSpan GrowthWindow = TimeSpan.FromMinutes(30);
    private const double HeadroomHours = 24;

    // Now is the fleet's clock (the database's), which process start times are compared with.
    public static void Check(
        HostState host, Scope scope, IReadOnlyList<ContainerProcesses> live, IReadOnlyList<ContainerProcessWindow> window, DateTimeOffset now, List<Finding> findings)
    {
        var containers = host.Containers.ToDictionary(c => c.ContainerId);
        void Add(string containerId, string severity, string code, string message, Lookup[]? next) =>
            findings.Add(new Finding(severity, code, ResourceTarget.Container, containerId,
                containers.GetValueOrDefault(containerId)?.Name, host.HostId, message, next));

        foreach (var container in live)
        {
            if (!scope.Includes(container.ContainerId)) continue;
            var zombies = 0;
            foreach (var p in container.Processes)
            {
                if (SoundingTraffic.IsOwn(p.Name, p.CommandLine)) continue;
                if (p.State is ['D', ..] && p.WaitMillisecondsPerSecond is >= 200 and var wait)
                {
                    Add(container.ContainerId, Severity.Warning, "process-blocked",
                        $"{p.Name} (pid {p.Pid}) is in uninterruptible wait (state {p.State}) {wait:0} ms of every second: usually stuck on disk or network storage I/O.",
                        [Look.Storage, Look.Metrics("ioPressure,readOps,writeOps")]);
                }

                if (p.State is ['Z', ..]) zombies++;

                if (p is { OpenFiles: { } open, OpenFilesLimit: > 0 and var max } && open * 10 >= max * 8)
                {
                    Add(container.ContainerId, open * 100 >= max * 95 ? Severity.Critical : Severity.Warning, "process-open-files",
                        $"{p.Name} (pid {p.Pid}) has {open:N0} of its {max:N0} allowed files open: a file or socket leak, or a limit set too low.",
                        [Look.Processes("openFiles"), Look.Connections()]);
                }
            }

            if (zombies >= 5)
            {
                Add(container.ContainerId, Severity.Warning, "process-zombies", $"{zombies} zombie processes are waiting for their parent to reap them.",
                    [Look.Processes()]);
            }
        }

        foreach (var container in window)
        {
            if (!scope.Includes(container.ContainerId)) continue;
            var usage = containers.GetValueOrDefault(container.ContainerId)?.Usage;
            foreach (var p in container.Processes)
            {
                // Sounding's own processes in the container are monitoring overhead, not the application.
                if (!p.Running || SoundingTraffic.IsOwn(p.Name, p.CommandLine) || p.MemoryGrowthBytesPerHour is not (>= 50L << 20 and var growth)) continue;
                // Headroom is the container's, not the process's: every process in it shares the limit.
                double? hoursToLimit = usage is { MemoryLimitBytes: > 0 and var limit } ? Math.Max(0, limit - usage.MemoryBytes) / growth : null;
                // Growth that takes days to reach the limit is a figure in the process trends, not a problem now: a
                // cache or a loaded model filling slowly reads the same.
                if (hoursToLimit >= HeadroomHours) continue;
                // A process that just started is often still loading (a model, a cache) and levels off: say so
                // rather than extrapolate a startup ramp into a crash.
                var age = p.StartedAt is { } started ? now - started : (TimeSpan?)null;
                // Unless the limit is minutes away: then it is not levelling off in time, whatever its age.
                var young = age < TimeSpan.FromMinutes(10) && hoursToLimit is not < 0.25;
                // And with hours of headroom or no limit, a young process growing is every process after a start:
                // reported once it is old enough to mean something.
                if (young && hoursToLimit is not < 2) continue;
                var severity = young ? Severity.Warning : hoursToLimit is < 2 ? Severity.Critical : Severity.Warning;
                Add(container.ContainerId, severity, "process-memory-growth",
                    $"{p.Name} (pid {p.Pid}) memory grows {Bytes((long)growth)}/h over the last {GrowthWindow.TotalMinutes:0} min (now {Bytes(p.MemoryBytes)})"
                    + (hoursToLimit is { } h ? $"; at this rate the container reaches its limit in {Duration(h)}." : ": a possible leak.")
                    + (young ? $" It started {age!.Value.TotalMinutes:0} min ago, so this may be startup loading that levels off: check again in a few minutes before calling it a leak." : ""),
                    [Look.Trends("memoryGrowth", 7200), Look.Metrics("memoryBytes,anon,file")]);
            }
        }
    }
}
