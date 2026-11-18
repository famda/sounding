namespace Sounding.Agent.Sampling;

// Where this container's cgroup is. With its own cgroup namespace (Docker, unprivileged Kubernetes pods) the mount
// is the container's cgroup and /proc/self/cgroup says "0::/". A privileged Kubernetes pod (containerd) or a Docker
// container run with --cgroupns=host sees the whole tree instead: its own cgroup is the path /proc/self/cgroup names.
public static class CgroupLocation
{
    public const string DefaultMount = "/sys/fs/cgroup";

    public static string Resolve(string configured, string procPath)
    {
        if (configured != DefaultMount) return configured;
        var self = Path.Combine(procPath, "self/cgroup");
        if (!File.Exists(self)) return configured;
        foreach (var line in File.ReadLines(self))
        {
            if (!line.StartsWith("0::", StringComparison.Ordinal)) continue;
            var relative = line.AsSpan(3).Trim();
            if (relative.IsEmpty || relative is "/") return configured;
            var own = string.Concat(configured, relative);
            return File.Exists(Path.Combine(own, "cgroup.procs")) ? own : configured;
        }

        return configured;
    }
}
