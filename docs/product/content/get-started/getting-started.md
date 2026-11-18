---
title: Getting started
description: Choose where to start with Sounding, see what every setup involves, and check that yours can run it.
---

Every Sounding setup has the same three parts: the agent inside each container you monitor, a PostgreSQL database
they all report to, and the dashboard that shows what they report. This page helps you choose where to start, and
lists what your containers and machines need.

## Choose where to start

Choose a sample or go directly to the setup guide for your own application.

| Your starting point | Guide |
|---|---|
| Sample Compose application | [Try the Docker demo](docker-demo.md) with a web server and a client. |
| Sample Kubernetes workload | [Try the Kubernetes demo](kubernetes-demo.md) on Docker Desktop’s cluster. |
| Your Compose service | [Set up Sounding with Docker Compose](../deployment/docker-compose.md) |
| Your Kubernetes workload | [Set up Sounding on Kubernetes](../deployment/kubernetes.md) |

## What every setup involves

1. **Run the agent inside your container.** In an image you build, copy in the agent and start your application
   through its launcher. On Kubernetes, an init container can also supply the agent if cluster policy permits.

   ```dockerfile title="Dockerfile"
   COPY --from=famda/sounding:agent-latest /opt/sounding /opt/sounding
   ENTRYPOINT ["/opt/sounding/run", "node", "server.js"]
   ```

2. **Point it at the database.** One setting, `SOUNDING_CONNECTION_STRING`, names the PostgreSQL database. Every
   monitored container and the dashboard use the same one.
3. **Open the dashboard.** Within a few seconds, your host and container appear with live figures.

Each container you want to monitor needs the agent; adding it to one doesn't monitor the others. When the first one
reports, [monitor more containers and hosts](monitor-containers-and-hosts.md) the same way.

## Requirements

Check these before you add the agent to your images.

### Containers

| Requirement | Details |
|---|---|
| Processor | **x86-64** (amd64) or **ARM64** (arm64), including Apple silicon Macs. Docker picks the matching image automatically. 32-bit ARM isn't supported. |
| Operating system | **Linux** containers, with **cgroup v2**, the default on current Linux distributions and Docker Desktop. |
| Base image | Any Linux image: Debian, Ubuntu, Red Hat-based, Alpine, BusyBox, or distroless. The agent brings everything it needs. |
| Runtime | **Docker Engine** with Docker Compose v2 on Linux, **Docker Desktop**, or **Kubernetes** with containerd. |

On Docker Desktop, the host Sounding shows is Docker's Linux virtual machine, not your computer. It has no temperature
sensors.

On Kubernetes, see [what Sounding doesn't see](../concepts/kubernetes.md#what-sounding-doesnt-see).

Podman and other container platforms aren't supported yet.

### Database

**PostgreSQL 14 or later**, reachable from every monitored container and the dashboard. The setup guides start one for
you; to use your own, see [set up the database](../operate/set-up-the-database.md).

### Browser

A current version of Chrome, Edge, Firefox, or Safari.

### GPUs

| To see | You need |
|---|---|
| The host's GPU cards | An **NVIDIA** GPU, its driver, and the **NVIDIA Container Toolkit**, with GPU access given to one container with Sounding. |
| Each container's GPU use | CUDA programs started through Sounding's launcher. Graphics work (OpenGL, Vulkan) isn't measured per container. Profilers that use the same CUDA interface, such as NVIDIA Nsight, can't run at the same time. |

See [monitor NVIDIA GPUs](../dashboard/monitor-gpus.md).

### Optional access

Most figures need no extra permissions. These features need more:

| Feature | Needs | See |
|---|---|---|
| The host's network | The host's `/proc` or `/sys` mounted read-only. | [Show the host's network, disks, and GPUs](../operate/show-host-network-disk-and-gpu.md) |
| Other disks of the host | A folder of each disk mounted read-only. | [Show the host's network, disks, and GPUs](../operate/show-host-network-disk-and-gpu.md) |
| Connections, requests, DNS, and packet captures | The `NET_RAW` capability, which Docker grants by default. | [Allow packet inspection and process details](../operate/allow-packet-inspection-and-process-details.md) |
| Details of other users' processes | Running as `root`, with the `SYS_PTRACE` capability. | [Allow packet inspection and process details](../operate/allow-packet-inspection-and-process-details.md) |

Without them, the rest of Sounding works, and the dashboard shows **Not available** where data is missing.
