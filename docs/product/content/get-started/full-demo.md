---
title: Try a standalone demo
description: Spin up a disposable example with Sounding, PostgreSQL, a web server, and a client.
---

Don't have an application handy right now? Spin up a disposable stack with the dashboard, PostgreSQL, a web server, and
a client container—then remove everything with one command.

Ready to monitor your own application instead? Go straight to
[add Sounding to your application](getting-started.md).

**You need:** Docker Compose 2.17 or later and x86-64 Linux containers, on Linux or Docker Desktop.

## 1. Create the demo

In a new, empty folder, create a `.env` file with a password of your choice using letters and numbers. Keep the file
out of version control.

```dotenv title=".env"
SOUNDING_DB_PASSWORD=replacewithastrongpassword
```

Save this as `compose.yaml` beside the `.env` file:

```yaml title="compose.yaml"
name: sounding-demo

services:
  db:
    image: postgres:18-alpine
    environment:
      POSTGRES_DB: sounding
      POSTGRES_USER: sounding
      POSTGRES_PASSWORD: ${SOUNDING_DB_PASSWORD}
    volumes:
      - db:/var/lib/postgresql
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U sounding -d sounding"]
      interval: 2s
      timeout: 3s
      retries: 30

  dashboard:
    image: famda/sounding:dashboard-latest
    ports: ["127.0.0.1:8080:8080"]
    environment:
      SOUNDING_CONNECTION_STRING: "Host=db;Database=sounding;Username=sounding;Password=${SOUNDING_DB_PASSWORD}"
    depends_on:
      db: { condition: service_healthy }

  web:
    build:
      dockerfile_inline: |
        FROM nginx:1.29
        COPY --from=famda/sounding:agent-latest /opt/sounding /opt/sounding
        ENTRYPOINT ["/opt/sounding/run", "/docker-entrypoint.sh"]
        CMD ["nginx", "-g", "daemon off;"]
    environment:
      SOUNDING_CONNECTION_STRING: "Host=db;Database=sounding;Username=sounding;Password=${SOUNDING_DB_PASSWORD}"
      SOUNDING_HOST_DISPLAY_NAME: demo-host

  client:
    build:
      dockerfile_inline: |
        FROM busybox:1.37
        COPY --from=famda/sounding:agent-latest /opt/sounding /opt/sounding
        ENTRYPOINT ["/opt/sounding/run"]
        CMD ["sh", "-c", "while true; do wget -q -O /dev/null http://web/; sleep 2; done"]
    environment:
      SOUNDING_CONNECTION_STRING: "Host=db;Database=sounding;Username=sounding;Password=${SOUNDING_DB_PASSWORD}"
      SOUNDING_HOST_DISPLAY_NAME: demo-host

volumes:
  db:
```

## 2. Start and explore

From the same folder, build and start the demo:

```sh
docker compose up -d --build
```

Open <http://localhost:8080>. Once the agents connect, the dashboard shows **demo-host** and the **web** and
**client** containers. The client requests the web server every two seconds.

Select a container to explore its metrics. See [find your way around the dashboard](../dashboard/find-your-way-around.md)
for help navigating the charts.

> [!WARNING]
> The dashboard has no built-in authentication. This example binds it to localhost; keep it private.

## 3. Remove the demo

From the demo folder, stop the demo and remove its database volume:

```sh
docker compose down --volumes
```

This permanently deletes the demo's stored metrics. It does not remove the images.

## Next steps

- [Add Sounding to your application](getting-started.md)
- [Find your way around the dashboard](../dashboard/find-your-way-around.md)
