---
title: Hosts, containers, and the fleet
description: The three levels the dashboard shows, how Sounding names them, and what their status means.
---

The dashboard shows three levels, and you can move between them at any time.

| Level | What it is | What its pages compare |
|---|---|---|
| **All hosts** | Every machine reporting to the same database: your fleet. | Each host's share of the fleet's resources. |
| **A host** | One machine running containers. On Docker Desktop, it's Docker's Linux virtual machine. | The machine's resources, and how much of them each container uses. |
| **A container** | One container with Sounding in its image. | The container's use against its own limits, and against the host. |

> [!TIP]
> Look at each host, not only the fleet. Spare capacity on one machine can hide another that's saturated.

## Names

**Hosts** are named after the start of their ID unless you give them a name. Set `SOUNDING_HOST_DISPLAY_NAME` to the
same value on every container of a machine.

**Containers** are named after their Docker container name, or their ID when Docker doesn't provide one (on Docker's
default network, for example). Set `SOUNDING_DISPLAY_NAME` to choose a name yourself.

## Identity

Behind each name is an ID that groups the figures:

- A **host's ID** is the machine's boot ID, which all its containers share. It changes when the machine restarts, so a
  restarted machine appears as a new host. To keep one host across restarts, set `SOUNDING_HOST_ID` to the same value on
  every container of the machine, and a different value on each machine.
- A **container's ID** defaults to its hostname, normally Docker's short container ID. Restarting the same container
  keeps it; recreating the container normally changes it. If you reuse custom hostnames, set `SOUNDING_CONTAINER_ID`
  to a value unique across all monitored hosts and containers. The dashboard counts restarts under the same ID.

> [!IMPORTANT]
> IDs must be unique. Two machines, or two running containers, with the same ID have their figures mixed together.

## Status

| Status | Meaning |
|---|---|
| **Running** | The container reported in the last 10 seconds. |
| **Not reporting** | The container hasn't reported for more than 10 seconds. It may have been killed, lost its connection to the database, or the machine may be down. |
| **Stopped** | The launcher reported that the application ended, whether successfully, with an error, or by a signal. The dashboard shows its exit code or signal when available. |

Containers that are no longer running disappear from the dashboard after a while: stopped containers after 2 minutes,
containers that aren't reporting after 10 minutes, and hosts after an hour. Hiding them doesn't delete their history.
You can change these times; see [configuration settings](../reference/configuration.md#dashboard).

## Next steps

- [Samples and history](samples-and-history.md)
- [Limits and capacity](limits-and-capacity.md)
