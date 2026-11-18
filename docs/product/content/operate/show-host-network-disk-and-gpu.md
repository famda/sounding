---
title: Show the host's network, disks, and GPUs
description: Give one container on each host access to the host's network, disks, or GPUs, so Sounding can show them.
---

Out of the box, Sounding shows each container, and its host's CPU, memory, disk activity, and the disk holding Docker's
data. To also see the host's **network**, other **disks**, or **GPUs**, give one container on the host access to them.

You only need to do this for **one container per host**: Sounding finds the container that has the access, and uses it
to report that resource. Different containers can provide access to different host resources.

Most container metrics work without these optional steps. Choose only what you need:

- [Host network](#network)
- [Additional host disks](#disks)
- [NVIDIA GPUs](#gpus)

> [!NOTE]
> Each mount below gives the container read-only access to information about the host. Choose a container you trust
> with it.

## Network

Mount the host's `/proc` at `/host/proc`, read-only:

```yaml title="compose.yaml"
services:
  your-app:
    volumes:
      - /proc:/host/proc:ro
```

The host's **Network** page then shows its network interfaces, its traffic, and its TCP, UDP, and connection-tracking
figures.

If you'd rather not share `/proc`, mount `/sys` at `/host/sys` instead. You get the interfaces and their traffic, but not
the TCP, UDP, and connection-tracking figures.

Traffic on Docker's internal interfaces and loopback isn't counted, so the host's traffic is what enters and leaves the
machine. To change which interfaces are left out, see `SOUNDING_NETWORK_EXCLUDE_PREFIXES` in
[configuration settings](../reference/configuration.md#host-access).

## Disks

To show the free space of the disk Docker uses, Sounding reads it from the container itself. To show another disk, or
on Docker Desktop, mount a folder from that disk at `/host/disk`:

```yaml title="compose.yaml"
services:
  your-app:
    volumes:
      - /var/lib/docker:/host/disk:ro
```

Open the host’s **Disk** page to check free space on the mounted disk. Any folder on that disk works.

To show several disks by name, list them with `SOUNDING_DISKS`:

```yaml title="compose.yaml"
services:
  your-app:
    environment:
      SOUNDING_DISKS__0__NAME: Application data
      SOUNDING_DISKS__0__PATH: /host/app-data
      SOUNDING_DISKS__1__NAME: Archive
      SOUNDING_DISKS__1__PATH: /host/archive
    volumes:
      - /srv/app-data:/host/app-data:ro
      - /srv/archive:/host/archive:ro
```

On the host’s **Disk** page, look for **Application data** and **Archive**. The configured paths replace
the `/host/disk` mount for that container.

## GPUs

Give one monitored container access to the NVIDIA GPUs, then open the host’s **GPU** page to check which cards appear. See [monitor NVIDIA GPUs](../dashboard/monitor-gpus.md).

## On Docker Desktop

On Docker Desktop, the host is Docker's Linux virtual machine. Its network, disks, and memory are the virtual machine's,
not your computer's, and it has no temperature sensors.

## Next steps

- [Allow packet inspection and process details](allow-packet-inspection-and-process-details.md)
- [Configuration settings](../reference/configuration.md)
