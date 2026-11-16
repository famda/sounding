using Sounding.Dashboard.Application.History;
using static Sounding.Dashboard.Application.Diagnostics.Rules.DiagnosisText;

namespace Sounding.Dashboard.Application.Diagnostics.Rules;

// A host's latest sample: whether its agents report, and how close it is to running out of CPU, memory, disk, GPU,
// temperature, file handles and connection tracking.
internal static class HostRules
{
    public static void Check(HostState host, List<Finding> findings)
    {
        void Add(string severity, string code, string message, Lookup[]? next = null) =>
            findings.Add(new Finding(severity, code, ResourceTarget.Host, host.HostId, host.DisplayName, host.HostId, message, next));

        if (!host.Online)
        {
            // Silence from the agents is a monitoring gap: it does not show that the host or its services are down.
            findings.Add(new Finding(Severity.Warning, "host-not-reporting", ResourceTarget.Host, host.HostId, host.DisplayName, host.HostId,
                $"No agent on the host has reported since {host.LastSeen:u}: the current state of the host and its containers is not known.",
                Advice: "Check that the containers running the Sounding agent on this host are up and can reach the database.",
                Facts: new FindingFacts(Since: host.LastSeen)));
            return;
        }

        if (host.Latest?.Snapshot is not { } s) return;

        if (s.Cpu is { } cpu)
        {
            Threshold(cpu.UsagePercent, 90, 75, "host-cpu-high", $"CPU is {Pct(cpu.UsagePercent)} busy across {cpu.LogicalCores} cores.",
                [Look.Containers("cpu")], Add);
            if (cpu.Split is { Steal: >= 10 } split)
            {
                Add(Severity.Warning, "host-cpu-steal", $"{Pct(split.Steal)} of CPU time is stolen by the hypervisor.");
            }

            if (cpu.Split is { IoWait: >= 20 } wait)
            {
                Add(Severity.Warning, "host-io-wait", $"{Pct(wait.IoWait)} of CPU time is waiting on I/O.", [Look.Host, Look.Containers("io")]);
            }
        }

        if (s.Memory is { } memory)
        {
            Threshold(memory.UsagePercent, 95, 85, "host-memory-high",
                $"Memory is {Pct(memory.UsagePercent)} used ({Bytes(memory.UsedBytes)} of {Bytes(memory.TotalBytes)}).",
                [Look.Containers("memory")], Add);
            if (memory.SwapTotalBytes > 0 && memory.SwapUsedBytes * 2 > memory.SwapTotalBytes)
            {
                Add(Severity.Warning, "host-swap", $"{Bytes(memory.SwapUsedBytes)} of {Bytes(memory.SwapTotalBytes)} swap is in use.");
            }
        }

        if (s.Pressure is { } pressure) CheckPressure(pressure, "host", Add);

        foreach (var volume in s.Disk.Volumes)
        {
            if (volume.Error is not null) continue;
            Threshold(volume.UsagePercent, 95, 85, "host-disk-full", $"Volume {volume.Path} is {Pct(volume.UsagePercent)} full ({Bytes(volume.FreeBytes)} free).", null, Add);
            if (volume is { InodesTotal: > 0 and var total, InodesFree: { } free } && free * 20 < total)
            {
                Add(Severity.Warning, "host-inodes", $"Volume {volume.Path} has {free:N0} of {total:N0} inodes free.");
            }
        }

        foreach (var device in s.Disk.Devices)
        {
            if (device.BusyPercent >= 90)
            {
                Add(Severity.Warning, "host-disk-busy", $"Disk {device.Name} is {Pct(device.BusyPercent)} busy.", [Look.Containers("io")]);
            }
        }

        // Full GPU memory alone is how model servers and other GPU programs work: they keep what they load resident.
        // It is a problem once allocations fail, so it is a finding only then; Measurements reports the figure.
        var failing = host.Containers.Where(c => c.Usage?.Detail?.GpuAllocationFailures is > 0).Select(c => c.Name ?? c.ContainerId).ToList();
        foreach (var gpu in s.Gpu.Devices)
        {
            if (gpu.EccUncorrected is > 0)
            {
                Add(Severity.Critical, "gpu-ecc", $"GPU {gpu.Index} ({gpu.Name}) reports {gpu.EccUncorrected} uncorrected ECC errors.");
            }

            if (gpu.TemperatureCelsius is >= 85 and var temperature)
            {
                Add(Severity.Warning, "gpu-hot", $"GPU {gpu.Index} ({gpu.Name}) is at {temperature:0} °C.");
            }

            if (gpu.MemoryUsagePercent is >= 95 and var vram && failing.Count > 0)
            {
                Add(Severity.Warning, "gpu-memory-full",
                    $"GPU {gpu.Index} ({gpu.Name}) memory is {Pct(vram)} used, and GPU memory allocations fail in {string.Join(", ", failing.Take(3))}.",
                    [Look.Containers("gpu")]);
            }
        }

        foreach (var sensor in s.Temperatures ?? [])
        {
            if (sensor.CriticalCelsius is { } critical && sensor.Celsius >= critical)
            {
                Add(Severity.Critical, "host-temperature", $"{sensor.Source} {sensor.Label} is at {sensor.Celsius:0} °C, at or above its critical {critical:0} °C.");
            }
            else if (sensor.HighCelsius is { } high && sensor.Celsius >= high)
            {
                Add(Severity.Warning, "host-temperature", $"{sensor.Source} {sensor.Label} is at {sensor.Celsius:0} °C, at or above its high {high:0} °C.");
            }
        }

        if (s.Files is { Max: > 0 } files && files.Open * 10 >= files.Max * 9)
        {
            Add(Severity.Warning, "host-file-handles", $"{files.Open:N0} of {files.Max:N0} file handles are open.");
        }

        if (s.Network.Conntrack is { Max: > 0 and var conntrackMax, Entries: var entries } && entries * 10 >= conntrackMax * 9)
        {
            Add(Severity.Critical, "host-conntrack-full", $"{entries:N0} of {conntrackMax:N0} connection-tracking entries are in use: new connections are dropped when it fills.");
        }

        if (s.Memory is { CommitLimitBytes: > 0 and var commitLimit, CommittedBytes: { } committed } && committed > commitLimit)
        {
            Add(Severity.Warning, "host-overcommit", $"{Bytes(committed)} of memory is committed against a commit limit of {Bytes(commitLimit)}.");
        }
    }

    // Stall time from the kernel's pressure figures, for a host ("host-…") or a container ("container-…").
    public static void CheckPressure(Pressure pressure, string prefix, Action<string, string, string, Lookup[]?> add)
    {
        if (pressure.MemoryFull >= 10)
        {
            add(Severity.Warning, $"{prefix}-memory-pressure", $"All tasks were stalled on memory {Pct(pressure.MemoryFull)} of the time.", null);
        }

        if (pressure.IoFull >= 20)
        {
            add(Severity.Warning, $"{prefix}-io-pressure", $"All tasks were stalled on I/O {Pct(pressure.IoFull)} of the time.", null);
        }

        if (pressure.CpuSome >= 50)
        {
            add(Severity.Warning, $"{prefix}-cpu-pressure", $"Some tasks waited for CPU {Pct(pressure.CpuSome)} of the time.", null);
        }
    }
}
