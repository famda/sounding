---
title: Getting Started
description: Add two lines to your Dockerfile, pass one environment variable, and see your container live in the dashboard.
---

Adding Sounding to an application takes **two lines in its Dockerfile** and **one environment variable**. There is no
host agent to install, no sidecar to deploy, and no Docker socket to mount.

**You need:** Docker Compose v2 and x86-64 Linux containers, on Linux or Docker Desktop (see
[requirements](../reference/requirements.md)).

## 1. Add the agent to your image

Copy `/opt/sounding` into your `Dockerfile` and start your application through `/opt/sounding/run`.

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

Your application keeps its user, arguments, signals, exit code, and logs—and keeps running normally even if the
database is unreachable.

## 2. Connect your application and start the dashboard

Add a database password to `.env` next to your `compose.yaml` (use letters and numbers, and keep `.env` out of version
control):

```dotenv title=".env"
SOUNDING_DB_PASSWORD=replacewithastrongpassword
```

In `compose.yaml`, pass `SOUNDING_CONNECTION_STRING` to your service (replace `your-app` with your service name,
keeping its existing settings), and add the `dashboard` and `db` services:

```yaml title="compose.yaml"
services:
  your-app:
    build: .
    # Keep your existing ports, volumes, and other settings.
    environment:
      SOUNDING_CONNECTION_STRING: "Host=db;Database=sounding;Username=sounding;Password=${SOUNDING_DB_PASSWORD}"
      SOUNDING_HOST_DISPLAY_NAME: my-host

  dashboard:
    image: famda/sounding:dashboard-latest
    ports:
      - "127.0.0.1:8080:8080"
    environment:
      SOUNDING_CONNECTION_STRING: "Host=db;Database=sounding;Username=sounding;Password=${SOUNDING_DB_PASSWORD}"
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
> **Already have PostgreSQL?** Skip `db` and `sounding-db`—just pass your database's connection string in
> `SOUNDING_CONNECTION_STRING` on `your-app` and `dashboard`. Sounding creates its tables automatically on first
> connect. Inside a container, `localhost` is the container itself, so use a reachable host name. See
> [set up the database](../operate/set-up-the-database.md).

## 3. Build and open the dashboard

Start your stack:

```sh
docker compose up -d --build
```

Open <http://localhost:8080>. Your host and container appear within a few seconds; with a single host, the dashboard
opens it directly. Select your container to inspect its CPU, memory, disk, and network against its limits.

If your container doesn't appear, see
[My container doesn't appear](../troubleshooting/troubleshoot.md#my-container-doesnt-appear).

> [!WARNING]
> The dashboard has no sign-in. Keep it on `127.0.0.1` as above, or put it behind your own authentication, before others
> can reach it. See [secure Sounding](../operate/secure-sounding.md).

## Next steps

- [Monitor more containers and hosts](monitor-more-containers.md)
- [Find your way around the dashboard](../dashboard/find-your-way-around.md)
- [Show the host's network, disks, and GPUs](../operate/show-host-network-disk-and-gpu.md)
