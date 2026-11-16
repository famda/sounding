using Sounding.Dashboard.Application.History;
using static Sounding.Dashboard.Application.Diagnostics.Rules.DiagnosisText;

namespace Sounding.Dashboard.Application.Diagnostics.Rules;

// One container's state and latest sample: how it ended, whether it reports, and how close it is to its limits.
internal static class ContainerRules
{
    public static void Check(HostState host, ContainerState container, List<Finding> findings)
    {
        void Advise(string severity, string code, string message, Lookup[]? next, string? advice, FindingFacts? facts = null) => findings.Add(new Finding(
            severity, code, ResourceTarget.Container, container.ContainerId, container.Name, host.HostId, message, next, advice, Facts: facts));
        void Add(string severity, string code, string message, Lookup[]? next = null) => Advise(severity, code, message, next, null);

        if (container.Status == ContainerStatus.Stopped)
        {
            var since = new FindingFacts(Since: container.LastSeen);
            if (container.ExitSignal is { } signal)
            {
                Advise(Severity.Critical, FindingCode.ContainerKilled, $"Not running: stopped at {container.LastSeen:u} by signal {signal}{(signal == 9 ? " (SIGKILL: often the OOM killer)" : signal == 15 ? " (SIGTERM: something asked it to stop, such as docker stop or a deploy)" : "")} and has not started again.",
                    [Look.Events], StoppedWhy, since);
            }
            else if (container.ExitCode is { } code and not 0)
            {
                Advise(Severity.Critical, FindingCode.ContainerFailed, $"Not running: exited at {container.LastSeen:u} with code {code} and has not started again.", [Look.Events], StoppedWhy, since);
            }

            return;
        }

        // Silence is a monitoring gap, not an outage: whether it still answers is shown only by calls to it
        // (NetworkRules.CheckUnanswered and FindingRelations raise this to critical when they do not get an answer).
        if (container.Status == ContainerStatus.Offline)
        {
            Advise(Severity.Warning, FindingCode.ContainerNotReporting, NotReporting(container.LastSeen),
                [Look.Timeline], "Check the container on its host (running, paused or gone) and whether its Sounding agent can reach the database; failing calls from the services that use it would show that it does not answer.",
                new FindingFacts(Since: container.LastSeen));
            return;
        }

        // Restarts are counted since the container was first seen, so the count alone says nothing about now: recent
        // restarts are reported from the events (EventRules).
        if (container.Usage is not { } usage) return;

        if (usage.CpuLimitCores is > 0 and var cpuLimit && usage.CpuCores >= cpuLimit * 0.9)
        {
            Add(Severity.Warning, "container-cpu-limit", $"Using {usage.CpuCores:0.##} of its {cpuLimit:0.##}-core CPU limit.",
                [Look.Trends("cpu")]);
        }

        if (usage.MemoryLimitBytes is > 0 and var memoryLimit)
        {
            // The working set leaves out inactive page cache, which the kernel reclaims before it ever OOM-kills.
            var inactive = usage.Detail?.Memory?.InactiveFileBytes ?? 0;
            var workingSet = Math.Max(0, usage.MemoryBytes - inactive);
            var percent = workingSet * 100d / memoryLimit;
            var split = usage.Detail?.Memory is { } m ? $" ({Bytes(m.Anon)} application memory, {Bytes(m.File)} page cache)" : "";
            Threshold(percent, 97, 90, "container-memory-limit",
                $"Its working set is {Bytes(workingSet)} of its {Bytes(memoryLimit)} memory limit ({Pct(percent)}){split}.",
                [Look.Trends("memoryGrowth")], Add);
        }

        if (usage.Detail is not { } detail) return;

        if (detail.MemoryEvents is { OomKill: > 0 } oom)
        {
            // A count since the container started, kept as a warning: a kill inside the event window is reported from
            // its event instead, with its time.
            Add(Severity.Warning, FindingCode.ContainerOomKill, $"The kernel OOM killer has killed {oom.OomKill} process(es) in this container since it started at {container.StartedAt:u}; none in the event window.",
                [Look.Trends("memory"), Look.Events]);
        }

        if (detail.Throttling is { ThrottledPercent: >= 25 } throttling)
        {
            Advise(throttling.ThrottledPercent >= 75 ? Severity.Critical : Severity.Warning, FindingCode.ContainerCpuThrottled,
                $"Held back by its own CPU limit{(usage.CpuLimitCores is > 0 and var cores ? $" of {cores:0.##} cores" : "")} in {Pct(throttling.ThrottledPercent)} of scheduling periods: it wants more CPU than its limit allows, so everything it does is slower. The limit is its own setting; other containers do not cause this.",
                [Look.Trends("cpu")], "Raise the limit if the work that uses the CPU is expected.");
        }

        if (detail.Pressure is { } pressure) HostRules.CheckPressure(pressure, "container", Add);

        if (detail is { Tasks: { } tasks, TasksLimit: > 0 and var tasksLimit } && tasks * 10 >= tasksLimit * 9)
        {
            Add(Severity.Warning, "container-tasks-limit", $"{tasks} of {tasksLimit} tasks (pids) are in use.");
        }

        foreach (var mount in detail.Mounts ?? [])
        {
            if (mount is { ReadOnly: false, TotalBytes: > 0 and var total, FreeBytes: { } free } && free * 10 < total)
            {
                Add(Severity.Warning, "container-mount-full", $"Mount {mount.MountPoint} has {Bytes(free)} of {Bytes(total)} free.");
            }
        }

        if (detail.Logs is { ErrorsPerSecond: >= 0.5 } logs)
        {
            Add(Severity.Warning, "container-log-errors", $"Logging {logs.ErrorsPerSecond:0.##} error lines per second.", [Look.Logs("error")]);
        }

        if (detail.Tcp is { } tcp)
        {
            if (tcp.FailedConnectsPerSecond >= 1)
            {
                Add(Severity.Warning, "container-tcp-failed-connects", $"{tcp.FailedConnectsPerSecond:0.##} outgoing connections fail per second.", [Look.Trace, Look.Connections(problemsOnly: true)]);
            }

            if (tcp.ListenDropsPerSecond > 0)
            {
                Add(Severity.Warning, "container-tcp-listen-drops", $"{tcp.ListenDropsPerSecond:0.##} incoming connections are dropped per second (full accept queue).", [Look.Trace, Look.Connections(problemsOnly: true)]);
            }

            if (tcp.RetransmitsPerSecond >= 10)
            {
                Add(Severity.Warning, "container-tcp-retransmits", $"{tcp.RetransmitsPerSecond:0.#} TCP segments are retransmitted per second.", [Look.Trace, Look.Connections(problemsOnly: true)]);
            }
        }

        if (detail.Dns is { FailuresPerSecond: >= 0.5 } dns)
        {
            Add(Severity.Warning, "container-dns-failures", $"{dns.FailuresPerSecond:0.##} DNS lookups fail per second.", [Look.Trace, Look.Connections(problemsOnly: true)]);
        }

        if (detail.GpuAllocationFailures is > 0 and var gpuFailures)
        {
            Add(Severity.Warning, "container-gpu-allocation", $"{gpuFailures} GPU memory allocations have failed.");
        }

        if (detail is { DiskGrowthBytesPerHour: > 0 and var diskGrowth, DiskFreeBytes: { } diskFree } && diskFree / diskGrowth is < 48 and var hoursLeft)
        {
            Add(hoursLeft < 6 ? Severity.Critical : Severity.Warning, "container-disk-filling",
                $"Its writable layer grows {Bytes((long)diskGrowth)}/h with {Bytes(diskFree)} free: full in about {hoursLeft:0.#} h.",
                [Look.Storage]);
        }

        if (detail.Memory is { RefaultsPerSecond: >= 1000 } memory)
        {
            Add(Severity.Warning, "container-memory-thrashing",
                $"Pages are refaulted {memory.RefaultsPerSecond:0}/s{(memory.MajorFaultsPerSecond is { } major ? $" with {major:0} major faults/s" : "")}: its working set does not fit in memory and it rereads from disk.",
                [Look.Metrics("refaults,majorFaults,memoryPressure")]);
        }

        if ((detail.DeletedFiles ?? []).Sum(f => f.Bytes) is >= 1L << 30 and var deletedBytes)
        {
            Add(Severity.Warning, "container-deleted-files-open",
                $"{Bytes(deletedBytes)} of deleted files are still held open, so their space is not freed.",
                [Look.Storage]);
        }

        if (detail.Udp is { BufferErrorsPerSecond: > 0 } udp)
        {
            Add(Severity.Warning, "container-udp-drops", $"{udp.BufferErrorsPerSecond:0.##} UDP datagrams are dropped per second because receive buffers are full.");
        }

        if (detail.Conntrack is { Max: > 0 and var conntrackMax, Entries: var entries } && entries * 10 >= conntrackMax * 9)
        {
            Add(Severity.Critical, "conntrack-full", $"{entries:N0} of {conntrackMax:N0} connection-tracking entries are in use: new connections are dropped when it fills.");
        }
    }
}
