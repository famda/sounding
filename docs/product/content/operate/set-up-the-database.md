---
title: Set up the database
description: Prepare the PostgreSQL database that Sounding's agents and dashboard share, and connect to it safely.
---

Use this guide to connect an existing PostgreSQL database or estimate storage for your deployment.
Sounding’s agents and dashboard share one database. It can run anywhere your containers can reach: on the
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
Their connection strings may differ in options, such as the user name.

```text
Host=db.example.com;Port=5432;Database=sounding;Username=sounding;Password=choose-a-password
```

The format is that of Npgsql, the PostgreSQL client Sounding uses; see the
[list of connection string parameters](https://www.npgsql.org/doc/connection-string-parameters.html). If your password
contains `;` or `=`, put it in single quotes: `Password='pa;ss'`.

> [!IMPORTANT]
> Inside a container, `localhost` is the container itself, not your machine. Use a host name that every container can
> reach: the database's service name in the same Compose file, its Service name on Kubernetes, or its address on your
> network.

Keep connection strings out of your source code: put them in an `.env` file excluded from version control, or in your
platform's secret store.

## Protect the connection

Sounding encrypts the connection whenever the database supports it. The `SSL Mode` setting in each connection string
decides what it accepts:

| `SSL Mode` | Encrypted | Server checked | Use |
|---|---|---|---|
| `Prefer` (default) | When the server supports it | No | A database on a network you trust. |
| `Require` | Always | No | Refuses unencrypted connections, but doesn't stop a machine pretending to be your server. |
| `VerifyFull` | Always | Yes: its certificate must be signed by a CA you trust and name the host you connect to | A database reached over a network you don't control, such as a managed service. |

For `VerifyFull`, the agent needs the certificate of the CA that signed the server's certificate. It looks in the CA
store of the image it runs in, which small images such as `busybox` don't have. To be sure, give the CA certificate to
every container that has the agent, for example from a mounted file, and name it in the connection string:

```text
Host=db.example.com;Database=sounding;Username=sounding;Password=choose-a-password;SSL Mode=VerifyFull;Root Certificate=/etc/sounding/db-ca.pem
```

Your database provider publishes its CA certificate. Check that each container connects after the change: one that
can't verify the server stops reporting, and its messages say why.

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
