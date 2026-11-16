---
title: Monitor more containers and hosts
description: Grow from one monitored container to more services and machines, all reporting to the same dashboard.
---

Your first container is reporting. Now bring in the worker beside it, another Compose project, or a second machine.
You keep the same dashboard: every monitored container reports to the same PostgreSQL database.

Choose the next step that matches your setup:

- [Add another application](#add-another-application) in the same stack.
- [Connect a separate Compose project](#use-a-separate-compose-project) on the same machine.
- [Add another machine](#add-another-machine).

Sounding doesn't add itself to other containers: each container you want to monitor needs the agent in its image.

## Add another application

1. Add the two Sounding lines to the service's `Dockerfile`, as in
   [add Sounding to your application](getting-started.md#1-add-the-agent-to-your-image).
2. Set `SOUNDING_CONNECTION_STRING` to the **same database** as the dashboard. Use the same
   `SOUNDING_HOST_DISPLAY_NAME` as the other containers on this machine.
3. Rebuild and deploy the service as usual.
4. Open that host in the dashboard and check that the new container has live figures.

### Use a separate Compose project

The `db` service name only works for containers on the same Docker network. For a second Compose project on the
same machine, connect its monitored service to the database's network.

For the stack from [getting started](getting-started.md), add this at the top level of the Compose file that runs
`db` to give its default network a shared name:

```yaml title="Dashboard compose.yaml"
networks:
  default:
    name: sounding
```

Apply the change by running `docker compose up -d` from that project's folder. Then attach the monitored service
in the other project's Compose file to that network:

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

The other project needs its own `.env` with `SOUNDING_DB_PASSWORD` set to the database's password. Compose doesn't
automatically read the first project's `.env`. Keep both files out of version control.

From the other project's folder, run `docker compose up -d --build worker`. It uses the same database and appears
under the same **my-host** in the dashboard.

## Add another machine

You don't need another dashboard. Reuse your images with Sounding already added, and point their
`SOUNDING_CONNECTION_STRING` to a database reachable from the new machine.

The quick-start `db` service has no published port, and its Docker service name isn't an address another machine
can use. Arrange database access first, following [protect the connection](../operate/set-up-the-database.md#protect-the-connection),
then use its reachable address in the connection string.

Set `SOUNDING_HOST_DISPLAY_NAME` to a readable name for the new machine, using the **same value** for every
container on it. Once they report, check that the dashboard lists the new host with its containers.

On each machine, agents coordinate automatically so each host resource is reported once; different eligible
containers can report core figures, network, disk space and GPUs. Another eligible container can take over when a
reporting agent's database session ends. See [how Sounding works](../concepts/how-sounding-works.md#the-agent).

Names label hosts; IDs keep their history together. For keeping the same host identity across reboots, see
[host identity](../concepts/hosts-and-containers.md#identity).

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
