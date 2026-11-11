namespace Sounding.Agent.Identity;

public sealed record HostIdentity(string HostId, string HostDisplayName, bool HostNameSet, string ContainerId, string Name)
{
    public static HostIdentity Create(AgentOptions options)
    {
        // The kernel's boot id: every container on the same host sees the same one.
        var hostId = NonEmpty(options.HostId) ?? ReadBootId(options.ProcPath) ?? Environment.MachineName;
        var containerId = NonEmpty(options.ContainerId) ?? Environment.MachineName;
        return new HostIdentity(
            hostId,
            NonEmpty(options.HostDisplayName) ?? hostId[..Math.Min(8, hostId.Length)],
            NonEmpty(options.HostDisplayName) is not null,
            containerId,
            NonEmpty(options.DisplayName) ?? ContainerName.Resolve() ?? containerId);
    }

    private static string? ReadBootId(string procPath)
    {
        var path = Path.Combine(procPath, "sys/kernel/random/boot_id");
        return File.Exists(path) ? NonEmpty(File.ReadAllText(path).Trim()) : null;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
