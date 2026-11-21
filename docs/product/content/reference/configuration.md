---
title: Configuration settings
description: Every setting of the Sounding agent and dashboard.
---

You configure Sounding with environment variables, all starting with `SOUNDING_`. Set the agent's settings on each
container you monitor, and the dashboard's on the dashboard.

> [!NOTE]
> Docker applies changed environment variables only when it recreates the container: run `docker compose up -d` after
> changing them. On Kubernetes, restart the pods: `kubectl rollout restart deployment <name>`.

Jump to [agent connection](#connection), [names and identity](#names-and-identity),
[sampling and history](#sampling-and-history), or [dashboard settings](#dashboard).

## Agent

### Connection

| Setting | Default | Description |
|---|---|---|
| `SOUNDING_CONNECTION_STRING` | *Required* | The PostgreSQL connection string. Use the same database for every container and the dashboard. See [set up the database](../operate/set-up-the-database.md). |

### Names and identity

| Setting | Default | Description |
|---|---|---|
| `SOUNDING_HOST_DISPLAY_NAME` | `SOUNDING_HOST_ID` if set, else the start of the host's ID | The host's name in the dashboard. Use the same value on every container of a machine. |
| `SOUNDING_HOST_ID` | The machine's boot ID | The host's identity. The default changes when the machine restarts; set a fixed value, the same on every container of a machine and different on each machine, to keep one host across restarts. On Kubernetes, use the node's name. |
| `SOUNDING_DISPLAY_NAME` | The Docker container name, or its ID. On Kubernetes, `namespace/workload`. | The container's name in the dashboard. |
| `SOUNDING_CONTAINER_ID` | The container's hostname, normally its short Docker ID. On Kubernetes, `<pod UID>/<container>`. | The container's identity. Must be unique across all monitored containers, including containers on different hosts. Set this explicitly if you reuse a custom hostname. When set, it's used instead of the Kubernetes settings below. |
| `SOUNDING_POD_NAMESPACE`, `SOUNDING_POD_NAME`, `SOUNDING_POD_UID` | Read from the pod's files, with an `identity` warning event | Kubernetes: the pod's namespace, name, and UID, from the Downward API fields `metadata.namespace`, `metadata.name`, and `metadata.uid`. The UID is part of the container's identity; the namespace and pod name make up its name. |
| `SOUNDING_POD_CONTAINER` | Read from the pod's files, with an `identity` warning event | Kubernetes: the container's name in the pod spec. Part of the container's identity. |

See [hosts, containers, and the fleet](../concepts/hosts-and-containers.md#identity).

### Sampling and history

| Setting | Default | Description |
|---|---|---|
| `SOUNDING_INTERVAL_MILLISECONDS` | `1000` | How often the container is measured, in milliseconds. The minimum is `250`. Measuring less often reduces the load on the database. |
| `SOUNDING_BUFFER_MINUTES` | `15` | How long the agent holds its figures in memory while it can't write them: the database is unreachable, or the dashboard hasn't created Sounding's tables yet. It writes them once it can; older figures are dropped. Values below `1` use `1`. |
| `SOUNDING_SAMPLE_HOST` | `true` | Whether this container may report its host's figures. Set it to `false` on short-lived containers, such as scheduled jobs, so that a long-running container reports the host. At least one container per host must keep it `true`. |
| `SOUNDING_SAMPLE_NETWORK` | `true` | Whether this container reports its network. In a Kubernetes pod with several monitored containers, set it to `false` on all but one. |
| `SOUNDING_CLUSTER_NETWORKS` | *None* | Kubernetes: the cluster's pod and Service ranges, such as `10.244.0.0/16,10.96.0.0/16`. Calls to addresses in these ranges are matched to the pod that owns them on any node, and never counted as internet traffic. See [set the cluster's ranges](../deployment/kubernetes.md#set-the-clusters-ranges). |
| `SOUNDING_CLUSTER_DOMAIN` | `cluster.local` | Kubernetes: the cluster's DNS domain, used to name the Services containers call, as `<service>.<namespace>.svc`. |

> [!TIP]
> If you measure less often than every 10 seconds, set the dashboard's `SOUNDING_STALE_AFTER_SECONDS` higher than the
> sampling interval in seconds. For example, with `SOUNDING_INTERVAL_MILLISECONDS=15000` (15 seconds), set
> `SOUNDING_STALE_AFTER_SECONDS=20` so the next sample can arrive before the dashboard marks the container stale.

### Host access

These settings work with the mounts described in
[show the host's network, disks, and GPUs](../operate/show-host-network-disk-and-gpu.md).

| Setting | Default | Description |
|---|---|---|
| `SOUNDING_DISKS__0__NAME`, `SOUNDING_DISKS__0__PATH` | *None* | A disk to show by name, and a path on it inside the container. Use `__1__`, `__2__`, and so on for more disks. They replace the `/host/disk` mount for this container. |
| `SOUNDING_NETWORK_EXCLUDE_PREFIXES` | `lo`, `veth`, `docker`, `br-`, `services`, `virbr`, `cni`, `flannel`, `cali`, `cilium`, `tunl`, `vxlan`, `kube`, `weave`, `lxc`, `dummy` | Host network interfaces left out of the host's traffic, by the start of their name. Set it as one comma-separated value, such as `lo,veth,docker`; setting it replaces the whole list. |
| `SOUNDING_HOST_PROC_PATH` | `/host/proc` | Where the host's `/proc` is mounted. |
| `SOUNDING_HOST_SYS_PATH` | `/host/sys` | Where the host's `/sys` is mounted. |
| `SOUNDING_HOST_DISK_PATH` | `/host/disk` | Where a folder of the host's disk is mounted. |

### Logging

| Setting | Default | Description |
|---|---|---|
| `SOUNDING_LOGGING__LOGLEVEL__DEFAULT` | `Warning` | How much the agent writes to the container's logs. Set it to `Information` to diagnose a problem. |

### Advanced

Change these only if your environment puts these resources elsewhere.

| Setting | Default | Description |
|---|---|---|
| `SOUNDING_PROC_PATH` | `/proc` | The container's process information. |
| `SOUNDING_CGROUP_PATH` | `/sys/fs/cgroup` | The container's resource accounting (cgroup v2). |
| `SOUNDING_NVIDIA_SMI_PATH` | `nvidia-smi` | NVIDIA's management tool, used for the GPU cards. |
| `SOUNDING_GPU_METER_PATH` | `/tmp/gpu-meter` | A folder where CUDA programs report their GPU use to the agent. It must be writable by the CUDA programs. |
| `SOUNDING_NET_METER_PATH` | `/opt/sounding/net-meter` | Sounding's network traffic reader. |

## Dashboard

### Connection

| Setting | Default | Description |
|---|---|---|
| `SOUNDING_CONNECTION_STRING` | *Required* | The PostgreSQL connection string, to the same database as the agents. Its user creates and updates Sounding's tables. |
| `SOUNDING_AGENT_ROLE` | *None* | A database user the agents connect as. On every start, the dashboard grants it what agents need and nothing more; see [give the agents their own user](../operate/set-up-the-database.md#give-the-agents-their-own-user). |

### History

| Setting | Default | Description |
|---|---|---|
| `SOUNDING_RETENTION_HOURS` | `24` | How long samples, events, and captures are kept, in hours; values below `1` use `1`. Samples are deleted an hour at a time, so up to an hour more is kept. The dashboard shows at most 24 hours, whatever you keep. Set the same value on every dashboard replica. |

### What the dashboard shows

| Setting | Default | Description |
|---|---|---|
| `SOUNDING_STALE_AFTER_SECONDS` | `10` | How long without a report before a container or host is shown as **Not reporting**. |
| `SOUNDING_HIDE_OFFLINE_HOSTS_AFTER_MINUTES` | `60` | How long a host that isn't reporting stays visible. |
| `SOUNDING_HIDE_OFFLINE_CONTAINERS_AFTER_MINUTES` | `10` | How long a container that isn't reporting stays visible. |
| `SOUNDING_HIDE_STOPPED_CONTAINERS_AFTER_MINUTES` | `2` | How long a stopped container stays visible. |

Hiding a host or container doesn't delete its history.

### Logging

| Setting | Default | Description |
|---|---|---|
| `SOUNDING_LOGGING__LOGLEVEL__DEFAULT` | `Information` | How much the dashboard writes to its logs. |

### Health checks

The dashboard listens on port `8080`, and has two endpoints for health checks:

| Endpoint | Answers |
|---|---|
| `/healthz` | `200` while the dashboard is running. |
| `/readyz` | `200` while it has read the database successfully in the last 10 seconds, `503` otherwise. |
