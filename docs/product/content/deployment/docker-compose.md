---
title: Set up Sounding with Docker Compose
description: Bring your first application into Sounding, open its live figures, and make your first check.
---

Copy the agent into your service’s image, start its existing command through the Sounding launcher, and point the
agent at PostgreSQL. This example runs the database and dashboard alongside your service. Additional services can
use the same database and dashboard.

There is no host agent to install, no sidecar to deploy, and no Docker socket to mount.

**Just exploring?** [Try the Docker demo](../get-started/docker-demo.md) for a ready-made application instead.

**You need:**

- An application that already builds and runs in Docker Compose.
- Docker Compose v2 and x86-64 Linux containers, on Linux or Docker Desktop. See
  [requirements](../get-started/getting-started.md#requirements).

## 1. Add the agent to your image

Copy `/opt/sounding` into your `Dockerfile` and start your application through `/opt/sounding/run`. Keep your
existing build steps and dependencies; the examples below show where Sounding fits, not a replacement application.

**If your Dockerfile uses `CMD`** (without an `ENTRYPOINT`), add `ENTRYPOINT ["/opt/sounding/run"]` above it:

```dockerfile title="Dockerfile"
FROM node:22-alpine
WORKDIR /app
COPY . .
COPY --from=famda/sounding:agent-latest /opt/sounding /opt/sounding
ENTRYPOINT ["/opt/sounding/run"]
CMD ["node", "server.js"]
```

**If your Dockerfile already sets `ENTRYPOINT`**, put `"/opt/sounding/run"` at the start of the list. For example, if it
currently starts `dotnet MyApp.dll`, keep your existing build stages and runtime instructions, and end the runtime
stage with:

```dockerfile title="Dockerfile"
COPY --from=famda/sounding:agent-latest /opt/sounding /opt/sounding
ENTRYPOINT ["/opt/sounding/run", "dotnet", "MyApp.dll"]
```

> [!TIP]
> Wrapping a third-party image (such as `redis` or `nginx`) whose entrypoint isn't in your `Dockerfile`? Pull and
> inspect it first:
>
> ```sh
> docker pull redis:8.2
> ```
>
> ```sh
> docker inspect --format '{{json .Config.Entrypoint}} {{json .Config.Cmd}}' redis:8.2
> ```
>
> Put `/opt/sounding/run` before the original entrypoint, and keep the original `CMD`. This preserves the image's
> startup script as well as the application it starts.

Your application keeps its user, arguments, signals, exit code, and logs—and keeps running normally even if the
database is unreachable.

## 2. Connect your application and start the dashboard

Add a database password to `.env` next to your `compose.yaml` (use letters and numbers, and keep `.env` out of version
control):

```dotenv title=".env"
SOUNDING_DB_PASSWORD=replacewithastrongpassword
```

In `compose.yaml`, define the connection string once in `x-sounding` and pass it to your service with
`<<: *sounding` (replace `your-app` with your service name, keeping its existing settings). Then add the `dashboard`
and `db` services:

```yaml title="compose.yaml"
x-sounding: &sounding
  SOUNDING_CONNECTION_STRING: "Host=db;Database=sounding;Username=sounding;Password=${SOUNDING_DB_PASSWORD}"

services:
  your-app:
    build: .
    # Keep your existing ports, volumes, and other settings.
    environment:
      <<: *sounding
      SOUNDING_HOST_DISPLAY_NAME: my-host

  dashboard:
    image: famda/sounding:dashboard-latest
    ports: ["127.0.0.1:8080:8080"]
    environment:
      <<: *sounding
    depends_on:
      db:
        condition: service_healthy
    restart: unless-stopped

  db:
    image: postgres:18-alpine
    environment:
      POSTGRES_DB: sounding
      POSTGRES_USER: sounding
      POSTGRES_PASSWORD: ${SOUNDING_DB_PASSWORD}
    volumes:
      - sounding-db:/var/lib/postgresql
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U sounding -d sounding"]
      interval: 2s
      timeout: 3s
      retries: 30
    restart: unless-stopped

volumes:
  sounding-db:
```

`SOUNDING_HOST_DISPLAY_NAME` sets the readable name shown for this machine. If `your-app` defines custom `networks`,
include `default` or attach `db` to the same network so the agent can reach it.

> [!TIP]
> **Already have PostgreSQL?** Skip the `db` service and `sounding-db` volume, and remove the dashboard's
> `depends_on` block. Set `SOUNDING_CONNECTION_STRING` in `x-sounding` to your database's connection string; every
> service that uses `<<: *sounding` picks it up. Sounding creates its tables automatically on first connect. Inside a
> container, `localhost` is the container itself, so use a reachable host name. See
> [set up the database](../operate/set-up-the-database.md).

## 3. Build and open the dashboard

Start your stack:

```sh
docker compose up -d --build
```

Open <http://localhost:8080>. Your host and container appear within a few seconds; with a single host, the dashboard
opens it directly.

**Check that it is working:**

1. Find the host named **my-host**, as set in the Compose example.
2. Select your container and look for live CPU and memory figures.
3. Check that the header says **Live**. Wait a few seconds for the charts to build up.

On Docker Desktop, the host is Docker's Linux virtual machine, not your Windows or macOS machine.

### Make your first check

Use the application as you normally would: send a request or run a small job. Open the container's **CPU** or
**Memory** page, choose **1m**, and hover over the chart to see its figures at that moment.

You now have a starting point for a real question: is memory growing, is the container reaching a CPU limit, or
which process is busy? [Find your way around the dashboard](../dashboard/find-your-way-around.md) explains how to
follow those clues.

If your container doesn't appear, start with
[My container doesn't appear](../troubleshooting/troubleshoot.md#my-container-doesnt-appear). If it appears but a
chart says **Not available**, check [what needs extra access](../get-started/getting-started.md#optional-access);
missing data isn't a zero reading.

> [!WARNING]
> The dashboard has no sign-in. Keep it on `127.0.0.1` as above, or put it behind your own authentication, before others
> can reach it. See [secure Sounding](../operate/secure-sounding.md).

## Next steps

- [Monitor containers and hosts](../get-started/monitor-containers-and-hosts.md)
- [Find your way around the dashboard](../dashboard/find-your-way-around.md)
- [Show the host's network, disks, and GPUs](../operate/show-host-network-disk-and-gpu.md)
- [How Sounding works](../concepts/how-sounding-works.md)
