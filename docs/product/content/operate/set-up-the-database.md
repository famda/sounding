---
title: Set up the database
description: Prepare the PostgreSQL database that Sounding's agents and dashboard share, and connect to it safely.
---

Sounding's agents and dashboard share one PostgreSQL database. It can run anywhere your containers can reach: on the
same machine, on another server, or in the cloud.

## Create the database

You need **PostgreSQL 14 or later**.

Create a database and a user for Sounding, and make the user the database's owner. Sounding creates its tables the first
time an agent or the dashboard connects, so the user needs permission to create tables as well as to read and write
data.

```sql
CREATE USER sounding WITH PASSWORD 'choose-a-password';
CREATE DATABASE sounding OWNER sounding;
```

A dedicated database keeps Sounding's tables apart from your application's data, and makes it easy to
[remove Sounding](remove-sounding.md) later.

## Connect to it

Point every agent and the dashboard to the same PostgreSQL database using the `SOUNDING_CONNECTION_STRING` setting.
Their connection strings may differ in options: for example, the dashboard can use `SSL Mode=Require`.

```text
Host=db.example.com;Port=5432;Database=sounding;Username=sounding;Password=choose-a-password
```

The format is that of Npgsql, the PostgreSQL client Sounding uses; see the
[list of connection string parameters](https://www.npgsql.org/doc/connection-string-parameters.html). If your password
contains `;` or `=`, put it in single quotes: `Password='pa;ss'`.

> [!IMPORTANT]
> Inside a container, `localhost` is the container itself, not your machine. Use a host name that every container can
> reach: the database's service name in the same Compose file, or its address on your network.

Keep connection strings out of your source code: put them in an `.env` file excluded from version control, or in your
platform's secret store.

## Protect the connection

> [!WARNING]
> The agent connects to PostgreSQL **without encryption**, and can't connect to a server that requires it. Don't let
> that traffic cross a network you don't trust.

If the database is on another machine, use one of these:

- **A private network** between your containers and the database, such as a Docker network, a private cloud network,
  or a VPN.
- **A tunnel or proxy** next to your containers that adds encryption on the way to the database.

The dashboard can use an encrypted connection: add `SSL Mode=Require` to its connection string.

## Connection poolers

Each agent keeps a single connection open. A connection pooler such as PgBouncer works in **session** mode. Don't use
**transaction** mode: the agents on a host coordinate through their connections, which transaction mode doesn't keep.

## How big it gets

Each container writes about one row of figures per second, and the database keeps 24 hours of history by default.
That takes about 50 MB of disk per container, about 5 MB more for each process it runs, and about 70 MB per host: a
hundred containers running ten processes each need about 10 GB. To use less, keep less history or measure less often; see
[configuration settings](../reference/configuration.md#sampling-and-history).

If the database is unreachable, your applications keep running, but figures taken in the meantime are lost.

## Next steps

- [Show the host's network, disks, and GPUs](show-host-network-disk-and-gpu.md)
- [Secure Sounding](secure-sounding.md)
- [Configuration settings](../reference/configuration.md)
