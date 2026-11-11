---
title: Secure Sounding
description: Protect the dashboard and the database, and understand what Sounding collects.
---

Sounding shows a lot about your systems, and lets its users record network traffic. Treat the dashboard and its
database as sensitive.

## Protect the dashboard

> [!WARNING]
> The dashboard has no sign-in. Anyone who can reach it can see everything, and record packet captures.

- **Keep it private.** Publish it on `127.0.0.1`, as in the examples, and reach it over SSH or a VPN.
- **Or put it behind authentication.** Place it behind a reverse proxy that signs users in and encrypts the connection.
- **Never expose it directly to the internet.**

## Protect the database

- Allow only the agents, the dashboard, and your administrators to connect, with strong credentials and firewall rules.
- Keep connection strings out of source code and screenshots.
- The agents connect without encryption. See [protect the connection](set-up-the-database.md#protect-the-connection).

## Know what Sounding collects

| Data | Where you see it | Why it may be sensitive |
|---|---|---|
| Host and container names | Everywhere | Reveals your infrastructure. |
| Process names, command lines, and users | Process tables | Command lines can contain secrets passed as arguments. |
| Addresses, ports, and DNS names | Connections, DNS lookups, map | Reveals who your systems talk to. |
| Web addresses and request names | Connections, Requests | Paths can contain identifiers; Sounding leaves out query strings. |
| Recent output | Events | Your applications' output can contain secrets or personal data. |
| Packet captures | Packet capture | The first 128 bytes of each packet, which can include requests, queries, and passwords. |

## Grant extra access only where needed

For host network, disk, or GPU visibility, give the necessary access to one trusted monitored container per host.
Network inspection and access to other users' process details are per-container features: grant the relevant capability
only to containers that need it. Docker commonly grants `NET_RAW` by default. See
[show the host's network, disks, and GPUs](show-host-network-disk-and-gpu.md) and
[allow packet inspection and process details](allow-packet-inspection-and-process-details.md).

## Retention isn't a security boundary

Sounding's default retention target is 24 hours, with cleanup performed by running host-sampling agents. If those agents
stop, old data can remain; see [history cleanup](../concepts/samples-and-history.md#history). Copies made outside the
database aren't deleted: database backups and replicas, downloaded captures, and screenshots. Apply your own rules to those.

## Next steps

- [Upgrade Sounding](upgrade-sounding.md)
- [Set up the database](set-up-the-database.md)
- [Capture packets](../dashboard/capture-packets.md)
