---
title: Investigate network activity
description: See who a container talks to, which requests are slow or failing, which ports are open, and whether the network has problems.
---

The Network page answers three questions about a container: **who does it talk to**, **what does it accept**, and **is
anything failing**. On a host, it shows the same for all its containers.

![The Network page of a container](../../assets/container-network.png)

## See who it talks to

**Connections** lists every connection the container made or accepted in the selected time range, up to the last hour:
the destination and the name it looked up, the process behind it, what it asked for (a web address, or the server name
of an encrypted connection), and the data sent each way.

Sounding follows about 6,000 connections per container at once. If a container has more, the panel's heading says how
much traffic is in none of the listed connections.

The **Flags** column shows where each connection goes:

| Flag | Meaning |
|---|---|
| **Same network** | Another container on one of its networks. |
| **Host** | The host itself, or a port published on it. |
| **Private** | An address in your network, outside the container's own networks. |
| **Internet** | A public address. |
| **New destination** | First contacted in the last 15 minutes. |

A container that should only talk to its database shouldn't have internet connections. **Internet vs internal** shows
how much traffic goes each way over time.

## Find slow or failing requests

**Requests** shows the requests the container made or answered in the last minute, per destination and operation: how
many per second, how many failed, and how long the answers took (the median, and the time 95 in 100 answers came
within). Sounding recognizes supported protocols even on unusual ports. Encrypted requests, such as HTTPS, can't be
read. See the [supported protocols](#supported-protocols).

### Supported protocols

Sounding recognizes these protocols from the traffic itself, including on unusual ports: HTTP/1, PostgreSQL, MySQL,
SQL Server, MongoDB, Cassandra, Redis, Memcached, LDAP, Kafka, MQTT, AMQP (RabbitMQ), SMTP, FTP, POP3, and IMAP.

For encrypted connections, **Connections** can still show the server name the container asked for.

> [!TIP]
> **Per connection** averages decoded requests per observed connection in the last minute. A value of 1 means one
> request per observed connection in that window; it doesn't prove that a new connection was opened for every request.
> Check connection lifetimes and handshake times before diagnosing a lack of connection reuse.

## Check open ports

**Listening ports** lists the ports the container accepts connections on, and who connects to them:

- **host :8088** means the port is published on the host's port 8088, so it's reachable from outside the machine. If
  the badge is missing, the port may still be published: Sounding can't always tell.
- **internet** means clients connected from public addresses.
- **New** means the port opened in the last 15 minutes.

A port you don't expect, especially a new one, is worth checking.

## Look for network problems

If traffic looks normal but users see errors, check these charts:

- **TCP health**: **Resent** data, **Connections reset**, and **Failed connections**. A rising count points to a network
  problem, or to a service that refuses connections.
- **UDP errors**: messages lost, or sent to a port nobody listens on.
- **DNS** and **DNS reply time**: failed and slow name lookups. Every new connection to a name waits for its lookup.
  **DNS lookups** lists the names, and which ones don't exist.
- **Connection tracking**: the system's table of connections for its firewall. When it's full, new connections are
  dropped.

**Interfaces and connection states** shows each network interface and the container's connections by state. A growing
number of `CLOSE_WAIT` connections usually means a program isn't closing connections the other side already closed.

## See the map

At the bottom of the page, the **Map** shows who talks to whom: the host, its networks, and its containers, with a line
for each pair that exchanged traffic in the selected time range. Dots move from the side that opens the connection.
Select a container to open its network page.
On a host or container page, connected containers on other hosts remain visible as peers outside the selected host's box.

The map opens at a readable size. Connections are laid out left to right, with hosts stacked vertically.
Drag to move around the map, and pinch or hold Ctrl and scroll to zoom. When the map uses the whole page, scrolling
zooms on its own. Use the buttons in the corner to zoom in and out, or to show the whole map at once.

A thicker line carries more traffic. Point at a line to see which containers and ports it joins and how much traffic
it carries; select it to highlight it and the two cards it joins, and select an empty spot to clear the
highlight. The internet and the host itself have cards of their own colour, so you can see at a glance what talks to
the outside.

![The network map of a host](../../assets/host-network-map.gif)

The map shows the traffic Sounding saw, not every connection your network allows. Sounding's own traffic isn't drawn.

## When details are missing

Connections, requests, DNS lookups, and the map come from reading the container's traffic. If the page says
**Packet capture off**, Sounding can't read it, and shows only open connections, without traffic or requests. See
[allow packet inspection and process details](../operate/allow-packet-inspection-and-process-details.md).

## Next steps

- [Capture packets](capture-packets.md)
- [Network in the metrics glossary](../reference/metrics.md#network)
