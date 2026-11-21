---
title: Secure Sounding
description: Protect the dashboard and the database, and understand what Sounding collects.
---

The dashboard has no built-in sign-in. Anyone who can reach it can view monitoring data and start packet captures.

## Protect the dashboard

> [!WARNING]
> Protect the dashboard’s port when you enable remote access. The [REST API](../reference/api.md) and the
> [read-only MCP tools](../ai-assistants/connect-an-ai-assistant.md) share that port.

- **Keep it private.** Publish it on `127.0.0.1`, as in the examples, and reach it over SSH or a VPN.
- **Or put it behind authentication.** Place it behind a reverse proxy that signs users in and encrypts the connection.
- **Never expose it directly to the internet.**
- **On Kubernetes, block it from other pods** with a NetworkPolicy, and open it with `kubectl port-forward`, or
  publish it through an authenticating proxy. See [security on Kubernetes](../deployment/kubernetes.md#security). The
  policy only works where the cluster enforces NetworkPolicies; check that yours does.

## Protect the database

- Allow only the agents, the dashboard, and your administrators to connect, with strong credentials and firewall rules.
- Give the agents a user of their own that can't delete data or change tables: every monitored container holds its
  connection string. See [give the agents their own user](set-up-the-database.md#give-the-agents-their-own-user).
- Keep connection strings out of source code and screenshots.
- Require encryption: see [protect the connection](set-up-the-database.md#protect-the-connection).

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

Sounding keeps 24 hours of data by default, and the dashboard deletes what is older. While no dashboard runs, old data
remains; see [history](../concepts/samples-and-history.md#history). Copies made outside the database aren't deleted:
database backups and replicas, downloaded captures, and screenshots. Apply your own rules to those.

## Next steps

- [Upgrade Sounding](upgrade-sounding.md)
- [Set up the database](set-up-the-database.md)
- [Capture packets](../dashboard/capture-packets.md)
