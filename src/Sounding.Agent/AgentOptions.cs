using Microsoft.Extensions.Configuration;

namespace Sounding.Agent;

public sealed class AgentOptions
{
    [ConfigurationKeyName("INTERVAL_MILLISECONDS")]
    public int IntervalMilliseconds { get; set; } = 1000;

    [ConfigurationKeyName("SAMPLE_HOST")]
    public bool SampleHost { get; set; } = true;

    [ConfigurationKeyName("PROC_PATH")]
    public string ProcPath { get; set; } = "/proc";

    [ConfigurationKeyName("CGROUP_PATH")]
    public string CgroupPath { get; set; } = "/sys/fs/cgroup";

    [ConfigurationKeyName("NVIDIA_SMI_PATH")]
    public string NvidiaSmiPath { get; set; } = "nvidia-smi";

    [ConfigurationKeyName("GPU_METER_PATH")]
    public string GpuMeterPath { get; set; } = "/tmp/gpu-meter";

    [ConfigurationKeyName("NET_METER_PATH")]
    public string NetMeterPath { get; set; } = "/opt/sounding/net-meter";

    [ConfigurationKeyName("HOST_ID")]
    public string? HostId { get; set; }

    [ConfigurationKeyName("HOST_DISPLAY_NAME")]
    public string? HostDisplayName { get; set; }

    [ConfigurationKeyName("CONTAINER_ID")]
    public string? ContainerId { get; set; }

    [ConfigurationKeyName("DISPLAY_NAME")]
    public string? DisplayName { get; set; }

    [ConfigurationKeyName("RETENTION_MINUTES")]
    public int RetentionMinutes { get; set; } = 1440;

    [ConfigurationKeyName("DISKS")]
    public List<DiskTarget> Disks { get; set; } = [];

    [ConfigurationKeyName("HOST_DISK_PATH")]
    public string HostDiskPath { get; set; } = "/host/disk";

    [ConfigurationKeyName("HOST_PROC_PATH")]
    public string HostProcPath { get; set; } = "/host/proc";

    [ConfigurationKeyName("HOST_SYS_PATH")]
    public string HostSysPath { get; set; } = "/host/sys";

    [ConfigurationKeyName("NETWORK_EXCLUDE_PREFIXES")]
    public string NetworkExcludePrefixes { get; set; } =
        "lo,veth,docker,br-,services,virbr,cni,flannel,cali,cilium,tunl,vxlan,kube,weave,lxc,dummy";
}

public sealed class DiskTarget
{
    [ConfigurationKeyName("NAME")]
    public string Name { get; set; } = "";

    [ConfigurationKeyName("PATH")]
    public string Path { get; set; } = "/";
}
