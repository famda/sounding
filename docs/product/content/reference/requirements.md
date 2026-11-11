---
title: Requirements
description: What Sounding needs to run, and the platforms it supports.
---

## Containers

| Requirement | Details |
|---|---|
| Processor | **x86-64** (amd64). ARM containers aren't supported yet. |
| Operating system | **Linux** containers, with **cgroup v2**, the default on current Linux distributions and Docker Desktop. |
| Base image | Any Linux image: Debian, Ubuntu, Red Hat-based, Alpine, BusyBox, or distroless. The agent brings everything it needs. |
| Runtime | **Docker Engine** with Docker Compose v2 on Linux, or **Docker Desktop**. |

On Docker Desktop, the host Sounding shows is Docker's Linux virtual machine, not your computer. It has no temperature
sensors.

Kubernetes and other container platforms aren't supported yet.

## Database

**PostgreSQL 14 or later**, reachable from every monitored container and the dashboard. See
[set up the database](../operate/set-up-the-database.md).

## Browser

A current version of Chrome, Edge, Firefox, or Safari.

## GPUs

| To see | You need |
|---|---|
| The host's GPU cards | An **NVIDIA** GPU, its driver, and the **NVIDIA Container Toolkit**, with GPU access given to one container with Sounding. |
| Each container's GPU use | CUDA programs started through Sounding's launcher. Graphics work (OpenGL, Vulkan) isn't measured per container. Profilers that use the same CUDA interface, such as NVIDIA Nsight, can't run at the same time. |

See [monitor NVIDIA GPUs](../dashboard/monitor-gpus.md).

## Optional access

Most figures need no extra permissions. These features need more:

| Feature | Needs | See |
|---|---|---|
| The host's network | The host's `/proc` or `/sys` mounted read-only. | [Show the host's network, disks, and GPUs](../operate/show-host-network-disk-and-gpu.md) |
| Other disks of the host | A folder of each disk mounted read-only. | [Show the host's network, disks, and GPUs](../operate/show-host-network-disk-and-gpu.md) |
| Connections, requests, DNS, and packet captures | The `NET_RAW` capability, which Docker grants by default. | [Allow packet inspection and process details](../operate/allow-packet-inspection-and-process-details.md) |
| Details of other users' processes | Running as `root`, with the `SYS_PTRACE` capability. | [Allow packet inspection and process details](../operate/allow-packet-inspection-and-process-details.md) |
