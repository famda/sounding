namespace Sounding.Agent.Identity;

public sealed record HostIdentity(
    string HostId, string HostDisplayName, bool HostNameSet, string ContainerId, string Name, string? Namespace = null, string? Warning = null)
{
    public static HostIdentity Create(AgentOptions options)
    {
        // The kernel's boot id: every container on the same host sees the same one.
        var configuredHost = NonEmpty(options.HostId);
        var hostId = configuredHost ?? ReadBootId(options.ProcPath) ?? Environment.MachineName;
        var configuredId = NonEmpty(options.ContainerId);
        var pod = KubernetesPod.Detect(options, Environment.MachineName);
        // An explicit id is authoritative. A pod without a known UID and container gets an id of its own for this run
        // rather than one guessed from its name, which another pod (or namespace) can share.
        var containerId = configuredId
            ?? (pod is null ? Environment.MachineName : pod.Id ?? $"{pod.Pod}/{Guid.NewGuid():N}");
        return new HostIdentity(
            hostId,
            // A configured id is a name already (a Kubernetes node's, say); a boot id is shortened.
            NonEmpty(options.HostDisplayName) ?? configuredHost ?? hostId[..Math.Min(8, hostId.Length)],
            NonEmpty(options.HostDisplayName) is not null,
            containerId,
            // Kubernetes DNS has no reverse names for pods: the workload's name is used instead.
            NonEmpty(options.DisplayName) ?? pod?.Name ?? ContainerName.Resolve() ?? containerId,
            pod?.Namespace,
            configuredId is null ? pod?.Warning : null);
    }

    private static string? ReadBootId(string procPath)
    {
        var path = Path.Combine(procPath, "sys/kernel/random/boot_id");
        return File.Exists(path) ? NonEmpty(File.ReadAllText(path).Trim()) : null;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
