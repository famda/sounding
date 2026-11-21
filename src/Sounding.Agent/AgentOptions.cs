using Microsoft.Extensions.Configuration;

namespace Sounding.Agent;

public sealed class AgentOptions
{
    [ConfigurationKeyName("INTERVAL_MILLISECONDS")]
    public int IntervalMilliseconds { get; set; } = 1000;

    [ConfigurationKeyName("SAMPLE_HOST")]
    public bool SampleHost { get; set; } = true;

    [ConfigurationKeyName("SAMPLE_NETWORK")]
    public bool SampleNetwork { get; set; } = true;

    [ConfigurationKeyName("CLUSTER_NETWORKS")]
    public string? ClusterNetworks { get; set; }

    [ConfigurationKeyName("PROC_PATH")]
    public string ProcPath { get; set; } = "/proc";

    [ConfigurationKeyName("CGROUP_PATH")]
    public string CgroupPath { get; set; } = Sampling.CgroupLocation.DefaultMount;

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

    // Kubernetes Downward API fields (metadata.namespace, metadata.name, metadata.uid) and the container's name in
    // the pod spec, which the Downward API does not expose.
    [ConfigurationKeyName("POD_NAMESPACE")]
    public string? PodNamespace { get; set; }

    [ConfigurationKeyName("POD_NAME")]
    public string? PodName { get; set; }

    [ConfigurationKeyName("POD_UID")]
    public string? PodUid { get; set; }

    [ConfigurationKeyName("POD_CONTAINER")]
    public string? PodContainer { get; set; }

    [ConfigurationKeyName("CLUSTER_DOMAIN")]
    public string ClusterDomain { get; set; } = "cluster.local";

    // How long samples are kept in memory while the database can't be reached or has no schema yet.
    [ConfigurationKeyName("BUFFER_MINUTES")]
    public int BufferMinutes { get; set; } = 15;

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
