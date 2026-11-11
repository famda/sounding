---
title: Limits and capacity
description: How container limits relate to the host's capacity, and why the numbers don't always add up.
---

Many charts compare use with a dashed line. On a host, the line is its **capacity**: everything the machine has. On a
container, it's the container's **limit**: what Docker allows it to use.

![A host's overview: each container's share of the host, Other, and Free, with the containers' limits added up at the top right](../../assets/host-overview.png)

## Limits aren't reservations

A container's CPU or memory limit caps what it can use; it doesn't set anything aside for it. You can give containers
limits that add up to more than the machine has. That's common and often fine, but then the containers can't all reach
their limits at the same time.

The host overview shows this as **Limits**: the containers' limits added up, next to what the host has.

A container without a limit can use everything the host has. Its charts compare its use with the host's capacity
instead.

## Why the numbers don't add up

Host figures and container figures are measured separately, and they overlap:

- **The host includes everything on it:** the containers with Sounding, containers without it, and programs running on
  the machine itself. The overview shows the difference as **Other**.
- **Container traffic includes traffic between containers.** When one container calls a database in another container
  on the same machine, both containers count the traffic, but it never leaves the machine, so the host's network
  doesn't count it.
- **Disk activity is counted differently** for the host (per physical disk) and for containers. Compare them for trends,
  not as exact totals.

## Next steps

- [Set up the database](../operate/set-up-the-database.md)
- [Metrics glossary](../reference/metrics.md)
