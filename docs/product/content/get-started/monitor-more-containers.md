---
title: Monitor more containers and hosts
description: Add Sounding to more applications and machines, and see them all in one dashboard.
---

Sounding shows every container that has the agent in its image and reports to the same database. To monitor more, add
the agent to more images. One dashboard shows them all.

## Add another application

1. Add the two Sounding lines to the service's `Dockerfile`, as in
   [add Sounding to your application](getting-started.md#1-add-the-agent-to-your-image).
2. Set `SOUNDING_CONNECTION_STRING` (and `SOUNDING_HOST_DISPLAY_NAME`) on the container, pointing to the **same
   database** as the dashboard.
3. Rebuild and deploy the service as usual.

Sounding doesn't add itself to other containers: each container you want to monitor needs the agent in its image.

If the other service lives in a **separate `compose.yaml`** on the same machine, share a Docker network with the stack
running `db` (add `networks: { default: { name: sounding } }` to that stack), then attach your other service to it:

```yaml title="Other application compose.yaml"
services:
  worker:
    build: .
    environment:
      SOUNDING_CONNECTION_STRING: "Host=db;Database=sounding;Username=sounding;Password=${SOUNDING_DB_PASSWORD}"
      SOUNDING_HOST_DISPLAY_NAME: my-host
    networks:
      - default
      - sounding

networks:
  sounding:
    name: sounding
    external: true
```

## Add another machine

Containers on another machine appear as a new host as soon as they report to the same database. For a readable name,
set `SOUNDING_HOST_DISPLAY_NAME` on the containers of that machine, using the **same value** for every container on it.

On each machine, agents coordinate automatically so each host resource is reported once. Core figures, network,
disk space, and GPUs can be reported by different eligible containers. If a reporting agent's database session ends,
another eligible container can take over; see [how Sounding works](../concepts/how-sounding-works.md#the-agent).
For host names and keeping the same identity across reboots, see [host identity](../concepts/hosts-and-containers.md#identity).

To also see a machine's network interfaces, its disks' free space, or its GPUs, give one of its containers access to
them. See [show the host's network, disks, and GPUs](../operate/show-host-network-disk-and-gpu.md).

## Replicas and short-lived containers

- **Replicas** of the same service appear as separate containers, each with its own figures. No configuration is needed.
- **Short-lived containers**, such as scheduled jobs, appear while they run and are hidden a few minutes after they stop.
  Set `SOUNDING_SAMPLE_HOST=false` on them so that a long-running container reports the host instead.

## Next steps

- [Find your way around the dashboard](../dashboard/find-your-way-around.md)
- [Hosts, containers, and the fleet](../concepts/hosts-and-containers.md)
- [Configuration settings](../reference/configuration.md)
